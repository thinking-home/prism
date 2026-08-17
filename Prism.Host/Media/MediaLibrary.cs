using System.Collections.Concurrent;
using System.Text.Json;
using Prism.Common;

namespace Prism.Host.Media;

public enum PlaybackMode
{
    /// <summary>Файл ещё не разобран (ffprobe не выполнялся) — переходное состояние,
    /// метаданные появятся после фоновой доразборки. Первый член enum — это же
    /// значение по умолчанию у только что найденного файла.</summary>
    Pending,

    /// <summary>Файл браузерный — отдаётся напрямую с поддержкой HTTP Range.</summary>
    Direct,

    /// <summary>Файл транскодируется на лету в H264/H265 и отдаётся как HLS.</summary>
    Transcode,

    /// <summary>Файлу нужна перекодировка, но ffmpeg/исходный кодек недоступны.</summary>
    Unsupported,
}

public sealed class MediaItem
{
    /// <summary>Ключ содержимого («размер-хеш краёв», см. <see cref="MediaFingerprint"/>) —
    /// единственный идентификатор файла: не меняется при переименовании/переносе.</summary>
    public required string Id { get; init; }

    /// <summary>Текущий путь файла; обновляется при переезде содержимого.</summary>
    public required string Path { get; set; }

    /// <summary>Путь относительно корня своей медиапапки, прямые слеши на всех ОС.
    /// Единственный потребитель — правила автозаполнения библиотеки: их шаблоны
    /// сопоставляются именно с этим путём (относительный он затем, чтобы правила
    /// не зависели от расположения корня на конкретной машине и были одинаковы
    /// для всех хостов). К правилам попадает двумя путями: внутрипроцессно через
    /// IMediaIdentity.GetLiveFilesAsync (плагин) и полем relativePath в /api/media
    /// (внешняя библиотека-сервис). Ремапу и стримингу не нужен — они работают
    /// по id и абсолютному пути.</summary>
    public required string RelativePath { get; set; }

    public required string FileName { get; set; }
    public MediaInfo? Info { get; set; }
    public PlaybackMode Mode { get; set; }
    public string? StatusMessage { get; set; }

    /// <summary>Найденные файлы субтитров (см. <see cref="MediaLibrary.Scan"/>).
    /// Обновляются при каждом скане, поэтому подложенный файл виден без перезапуска.</summary>
    public IReadOnlyList<TrackFile> ExternalSubtitles { get; set; } = [];

    /// <summary>Найденные файлы отдельных аудиодорожек (например, озвучка).</summary>
    public IReadOnlyList<TrackFile> ExternalAudio { get; set; } = [];

    /// <summary>Разбор внешних аудиофайлов (путь → кодек/каналы/язык), заполняется
    /// библиотекой лениво: без него не выбрать правильный даунмикс 5.1 → стерео.</summary>
    public IReadOnlyDictionary<string, AudioTrack> ExternalAudioInfo { get; set; } =
        new Dictionary<string, AudioTrack>();

    /// <summary>
    /// Все аудиодорожки файла: вшитые (из ffprobe) и внешние файлы, дописанные в
    /// конец — номера вшитых от этого не меняются.
    /// </summary>
    public IReadOnlyList<AudioTrack> AudioTracks
    {
        get
        {
            var embedded = Info?.AudioTracks ?? [];
            if (ExternalAudio.Count == 0) return embedded;

            var all = new List<AudioTrack>(embedded);
            foreach (var file in ExternalAudio)
            {
                // Кодек и каналы — из разбора файла, пока он не разобран, показываем
                // расширение; на воспроизведение это не влияет. Язык, как и у
                // субтитров из файла, не выставляем — он вытеснил бы подпись.
                ExternalAudioInfo.TryGetValue(file.Path, out var probed);
                all.Add(new AudioTrack(
                    all.Count,
                    probed?.Codec ?? System.IO.Path.GetExtension(file.Path).TrimStart('.').ToLowerInvariant(),
                    Language: null,
                    file.Label,
                    probed?.Channels ?? 0,
                    Path: file.Path));
            }
            return all;
        }
    }

    /// <summary>
    /// Все дорожки субтитров файла: вшитые (из ffprobe) и внешние файлы, дописанные
    /// в конец — номера вшитых от этого не меняются.
    /// </summary>
    public IReadOnlyList<SubtitleTrack> SubtitleTracks
    {
        get
        {
            var embedded = Info?.SubtitleTracks ?? [];
            if (ExternalSubtitles.Count == 0) return embedded;

            var all = new List<SubtitleTrack>(embedded);
            foreach (var file in ExternalSubtitles)
            {
                // Язык у дорожки из файла не выставляем: плееры показывают в меню
                // язык ВМЕСТО подписи, а подпись здесь — единственное, что мы знаем.
                all.Add(new SubtitleTrack(
                    all.Count,
                    System.IO.Path.GetExtension(file.Path).TrimStart('.').ToLowerInvariant(),
                    Language: null,
                    file.Label,
                    TextBased: true,
                    Path: file.Path));
            }
            return all;
        }
    }

    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(FileName);
}

/// <summary>Файл дорожки рядом с видео: путь и подпись (остаток имени после шаблона).</summary>
public sealed record TrackFile(string Path, string? Label);

/// <summary>
/// Находит медиафайлы в заданных папках и определяет для каждого файла, можно ли
/// воспроизвести его напрямую или требуется транскодирование через ffmpeg.
/// Идентификатор файла — отпечаток содержимого; отпечатки считаются при скане и
/// кэшируются в data/fingerprints.json (по размеру/mtime), поэтому пересчёт
/// происходит только для новых или изменившихся файлов.
/// </summary>
public sealed class MediaLibrary
{
    private static readonly string[] Extensions =
        [".mkv", ".mp4", ".mov", ".webm", ".avi", ".m4v", ".ts", ".flv", ".wmv", ".mpg", ".mpeg"];

    // Текстовые субтитры отдельными файлами рядом с видео. Графические (.sup,
    // .idx/.sub) не берём — их нельзя перевести в текст (нужен прожиг).
    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt"];

    // Отдельные аудиодорожки рядом с видео (например, русская озвучка к фильму).
    private static readonly string[] AudioExtensions =
        [".mka", ".m4a", ".aac", ".ac3", ".eac3", ".dts", ".flac", ".mp3", ".opus", ".ogg", ".wav"];

    private readonly MediaProbe _probe;
    private readonly FFTools _tools;
    private readonly MediaInfoCache _infoCache;
    private readonly ILogger<MediaLibrary> _logger;
    // Шаблоны путей к файлам дорожек рядом с видео (из настроек).
    private readonly string[] _subtitleFiles;
    private readonly string[] _audioFiles;
    private readonly ConcurrentDictionary<string, MediaItem> _byId = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _resolveLocks = new();
    // Разбор внешних аудиофайлов: один ffprobe на файл за всё время работы.
    private readonly ConcurrentDictionary<string, Lazy<Task<AudioTrack?>>> _externalAudio = new();

    // Персистентный кэш отпечатков: путь → (размер, mtime, хеш) + «последний
    // известный путь» каждого id — по нему ремап находит, куда переехали записи
    // библиотеки, когда содержимое по прежнему пути сменилось.
    private readonly ConcurrentDictionary<string, CacheEntry> _fingerprints;
    private readonly ConcurrentDictionary<string, string> _lastPath;
    private readonly string _cachePath;
    private readonly object _saveLock = new();
    private volatile bool _dirty;

    private sealed record CacheEntry(long Size, long Mtime, string Hash);
    private sealed record CacheFile(Dictionary<string, CacheEntry> Files, Dictionary<string, string> LastPath);

    /// <summary>Корневые папки библиотеки (абсолютные пути, без дублей).</summary>
    public IReadOnlyList<string> MediaDirectories { get; }

    public MediaLibrary(PlayerOptions options, MediaProbe probe, FFTools tools,
        MediaInfoCache infoCache, IHostEnvironment env, ILogger<MediaLibrary> logger)
    {
        _probe = probe;
        _tools = tools;
        _infoCache = infoCache;
        _logger = logger;
        _subtitleFiles = options.SubtitleFiles;
        _audioFiles = options.AudioFiles;

        // Одна и та же папка, указанная дважды (в т.ч. с разным регистром/слешем на
        // конце), — один корень; иначе её файлы сканировались бы по два раза.
        MediaDirectories = options.MediaDirectories
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var dir in MediaDirectories)
        {
            // Папку из конфига создаём, если её нет (как раньше для единственной);
            // недоступный путь не должен ронять запуск — просто останется пустым.
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { _logger.LogError(ex, "Не удалось создать папку с медиа {dir}", dir); }
        }

        _cachePath = Path.Combine(env.ContentRootPath, "data", "fingerprints.json");
        (_fingerprints, _lastPath) = LoadCache();
    }

    /// <summary>Срабатывает в конце скана, если найден файл без метаданных, —
    /// сигнал фоновой доразборке (<see cref="MediaResolveService"/>) проснуться,
    /// не дожидаясь её таймера.</summary>
    public event Action? PendingDiscovered;

    /// <summary>Повторно сканирует папки с медиа и возвращает текущий каталог.</summary>
    public IReadOnlyList<MediaItem> Scan()
    {
        var found = new List<MediaItem>();
        // Один и тот же контент (копия файла, вложенные корни) — одна запись.
        var seen = new HashSet<string>();

        foreach (var dir in MediaDirectories)
        {
            List<string> files;
            // Файлы-компаньоны (субтитры и аудио) по папкам — заполняются тем же
            // проходом, что и список видео, поэтому обнаружение не стоит лишнего I/O.
            var subs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var audios = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                files = [];
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (Extensions.Contains(ext)) files.Add(file);
                    else if (SubtitleExtensions.Contains(ext)) Bucket(subs, file, dir);
                    else if (AudioExtensions.Contains(ext)) Bucket(audios, file, dir);
                }
                files.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                // Одна недоступная папка не должна лишать библиотеку остальных.
                _logger.LogError(ex, "Не удалось перечислить файлы в {dir}", dir);
                continue;
            }

            foreach (var file in files)
            {
                var id = TryComputeId(file);
                if (id is null) continue; // нечитаемый/исчезающий файл — пропускаем
                if (!seen.Add(id)) continue;

                var relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
                var item = _byId.GetOrAdd(id, _ => new MediaItem
                {
                    Id = id,
                    Path = file,
                    RelativePath = relative,
                    FileName = Path.GetFileName(file),
                });
                if (item.Path != file)
                {
                    // Содержимое переехало (или первой нашлась другая копия) —
                    // id прежний, путь актуализируем.
                    item.Path = file;
                    item.RelativePath = relative;
                    item.FileName = Path.GetFileName(file);
                }
                item.ExternalSubtitles = MatchTrackFiles(subs, file, _subtitleFiles);
                item.ExternalAudio = MatchTrackFiles(audios, file, _audioFiles);
                ApplyExternalAudioMode(item);
                found.Add(item);
            }
        }

        SaveCacheIfDirty();
        _infoCache.SaveIfDirty(); // пакетная выгрузка разборов, накопленных с прошлого скана

        // Файлы без метаданных разберёт фоновый цикл — будим его, чтобы новый файл
        // получил метаданные через секунды, а не по таймеру.
        if (found.Any(i => i.Info is null))
            PendingDiscovered?.Invoke();

        return found;
    }

    public MediaItem? Get(string id) => _byId.TryGetValue(id, out var item) ? item : null;

    private static void Bucket(Dictionary<string, List<string>> byFolder, string file, string fallbackDir)
    {
        var folder = Path.GetDirectoryName(file) ?? fallbackDir;
        if (!byFolder.TryGetValue(folder, out var list))
            byFolder[folder] = list = [];
        list.Add(file);
    }

    // Альтернативное аудио возможно только в HLS (рендиции), поэтому файл с внешней
    // звуковой дорожкой раздаётся через HLS, даже если сам по себе браузерный.
    private void ApplyExternalAudioMode(MediaItem item)
    {
        if (item.Mode == PlaybackMode.Direct && item.ExternalAudio.Count > 0 && _tools.Available)
            item.Mode = PlaybackMode.Transcode;
    }

    /// <summary>
    /// Ищет файлы дорожек видеофайла по шаблонам из настроек. Шаблон — путь
    /// относительно папки видео, где <c>{name}</c> — имя видео без расширения
    /// (<c>"{name}"</c>, <c>"subs/{name}"</c>); он должен совпасть с началом пути
    /// файла, а остаток имени становится подписью дорожки. Порядок результата
    /// стабилен — от него зависят номера дорожек.
    /// </summary>
    private static IReadOnlyList<TrackFile> MatchTrackFiles(
        Dictionary<string, List<string>> byFolder, string videoPath, string[] templates)
    {
        var folder = Path.GetDirectoryName(videoPath);
        if (folder is null) return [];

        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        var found = new List<TrackFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var template in templates)
        {
            // Шаблон → конкретный путь-префикс; слеши шаблона одинаковы на всех ОС.
            var prefix = Path.Combine(folder, template
                .Replace("{name}", baseName)
                .Replace('/', Path.DirectorySeparatorChar));

            var searchFolder = Path.GetDirectoryName(prefix);
            if (searchFolder is null || !byFolder.TryGetValue(searchFolder, out var candidates)) continue;

            foreach (var candidate in candidates)
            {
                var withoutExtension = Path.Combine(
                    Path.GetDirectoryName(candidate)!, Path.GetFileNameWithoutExtension(candidate));
                if (!withoutExtension.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(candidate)) continue; // файл уже взят другим шаблоном

                var label = withoutExtension[prefix.Length..].Trim(' ', '.', '_', '-');
                found.Add(new TrackFile(candidate, label.Length > 0 ? label : null));
            }
        }
        return found;
    }

    /// <summary>
    /// Куда «переехало» содержимое: текущий id файла по последнему известному пути
    /// <paramref name="missingId"/>, если там теперь другое содержимое (докачка/
    /// перезапись). null — путь неизвестен, файла нет или содержимое прежнее.
    /// </summary>
    public string? FindSuccessor(string missingId)
    {
        if (!_lastPath.TryGetValue(missingId, out var path)) return null;
        var current = TryComputeId(path);
        SaveCacheIfDirty();
        return current is not null && current != missingId ? current : null;
    }

    // Id файла с кэшированием по (путь, размер, mtime). null — файл исчез/нечитаем.
    private string? TryComputeId(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;

            var mtime = fi.LastWriteTimeUtc.Ticks;
            if (_fingerprints.TryGetValue(path, out var cached) && cached.Size == fi.Length && cached.Mtime == mtime)
                return $"{cached.Size}-{cached.Hash}";

            var hash = MediaFingerprinter.Compute(path).Hash;
            _fingerprints[path] = new CacheEntry(fi.Length, mtime, hash);
            var id = $"{fi.Length}-{hash}";
            _lastPath[id] = path;
            _dirty = true;
            return id;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось посчитать отпечаток {path}", path);
            return null;
        }
    }

    private (ConcurrentDictionary<string, CacheEntry>, ConcurrentDictionary<string, string>) LoadCache()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cachePath));
                if (cache is not null)
                    return (new(cache.Files), new(cache.LastPath));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Кэш отпечатков повреждён — будет пересоздан");
        }
        return (new(), new());
    }

    private void SaveCacheIfDirty()
    {
        if (!_dirty) return;
        lock (_saveLock)
        {
            if (!_dirty) return;
            _dirty = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                var payload = JsonSerializer.Serialize(new CacheFile(new(_fingerprints), new(_lastPath)));
                File.WriteAllText(_cachePath + ".tmp", payload);
                File.Move(_cachePath + ".tmp", _cachePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось сохранить кэш отпечатков");
            }
        }
    }

    /// <summary>
    /// Быстрый резолв без запуска инструментов: применяет разбор из персистентного
    /// кэша, если он там есть; иначе файл остаётся неразобранным (Pending) до
    /// фоновой доразборки. ffprobe здесь не запускается ни для видео, ни для
    /// внешних аудиофайлов — поэтому список отвечает мгновенно.
    /// </summary>
    public async Task TryResolveFromCacheAsync(MediaItem item)
    {
        if (item.Info is not null) return;
        var cached = _infoCache.TryGet(item.Id);
        if (cached is null) return;

        var sem = _resolveLocks.GetOrAdd(item.Id, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(0)) return; // файл уже резолвится параллельно — не ждём
        try
        {
            if (item.Info is not null) return;
            await ApplyInfoAsync(item, cached);
            ApplyExternalAudioMode(item);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Лениво анализирует файл и определяет режим воспроизведения. Операция общая и
    /// одноразовая: результат кэшируется, повторные вызовы возвращают его сразу.
    /// </summary>
    public async Task<MediaItem> ResolveAsync(MediaItem item, CancellationToken ct = default)
    {
        if (item.Info is not null)
        {
            // Файл уже разобран, но рядом мог появиться новый внешний аудиофайл.
            await ProbeExternalAudioAsync(item);
            return item;
        }

        // Сериализуем определение режима по каждому файлу, чтобы параллельные
        // запросы не запускали несколько ffprobe и не перетирали результат друг друга.
        var sem = _resolveLocks.GetOrAdd(item.Id, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            if (item.Info is not null)
                return item; // другой запрос успел определить режим, пока мы ждали
            await ResolveCoreAsync(item);
            await ProbeExternalAudioAsync(item);
            ApplyExternalAudioMode(item);
            return item;
        }
        finally
        {
            sem.Release();
        }
    }

    // Разбирает внешние аудиофайлы (кодек/каналы/язык) — по одному ffprobe на файл,
    // с кэшем: без каналов не выбрать правильный даунмикс 5.1 → стерео.
    private async Task ProbeExternalAudioAsync(MediaItem item)
    {
        if (item.ExternalAudio.Count == 0) return;
        if (item.ExternalAudioInfo.Count == item.ExternalAudio.Count &&
            item.ExternalAudio.All(f => item.ExternalAudioInfo.ContainsKey(f.Path)))
            return; // всё уже разобрано

        var map = new Dictionary<string, AudioTrack>();
        foreach (var file in item.ExternalAudio.Select(f => f.Path))
        {
            var track = await _externalAudio
                .GetOrAdd(file, p => new Lazy<Task<AudioTrack?>>(async () =>
                    (await _probe.ProbeAsync(p, CancellationToken.None))?.AudioTracks.FirstOrDefault()))
                .Value;
            if (track is not null) map[file] = track;
        }
        item.ExternalAudioInfo = map;
    }

    private async Task ResolveCoreAsync(MediaItem item)
    {
        if (!File.Exists(item.Path))
        {
            // Не кэшируем: файл может появиться позже.
            item.Mode = PlaybackMode.Unsupported;
            item.StatusMessage = "Файл больше не существует на диске.";
            return;
        }

        // Разбор мог сохраниться в персистентном кэше (ключ — отпечаток, он же id):
        // тогда ffprobe не нужен вовсе — в том числе после рестарта хоста. Режим
        // ниже вычисляется всегда: он зависит от окружения (наличие ffmpeg и
        // декодера), а не только от содержимого файла.
        var info = _infoCache.TryGet(item.Id);
        if (info is null)
        {
            // ВАЖНО: анализ файла не привязан к токену конкретного HTTP-запроса.
            // Прерванный запрос (например, при перемотке клиент отменяет загрузку
            // сегментов) не должен отменять ffprobe и «отравлять» кэш режимом Unsupported.
            info = await _probe.ProbeAsync(item.Path, CancellationToken.None);
            if (info is not null)
                _infoCache.Put(item.Id, info); // неудачу не кэшируем: ffprobe может появиться позже
        }

        if (info is null)
        {
            // ffprobe недоступен/не смог прочитать файл: для mp4/webm пробуем прямое
            // воспроизведение, для остального — помечаем как недоступное.
            var ext = Path.GetExtension(item.Path).ToLowerInvariant();
            if (ext is ".mp4" or ".webm" or ".m4v" or ".mov")
            {
                item.Mode = PlaybackMode.Direct;
            }
            else
            {
                item.Mode = PlaybackMode.Unsupported;
                item.StatusMessage = _tools.Available
                    ? "Не удалось прочитать метаданные медиа (ошибка ffprobe)."
                    : "ffmpeg/ffprobe не установлены, этот контейнер обработать нельзя.";
            }
            // Публикуем Info последним — после того как Mode уже выставлен.
            item.Info = new MediaInfo
            {
                DurationSeconds = 0, Container = ext.TrimStart('.'),
                VideoCodec = null, AudioCodec = null,
            };
            return;
        }

        await ApplyInfoAsync(item, info);
    }

    // Выбирает режим воспроизведения по разбору и публикует его в записи. Режим
    // зависит от окружения (наличие ffmpeg и декодера), поэтому вычисляется при
    // каждом запуске заново, а не хранится в кэше вместе с разбором.
    private async Task ApplyInfoAsync(MediaItem item, MediaInfo info)
    {
        if (info.IsBrowserNative)
        {
            item.Mode = PlaybackMode.Direct;
        }
        else if (!_tools.Available)
        {
            // Нужна обработка -> требуется ffmpeg И декодер для исходного кодека.
            item.Mode = PlaybackMode.Unsupported;
            item.StatusMessage = "ffmpeg не установлен; перекодировать этот файл для браузера нельзя.";
        }
        else if (!await _tools.HasDecoderAsync(info.VideoCodec ?? "", CancellationToken.None))
        {
            item.Mode = PlaybackMode.Unsupported;
            item.StatusMessage = $"Видеокодек '{info.VideoCodec}' не установлен/не декодируется ffmpeg в этой системе.";
        }
        else
        {
            item.Mode = PlaybackMode.Transcode;
        }

        item.Info = info; // публикуем последним
    }
}
