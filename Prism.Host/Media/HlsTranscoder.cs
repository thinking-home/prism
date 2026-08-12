using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Prism.Host.Media;

/// <summary>
/// Реализует VOD HLS «на лету» с master-плейлистом: видео и каждая аудиодорожка —
/// отдельные сегментные потоки (рендиции), поэтому плеер переключает дорожки
/// локально, а смена аудио не перекодирует видео. Плейлисты рассчитываются заранее
/// по длительности файла (браузер может перематывать в любую точку). Сегменты
/// производят сессии — каждый процесс ffmpeg своим HLS-муксером выдаёт ограниченный
/// по времени диапазон сегментов одного потока (см.
/// <see cref="PlayerOptions.SessionMinutes"/>) и завершается. Внутри сессии поток
/// бесшовный; разрыв возможен лишь на границе сессий или при перемотке. Старые
/// сессии вытесняются по LRU.
/// </summary>
public sealed class HlsTranscoder : IAsyncDisposable
{
    // Сколько сессий КАЖДОГО вида (видео / аудио) держать на файл одновременно.
    // Нужно ≥2, чтобы на стыке сессий (или при буферизации плеера вперёд) «хвост»
    // предыдущей сессии оставался доступен, пока подхватывается следующая.
    private const int MaxSessionsPerMedia = 3;

    // Если запрошенный сегмент ещё не готов, но сессия его покрывает и фронт
    // производства недалеко — ждём её; иначе считаем это перемоткой и стартуем
    // новую сессию прямо с этого сегмента (чтобы перемотка была быстрой).
    private const int LookaheadGap = 12;

    private readonly FFTools _tools;
    private readonly PlayerOptions _options;
    private readonly ILogger<HlsTranscoder> _logger;

    private readonly ConcurrentDictionary<string, List<TranscodeSession>> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly string _tempRoot;
    private long _touch;

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _reaper;

    // Выбор аудиокодировщика делается один раз и кэшируется: libfdk_aac заметно
    // качественнее встроенного aac, но в большинстве сборок (в т.ч. Homebrew) его
    // нет из-за лицензии, поэтому используется откат на встроенный aac.
    private readonly Lazy<Task<string>> _audioEncoder;

    public HlsTranscoder(FFTools tools, PlayerOptions options, ILogger<HlsTranscoder> logger)
    {
        _tools = tools;
        _options = options;
        _logger = logger;
        _audioEncoder = new Lazy<Task<string>>(ResolveAudioEncoderAsync);

        _tempRoot = Path.Combine(Path.GetTempPath(), "prism-hls");
        TryCleanTempRoot();
        Directory.CreateDirectory(_tempRoot);

        _reaper = Task.Run(ReaperLoopAsync);
    }

    private double SegmentLength => Math.Max(1, _options.SegmentSeconds);

    private int SessionSegments =>
        Math.Max(1, (int)Math.Ceiling(Math.Max(1, _options.SessionMinutes) * 60.0 / SegmentLength));

    public int SegmentCount(MediaInfo info) =>
        Math.Max(1, (int)Math.Ceiling(info.DurationSeconds / SegmentLength));

    /// <summary>
    /// Строит master-плейлист: вариант видео + рендиции аудио (#EXT-X-MEDIA:TYPE=AUDIO)
    /// и текстовых субтитров (TYPE=SUBTITLES). Из него плееры (hls.js, ExoPlayer,
    /// Safari) узнают о дорожках и переключают их локально, без пересборки потока.
    /// </summary>
    public string BuildMasterPlaylist(MediaInfo info)
    {
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:4\n");

        var hasAudio = info.HasAudio && info.AudioTracks.Count > 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; hasAudio && i < info.AudioTracks.Count; i++)
        {
            var t = info.AudioTracks[i];
            sb.Append("#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\"");
            sb.Append($",NAME=\"{RenditionName(t.Title, t.Language, "Audio", i, names)}\"");
            if (!string.IsNullOrWhiteSpace(t.Language)) sb.Append($",LANGUAGE=\"{t.Language.Replace('"', '\'')}\"");
            sb.Append(i == 0 ? ",DEFAULT=YES,AUTOSELECT=YES" : ",DEFAULT=NO,AUTOSELECT=YES");
            sb.Append(CultureInfo.InvariantCulture, $",URI=\"audio/{t.Index}.m3u8\"\n");
        }

        var textSubs = info.SubtitleTracks.Where(s => s.TextBased).ToArray();
        var subNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < textSubs.Length; i++)
        {
            var t = textSubs[i];
            sb.Append("#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\"");
            sb.Append($",NAME=\"{RenditionName(t.Title, t.Language, "Subtitles", i, subNames)}\"");
            if (!string.IsNullOrWhiteSpace(t.Language)) sb.Append($",LANGUAGE=\"{t.Language.Replace('"', '\'')}\"");
            sb.Append(",DEFAULT=NO,AUTOSELECT=NO");
            sb.Append(CultureInfo.InvariantCulture, $",URI=\"subs/{t.Index}.m3u8\"\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-STREAM-INF:BANDWIDTH={EstimateBandwidth(info)}");
        if (info.Width > 0 && info.Height > 0)
            sb.Append(CultureInfo.InvariantCulture, $",RESOLUTION={info.Width}x{info.Height}");
        sb.Append($",CODECS=\"{CodecsAttribute(hasAudio)}\"");
        if (hasAudio) sb.Append(",AUDIO=\"audio\"");
        if (textSubs.Length > 0) sb.Append(",SUBTITLES=\"subs\"");
        sb.Append("\nvideo.m3u8\n");
        return sb.ToString();
    }

    /// <summary>Строит статический VOD-плейлист видеодорожки (сегменты segment/N.ts).</summary>
    public string BuildVideoPlaylist(MediaInfo info) =>
        BuildMediaPlaylist(info, i => $"segment/{i}.ts");

    /// <summary>Строит статический VOD-плейлист аудиодорожки (URI относительно /hls/{id}/audio/).</summary>
    public string BuildAudioPlaylist(MediaInfo info, int track) =>
        BuildMediaPlaylist(info, i => $"{track}/{i}.ts");

    /// <summary>
    /// Строит VOD-плейлист дорожки субтитров: WebVTT-сегменты той же сетки, что и
    /// видео/аудио (URI относительно /hls/{id}/subs/). Каждый сегмент несёт
    /// X-TIMESTAMP-MAP — это требование ExoPlayer для субтитров в HLS.
    /// </summary>
    public string BuildSubtitlePlaylist(MediaInfo info, int track) =>
        BuildMediaPlaylist(info, i => $"{track}/{i}.vtt");

    /// <summary>Длина HLS-сегмента в секундах (общая сетка всех дорожек).</summary>
    public double SegmentLengthSeconds => SegmentLength;

    private string BuildMediaPlaylist(MediaInfo info, Func<int, string> segmentUri)
    {
        var seg = SegmentLength;
        var total = info.DurationSeconds;
        var count = SegmentCount(info);

        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:3\n");
        sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{(int)Math.Ceiling(seg)}\n");
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
        sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");

        for (var i = 0; i < count; i++)
        {
            var start = i * seg;
            var len = Math.Min(seg, total - start);
            if (len <= 0) len = seg;
            sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:{len.ToString("0.000", CultureInfo.InvariantCulture)},\n");
            sb.Append(segmentUri(i)).Append('\n');
        }

        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    // Имя рендиции для меню плеера: Title → Language → «Audio N»; уникально в
    // группе (одинаковые имена плееры сливают в один пункт), кавычки — в апострофы.
    private static string RenditionName(string? title, string? language, string fallback, int ordinal, HashSet<string> used)
    {
        var name = !string.IsNullOrWhiteSpace(title) ? title
            : !string.IsNullOrWhiteSpace(language) ? language
            : $"{fallback} {ordinal + 1}";
        name = name.Replace('"', '\'');
        if (!used.Add(name))
        {
            var n = 2;
            string candidate;
            do { candidate = $"{name} ({n++})"; } while (!used.Add(candidate));
            name = candidate;
        }
        return name;
    }

    // Строка CODECS для STREAM-INF: должна соответствовать VideoEncoderArgs
    // (H.264 High 4.1 / HEVC Main) и AAC-LC на выходе аудиосессий.
    private string CodecsAttribute(bool hasAudio)
    {
        var video = _options.OutputCodec.ToLowerInvariant() is "h265" or "hevc"
            ? "hvc1.1.6.L123.B0"
            : "avc1.640029";
        return hasAudio ? video + ",mp4a.40.2" : video;
    }

    // Грубая оценка пиковой полосы. Вариант в master один, так что на выбор плеера
    // атрибут не влияет, но BANDWIDTH обязателен по спецификации.
    private long EstimateBandwidth(MediaInfo info)
    {
        var pixels = info.Width > 0 && info.Height > 0 ? (long)info.Width * info.Height : 1920L * 1080;
        return Math.Max(2_000_000, pixels * 4) + _options.AudioBitrateKbps * 1000L;
    }

    /// <summary>Отдаёт видеосегмент <paramref name="index"/> (сессия только-видео).</summary>
    public Task WriteVideoSegmentAsync(MediaItem item, int index, Stream destination, CancellationToken ct) =>
        WriteSegmentAsync(item, index, null, destination, ct);

    /// <summary>Отдаёт аудиосегмент <paramref name="index"/> дорожки <paramref name="audio"/>.</summary>
    public Task WriteAudioSegmentAsync(MediaItem item, int index, int audio, Stream destination, CancellationToken ct) =>
        WriteSegmentAsync(item, index, audio, destination, ct);

    /// <summary>
    /// Отдаёт сегмент <paramref name="index"/> потока (<paramref name="audio"/>: null —
    /// видео, иначе номер аудиодорожки): при необходимости поднимает сессию, дожидается
    /// готовности файла сегмента и копирует его в <paramref name="destination"/>.
    /// </summary>
    private async Task WriteSegmentAsync(MediaItem item, int index, int? audio, Stream destination, CancellationToken ct)
    {
        if (_tools.FfmpegPath is null)
            throw new InvalidOperationException("ffmpeg недоступен.");
        if (item.Info is null)
            throw new InvalidOperationException("Файл ещё не проанализирован.");

        // Несколько попыток: сессию, которую ждёт этот запрос, может вытеснить по LRU
        // параллельный запрос на другую позицию. Тогда просто берём/создаём заново.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var session = await AcquireSegmentAsync(item, index, audio, ct);
            var path = session.SegmentPath(index);

            if (await WaitForSegmentAsync(session, path, ct))
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
                await fs.CopyToAsync(destination, 64 * 1024, ct);
                return;
            }
        }

        throw new InvalidOperationException($"Не удалось получить сегмент {index} (ffmpeg не выдал файл).");
    }

    /// <summary>Ключ потока сессии: "v" — видео, "aN" — аудиодорожка N.</summary>
    private static string StreamKey(int? audio) => audio is null ? "v" : $"a{audio}";

    /// <summary>
    /// Находит сессию, которая выдаст сегмент <paramref name="index"/>, или создаёт её.
    /// </summary>
    private async Task<TranscodeSession> AcquireSegmentAsync(MediaItem item, int index, int? audio, CancellationToken ct)
    {
        var stream = StreamKey(audio);
        var sem = _locks.GetOrAdd(item.Id, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            // Прерванный запрос (плеер отменил загрузку при перемотке) не должен
            // трогать/вытеснять сессии, которыми пользуются активные запросы.
            ct.ThrowIfCancellationRequested();

            var list = _sessions.GetOrAdd(item.Id, _ => new List<TranscodeSession>());

            TranscodeSession? pick = null;
            foreach (var s in list)
            {
                if (s.Stream != stream || !s.Covers(index)) continue;         // другой поток/диапазон
                if (s.SegmentReady(index)) { pick = s; break; }               // уже готов
                if (!s.HasExited && index <= s.HighestProduced() + LookaheadGap) pick = s; // скоро будет
            }

            if (pick is null)
            {
                var audioEncoder = audio is null ? "" : await _audioEncoder.Value;
                pick = StartSession(item, index, audio, audioEncoder);
                list.Add(pick);
                _logger.LogDebug("Запущена сессия {stream} {file}: сегменты [{from}..{to})",
                    stream, item.FileName, pick.StartIndex, pick.EndIndex);
            }

            // Помечаем сессию самой свежей ДО вытеснения — иначе только что созданную
            // (с LastTouch=0) вытеснит же EvictExtraSessions, и запрос останется без сессии.
            pick.LastTouch = Interlocked.Increment(ref _touch);
            pick.LastAccessMs = Environment.TickCount64;
            EvictExtraSessions(list);
            return pick;
        }
        finally
        {
            sem.Release();
        }
    }

    // Видео- и аудиосессии вытесняются раздельно (лимит на каждый вид): плеер тянет
    // оба потока параллельно, и тяжёлая видеосессия не должна выбивать дешёвую
    // аудиосессию той же позиции (и наоборот).
    private void EvictExtraSessions(List<TranscodeSession> list)
    {
        EvictExtraSessions(list, video: true);
        EvictExtraSessions(list, video: false);
    }

    private void EvictExtraSessions(List<TranscodeSession> list, bool video)
    {
        while (true)
        {
            TranscodeSession? lru = null;
            var count = 0;
            foreach (var s in list)
            {
                if ((s.Stream == "v") != video) continue;
                count++;
                if (lru is null || s.LastTouch < lru.LastTouch) lru = s;
            }
            if (count <= MaxSessionsPerMedia || lru is null) return;
            list.Remove(lru);
            _ = lru.DisposeAsync().AsTask(); // не держим лок на время kill/cleanup
        }
    }

    private static async Task<bool> WaitForSegmentAsync(TranscodeSession session, string path, CancellationToken ct)
    {
        var waitedMs = 0;
        const int stepMs = 100;
        const int maxMs = 60_000;
        while (!ct.IsCancellationRequested)
        {
            if (File.Exists(path)) return true;
            // Процесс завершился, а файла так и нет — дальше ждать бессмысленно.
            if (session.HasExited) return File.Exists(path);
            await Task.Delay(stepMs, ct);
            waitedMs += stepMs;
            if (waitedMs >= maxMs) return File.Exists(path);
        }
        return File.Exists(path);
    }

    private TranscodeSession StartSession(MediaItem item, int startIndex, int? audio, string audioEncoder)
    {
        var info = item.Info!;
        var total = SegmentCount(info);
        var endIndex = Math.Min(startIndex + SessionSegments, Math.Max(startIndex + 1, total));
        var stream = StreamKey(audio);

        var dir = Path.Combine(_tempRoot, $"{item.Id}-{stream}-{startIndex}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var start = startIndex * SegmentLength;
        var duration = (endIndex - startIndex) * SegmentLength;

        var psi = new ProcessStartInfo(_tools.FfmpegPath!)
        {
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var a = psi.ArgumentList;
        a.Add("-nostdin");
        a.Add("-hide_banner");
        a.Add("-loglevel"); a.Add("error");

        // Пейсинг чтения: быстро выдаём начальный «бёрст» (быстрый старт/перемотка),
        // затем читаем вход со скоростью ~1x — процесс не транскодирует весь диапазон
        // на полной скорости и не жжёт CPU впустую. Ограничение действует на вход,
        // поэтому идёт до -i (и до -ss — сам seek остаётся мгновенным).
        if (_options.BufferBurstSeconds > 0)
        {
            // Пейсинг на кратность максимальной скорости плеера — чтобы 2x не буксовал.
            var rate = Math.Max(1.0, _options.MaxPlaybackRate);
            a.Add("-readrate"); a.Add(rate.ToString("0.0##", CultureInfo.InvariantCulture));
            a.Add("-readrate_initial_burst");
            a.Add(_options.BufferBurstSeconds.ToString(CultureInfo.InvariantCulture));
        }

        // Быстрая перемотка к началу сессии + ограничение её длительности.
        a.Add("-ss"); a.Add(start.ToString("0.000", CultureInfo.InvariantCulture));
        a.Add("-t"); a.Add(duration.ToString("0.000", CultureInfo.InvariantCulture));
        a.Add("-i"); a.Add(item.Path);

        if (audio is null)
        {
            // Только видео в целевой кодек H264/H265, ключевые кадры ровно на сетке
            // сегментов. Аудио не мапится вовсе — оно едет отдельными рендициями.
            a.Add("-map"); a.Add("0:v:0");
            var (vcodec, extra) = VideoEncoderArgs();
            a.Add("-c:v"); a.Add(vcodec);
            foreach (var e in extra) a.Add(e);
            a.Add("-preset"); a.Add(_options.EncoderPreset);
            a.Add("-crf"); a.Add(_options.Crf.ToString(CultureInfo.InvariantCulture));
            a.Add("-pix_fmt"); a.Add("yuv420p");
            a.Add("-force_key_frames");
            a.Add($"expr:gte(t,n_forced*{SegmentLength.ToString("0.###", CultureInfo.InvariantCulture)})");
        }
        else
        {
            // Только аудио выбранной дорожки в AAC: видео не кодируется вовсе,
            // такая сессия почти бесплатна по CPU. Границы сегментов у аудио режутся
            // муксером по кадрам AAC (~6 с, не тик-в-тик с видео) — плееры сводят
            // потоки по таймстемпам, точного совпадения не требуется.
            a.Add("-map"); a.Add($"0:a:{audio.Value}");
            var track = audio.Value < info.AudioTracks.Count ? info.AudioTracks[audio.Value] : null;
            AddAudioArgs(a, hasAudio: true, track?.Channels ?? info.AudioChannels, audioEncoder);
        }

        // Глобальные таймстемпы: смещаем выход на позицию сессии, чтобы сегменты всех
        // сессий ложились на единую шкалу времени. muxdelay/muxpreload=0 убирают
        // дефолтный начальный сдвиг mpegts.
        a.Add("-output_ts_offset"); a.Add(start.ToString("0.000", CultureInfo.InvariantCulture));
        a.Add("-muxdelay"); a.Add("0");
        a.Add("-muxpreload"); a.Add("0");

        // HLS-муксер сам нарезает непрерывный поток на сегменты по сетке.
        a.Add("-f"); a.Add("hls");
        a.Add("-hls_time"); a.Add(SegmentLength.ToString("0.###", CultureInfo.InvariantCulture));
        a.Add("-hls_segment_type"); a.Add("mpegts");
        a.Add("-hls_flags"); a.Add("independent_segments+temp_file");
        a.Add("-hls_list_size"); a.Add("0");
        a.Add("-start_number"); a.Add(startIndex.ToString(CultureInfo.InvariantCulture));
        a.Add("-hls_segment_filename"); a.Add(Path.Combine(dir, "seg%d.ts"));
        a.Add(Path.Combine(dir, "index.m3u8"));

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logger.LogDebug("ffmpeg[{id}/{stream}@{idx}]: {line}", item.Id, stream, startIndex, e.Data);
        };

        if (!proc.Start())
            throw new InvalidOperationException("Не удалось запустить ffmpeg.");
        proc.BeginErrorReadLine();

        return new TranscodeSession(startIndex, endIndex, stream, dir, proc);
    }

    private void AddAudioArgs(System.Collections.ObjectModel.Collection<string> a, bool hasAudio, int channels, string audioEncoder)
    {
        if (!hasAudio) return;

        a.Add("-c:a"); a.Add(audioEncoder);
        a.Add("-b:a"); a.Add($"{_options.AudioBitrateKbps}k");
        a.Add("-ar"); a.Add(_options.AudioSampleRate.ToString(CultureInfo.InvariantCulture));

        // Сведение многоканала в стерео. Простой -ac 2 в ffmpeg использует
        // коэффициенты, при которых центр/диалоги звучат тихо и глухо, поэтому
        // для 5.1/7.1 применяем явный даунмикс с диалогами на полном уровне.
        // Каналы адресуются по индексам (c0..), что устойчиво к вариантам
        // раскладки (5.1 back / 5.1 side).
        switch (channels)
        {
            case 6: // 5.1: FL FR FC LFE BL/SL BR/SR (LFE в стерео не подмешиваем)
                a.Add("-af"); a.Add("pan=stereo|c0=c0+0.707*c2+0.707*c4|c1=c1+0.707*c2+0.707*c5");
                break;
            case 8: // 7.1: FL FR FC LFE BL BR SL SR
                a.Add("-af");
                a.Add("pan=stereo|c0=c0+0.707*c2+0.5*c4+0.5*c6|c1=c1+0.707*c2+0.5*c5+0.5*c7");
                break;
            default: // моно / стерео / нетипичные раскладки — обычное сведение
                a.Add("-ac"); a.Add("2");
                break;
        }
    }

    private (string codec, string[] extra) VideoEncoderArgs()
    {
        return _options.OutputCodec.ToLowerInvariant() switch
        {
            // HEVC; тег hvc1 — то, что ожидают Safari/HLS.
            "h265" or "hevc" => ("libx265", ["-tag:v", "hvc1"]),
            // H.264 high profile, широко совместим с hls.js и нативными плеерами.
            _ => ("libx264", ["-profile:v", "high", "-level", "4.1"]),
        };
    }

    private async Task<string> ResolveAudioEncoderAsync()
    {
        if (await _tools.HasEncoderAsync("libfdk_aac"))
        {
            _logger.LogInformation("Аудиокодировщик: libfdk_aac");
            return "libfdk_aac";
        }
        _logger.LogInformation("Аудиокодировщик: встроенный aac (libfdk_aac недоступен)");
        return "aac";
    }

    private void TryCleanTempRoot()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Не критично, если осталось от прошлого запуска.
        }
    }

    /// <summary>
    /// Фоновый уборщик: убивает сессии, к которым давно не обращались (никто не
    /// запрашивает их сегменты), чтобы простаивающие процессы ffmpeg не жгли CPU.
    /// </summary>
    private async Task ReaperLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try { await Task.Delay(3000, _shutdown.Token); }
            catch (OperationCanceledException) { break; }

            var idleMs = Math.Max(5, _options.SessionIdleSeconds) * 1000L;
            var now = Environment.TickCount64;

            foreach (var (id, sem) in _locks)
            {
                if (!_sessions.TryGetValue(id, out var list)) continue;
                if (!await sem.WaitAsync(0)) continue; // занято запросом — попробуем в следующий раз
                try
                {
                    var stale = list.Where(s => now - s.LastAccessMs > idleMs).ToArray();
                    foreach (var s in stale)
                    {
                        list.Remove(s);
                        _ = s.DisposeAsync().AsTask();
                        _logger.LogDebug("Убрана простаивающая сессия {stream} [{from}..{to})",
                            s.Stream, s.StartIndex, s.EndIndex);
                    }
                }
                finally { sem.Release(); }
            }
        }
    }

    /// <summary>Снимок активных сессий и метрик их процессов для дебаг-панели.</summary>
    public IReadOnlyList<object> DebugSnapshot()
    {
        var now = Environment.TickCount64;
        var result = new List<object>();
        foreach (var (id, list) in _sessions)
        {
            foreach (var s in list.ToArray())
            {
                result.Add(new
                {
                    mediaId = id,
                    startIndex = s.StartIndex,
                    endIndex = s.EndIndex,
                    stream = s.Stream,
                    produced = Math.Max(0, s.HighestProduced() - s.StartIndex + 1),
                    total = s.EndIndex - s.StartIndex,
                    alive = !s.HasExited,
                    pid = s.Pid,
                    memoryBytes = s.MemoryBytes,
                    cpuSeconds = s.CpuSeconds,
                    idleSeconds = Math.Round((now - s.LastAccessMs) / 1000.0, 1),
                });
            }
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { await _reaper; } catch { }

        foreach (var list in _sessions.Values)
        {
            foreach (var s in list.ToArray())
            {
                try { await s.DisposeAsync(); } catch { }
            }
        }
        _sessions.Clear();
        TryCleanTempRoot();
        _shutdown.Dispose();
    }
}
