using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

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
    /// Лениво анализирует файл и определяет режим воспроизведения. После первого
    /// успешного определения результат кэшируется.
    /// </summary>
    public async Task<MediaItem> ResolveAsync(MediaItem item, CancellationToken ct = default)
    {
        if (item.Info is not null)
            return item;

        if (!File.Exists(item.Path))
        {
            item.Mode = PlaybackMode.Unsupported;
            item.StatusMessage = "Файл больше не существует на диске.";
            return item;
        }

        var info = await _probe.ProbeAsync(item.Path, ct);

        if (info is null)
        {
            // ffprobe недоступен: остаётся только прямое воспроизведение, пусть
            // браузер сам пробует. Работает для обычных mp4/webm; mkv, скорее всего,
            // не воспроизведётся.
            item.Info = new MediaInfo
            {
                DurationSeconds = 0, Container = Path.GetExtension(item.Path).TrimStart('.'),
                VideoCodec = null, AudioCodec = null,
            };
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
            return item;
        }

        item.Info = info;

        if (info.IsBrowserNative)
        {
            item.Mode = PlaybackMode.Direct;
            return item;
        }

        // Нужна обработка -> требуется ffmpeg И декодер для исходного кодека.
        if (!_tools.Available)
        {
            item.Mode = PlaybackMode.Unsupported;
            item.StatusMessage = "ffmpeg не установлен; перекодировать этот файл для браузера нельзя.";
            return item;
        }

        var codec = info.VideoCodec ?? "";
        if (!await _tools.HasDecoderAsync(codec, ct))
        {
            item.Mode = PlaybackMode.Unsupported;
            item.StatusMessage = $"Видеокодек '{codec}' не установлен/не декодируется ffmpeg в этой системе.";
            return item;
        }

        item.Mode = PlaybackMode.Transcode;
        return item;
    }

    private static string MakeId(string path)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
