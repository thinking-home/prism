using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
    public required string Id { get; init; }
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public MediaInfo? Info { get; set; }
    public PlaybackMode Mode { get; set; }
    public string? StatusMessage { get; set; }

    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(FileName);
}

/// <summary>
/// Находит медиафайлы в заданной папке и определяет для каждого файла, можно ли
/// воспроизвести его напрямую или требуется транскодирование через ffmpeg.
/// </summary>
public sealed class MediaLibrary
{
    private static readonly string[] Extensions =
        [".mkv", ".mp4", ".mov", ".webm", ".avi", ".m4v", ".ts", ".flv", ".wmv", ".mpg", ".mpeg"];

    private readonly PlayerOptions _options;
    private readonly MediaProbe _probe;
    private readonly FFTools _tools;
    private readonly ILogger<MediaLibrary> _logger;
    private readonly ConcurrentDictionary<string, MediaItem> _byId = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _resolveLocks = new();
    // Кэш отпечатков по пути файла; переснимается, если файл изменился (размер/mtime).
    private readonly ConcurrentDictionary<string, (long Size, long Mtime, string Hash)> _fingerprints = new();

    public string MediaDirectory { get; }

    public MediaLibrary(PlayerOptions options, MediaProbe probe, FFTools tools, ILogger<MediaLibrary> logger)
    {
        _options = options;
        _probe = probe;
        _tools = tools;
        _logger = logger;
        MediaDirectory = Path.GetFullPath(options.MediaDirectory);
        Directory.CreateDirectory(MediaDirectory);
    }

    /// <summary>Повторно сканирует папку с медиа и возвращает текущий каталог.</summary>
    public IReadOnlyList<MediaItem> Scan()
    {
        var found = new List<MediaItem>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(MediaDirectory, "*", SearchOption.AllDirectories)
                .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось перечислить файлы в {dir}", MediaDirectory);
            return [];
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var id = MakeId(file);
            var item = _byId.GetOrAdd(id, _ => new MediaItem
            {
                Id = id,
                Path = file,
                FileName = Path.GetFileName(file),
            });
            found.Add(item);
        }

        return found;
    }

    public MediaItem? Get(string id) => _byId.TryGetValue(id, out var item) ? item : null;

    /// <summary>
    /// Находит файл библиотеки по отпечатку содержимого (см. <see cref="MediaFingerprint"/>).
    /// Идентификация по содержимому, а не по пути: работает и когда запрос пришёл с
    /// другой машины. Сначала мгновенный пред-фильтр по размеру (из <see cref="FileInfo"/>),
    /// затем сверка хеша краёв — блоки читаются только у файлов совпавшего размера,
    /// результат кэшируется. Возвращает <c>null</c>, если такого файла в библиотеке нет.
    /// </summary>
    public MediaItem? FindByFingerprint(long size, string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return null;

        foreach (var item in Scan())
        {
            FileInfo fi;
            try
            {
                fi = new FileInfo(item.Path);
                if (!fi.Exists || fi.Length != size) continue; // пред-фильтр по размеру
            }
            catch { continue; }

            string fp;
            try { fp = Fingerprint(item.Path, fi); }
            catch { continue; } // файл занят/исчез — пропускаем, не роняем поиск

            if (string.Equals(fp, hash, StringComparison.OrdinalIgnoreCase))
                return item;
        }

        return null;
    }

    private string Fingerprint(string path, FileInfo fi)
    {
        var mtime = fi.LastWriteTimeUtc.Ticks;
        if (_fingerprints.TryGetValue(path, out var cached) && cached.Size == fi.Length && cached.Mtime == mtime)
            return cached.Hash;

        var hash = MediaFingerprinter.Compute(path).Hash;
        _fingerprints[path] = (fi.Length, mtime, hash);
        return hash;
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

    private static string MakeId(string path)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
