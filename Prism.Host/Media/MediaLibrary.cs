using System.Collections.Concurrent;
using System.Text.Json;
using Prism.Common;

namespace Prism.Host.Media;

public enum PlaybackMode
{
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

    public required string FileName { get; set; }
    public MediaInfo? Info { get; set; }
    public PlaybackMode Mode { get; set; }
    public string? StatusMessage { get; set; }

    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(FileName);
}

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

    private readonly MediaProbe _probe;
    private readonly FFTools _tools;
    private readonly ILogger<MediaLibrary> _logger;
    private readonly ConcurrentDictionary<string, MediaItem> _byId = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _resolveLocks = new();

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
        IHostEnvironment env, ILogger<MediaLibrary> logger)
    {
        _probe = probe;
        _tools = tools;
        _logger = logger;

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

    /// <summary>Повторно сканирует папки с медиа и возвращает текущий каталог.</summary>
    public IReadOnlyList<MediaItem> Scan()
    {
        var found = new List<MediaItem>();
        // Один и тот же контент (копия файла, вложенные корни) — одна запись.
        var seen = new HashSet<string>();

        foreach (var dir in MediaDirectories)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
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

                var item = _byId.GetOrAdd(id, _ => new MediaItem
                {
                    Id = id,
                    Path = file,
                    FileName = Path.GetFileName(file),
                });
                if (item.Path != file)
                {
                    // Содержимое переехало (или первой нашлась другая копия) —
                    // id прежний, путь актуализируем.
                    item.Path = file;
                    item.FileName = Path.GetFileName(file);
                }
                found.Add(item);
            }
        }

        SaveCacheIfDirty();
        return found;
    }

    public MediaItem? Get(string id) => _byId.TryGetValue(id, out var item) ? item : null;

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
    /// Лениво анализирует файл и определяет режим воспроизведения. Операция общая и
    /// одноразовая: результат кэшируется, повторные вызовы возвращают его сразу.
    /// </summary>
    public async Task<MediaItem> ResolveAsync(MediaItem item, CancellationToken ct = default)
    {
        if (item.Info is not null)
            return item;

        // Сериализуем определение режима по каждому файлу, чтобы параллельные
        // запросы не запускали несколько ffprobe и не перетирали результат друг друга.
        var sem = _resolveLocks.GetOrAdd(item.Id, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            if (item.Info is not null)
                return item; // другой запрос успел определить режим, пока мы ждали
            await ResolveCoreAsync(item);
            return item;
        }
        finally
        {
            sem.Release();
        }
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

        // ВАЖНО: анализ файла не привязан к токену конкретного HTTP-запроса.
        // Прерванный запрос (например, при перемотке клиент отменяет загрузку
        // сегментов) не должен отменять ffprobe и «отравлять» кэш режимом Unsupported.
        var info = await _probe.ProbeAsync(item.Path, CancellationToken.None);

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
