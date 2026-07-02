using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Prism.Host.Media;

/// <summary>
/// Реализует VOD HLS «на лету». Плейлист рассчитывается заранее по длительности
/// файла (чтобы браузер мог перематывать в любую точку). Сегменты производят
/// сессии — каждый процесс ffmpeg своим HLS-муксером выдаёт ограниченный по времени
/// диапазон сегментов (см. <see cref="PlayerOptions.SessionMinutes"/>) и завершается.
/// Внутри сессии звук бесшовный; разрыв возможен лишь на границе сессий или при
/// перемотке. Старые сессии вытесняются по LRU.
/// </summary>
public sealed class HlsTranscoder : IAsyncDisposable
{
    // Сколько сессий на файл держать одновременно. Нужно ≥2, чтобы на стыке сессий
    // (или при буферизации плеера вперёд) «хвост» предыдущей сессии оставался
    // доступен, пока подхватывается следующая.
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

    /// <summary>Строит статический VOD-плейлист m3u8 для заданного файла и аудиодорожки.</summary>
    public string BuildPlaylist(MediaInfo info, int audio)
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
            sb.Append(CultureInfo.InvariantCulture, $"segment/{i}.ts?audio={audio}\n");
        }

        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    /// <summary>
    /// Отдаёт сегмент <paramref name="index"/>: при необходимости поднимает сессию,
    /// дожидается готовности файла сегмента и копирует его в <paramref name="destination"/>.
    /// </summary>
    public async Task WriteSegmentAsync(MediaItem item, int index, int audio, Stream destination, CancellationToken ct)
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

    /// <summary>
    /// Находит сессию, которая выдаст сегмент <paramref name="index"/>, или создаёт её.
    /// </summary>
    private async Task<TranscodeSession> AcquireSegmentAsync(MediaItem item, int index, int audio, CancellationToken ct)
    {
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
                if (s.AudioTrack != audio || !s.Covers(index)) continue;      // другая дорожка/диапазон
                if (s.SegmentReady(index)) { pick = s; break; }               // уже готов
                if (!s.HasExited && index <= s.HighestProduced() + LookaheadGap) pick = s; // скоро будет
            }

            if (pick is null)
            {
                var audioEncoder = await _audioEncoder.Value;
                pick = StartSession(item, index, audio, audioEncoder);
                list.Add(pick);
                _logger.LogDebug("Запущена сессия транскодирования {file}: сегменты [{from}..{to}) audio={audio}",
                    item.FileName, pick.StartIndex, pick.EndIndex, audio);
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

    private void EvictExtraSessions(List<TranscodeSession> list)
    {
        while (list.Count > MaxSessionsPerMedia)
        {
            // Вытесняем самую давно не используемую сессию.
            var lru = list[0];
            foreach (var s in list)
                if (s.LastTouch < lru.LastTouch) lru = s;
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

    private TranscodeSession StartSession(MediaItem item, int startIndex, int audio, string audioEncoder)
    {
        var info = item.Info!;
        var total = SegmentCount(info);
        var endIndex = Math.Min(startIndex + SessionSegments, Math.Max(startIndex + 1, total));

        var dir = Path.Combine(_tempRoot, $"{item.Id}-{startIndex}-{Guid.NewGuid():N}");
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

        a.Add("-map"); a.Add("0:v:0");
        // Выбранная аудиодорожка (индекс среди аудиопотоков); ? — не падать, если её нет.
        var audioTrack = audio >= 0 && audio < info.AudioTracks.Count ? info.AudioTracks[audio] : null;
        if (info.HasAudio) { a.Add("-map"); a.Add($"0:a:{Math.Max(0, audio)}?"); }

        // Видео в целевой кодек H264/H265 с ключевыми кадрами ровно на сетке сегментов.
        var (vcodec, extra) = VideoEncoderArgs();
        a.Add("-c:v"); a.Add(vcodec);
        foreach (var e in extra) a.Add(e);
        a.Add("-preset"); a.Add(_options.EncoderPreset);
        a.Add("-crf"); a.Add(_options.Crf.ToString(CultureInfo.InvariantCulture));
        a.Add("-pix_fmt"); a.Add("yuv420p");
        a.Add("-force_key_frames");
        a.Add($"expr:gte(t,n_forced*{SegmentLength.ToString("0.###", CultureInfo.InvariantCulture)})");

        AddAudioArgs(a, info.HasAudio, audioTrack?.Channels ?? info.AudioChannels, audioEncoder);

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
                _logger.LogDebug("ffmpeg[{id}@{idx}]: {line}", item.Id, startIndex, e.Data);
        };

        if (!proc.Start())
            throw new InvalidOperationException("Не удалось запустить ffmpeg.");
        proc.BeginErrorReadLine();

        return new TranscodeSession(startIndex, endIndex, audio, dir, proc);
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
                        _logger.LogDebug("Убрана простаивающая сессия [{from}..{to}) audio={a}",
                            s.StartIndex, s.EndIndex, s.AudioTrack);
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
                    audioTrack = s.AudioTrack,
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
