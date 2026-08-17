using System.Collections.Concurrent;
using System.Text.Json;

namespace Prism.Host.Media;

/// <summary>
/// Персистентный кэш результатов ffprobe с ключом-отпечатком содержимого (он же
/// id файла). Ключ контентный, поэтому кэш не протухает по определению: то же
/// содержимое всегда даёт те же дорожки и длительность — переименования, переезды
/// и рестарты хоста пересчёта не требуют. Повторный разбор нужен только
/// действительно новым файлам.
/// </summary>
public sealed class MediaInfoCache
{
    private readonly ConcurrentDictionary<string, MediaInfo> _byId;
    private readonly string _cachePath;
    private readonly Lock _saveLock = new();
    private readonly ILogger<MediaInfoCache> _logger;
    private volatile bool _dirty;

    public MediaInfoCache(IHostEnvironment env, ILogger<MediaInfoCache> logger)
    {
        _logger = logger;
        _cachePath = Path.Combine(env.ContentRootPath, "data", "mediainfo.json");
        _byId = Load();
    }

    public MediaInfo? TryGet(string id) => _byId.TryGetValue(id, out var info) ? info : null;

    /// <summary>Кладёт разбор в память, на диск НЕ пишет — сохранение пакетное:
    /// <see cref="SaveIfDirty"/> вызывают скан и фоновая доразборка, чтобы холодное
    /// заполнение большой библиотеки не переписывало файл после каждого ffprobe.</summary>
    public void Put(string id, MediaInfo info)
    {
        _byId[id] = info;
        _dirty = true;
    }

    private ConcurrentDictionary<string, MediaInfo> Load()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                var cache = JsonSerializer.Deserialize<Dictionary<string, MediaInfo>>(File.ReadAllText(_cachePath));
                if (cache is not null) return new(cache);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Кэш разбора медиа повреждён — будет пересоздан");
        }
        return new();
    }

    public void SaveIfDirty()
    {
        if (!_dirty) return;
        lock (_saveLock)
        {
            if (!_dirty) return;
            _dirty = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                var payload = JsonSerializer.Serialize(new Dictionary<string, MediaInfo>(_byId));
                File.WriteAllText(_cachePath + ".tmp", payload);
                File.Move(_cachePath + ".tmp", _cachePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось сохранить кэш разбора медиа");
            }
        }
    }
}
