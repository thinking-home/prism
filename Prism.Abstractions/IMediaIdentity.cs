namespace Prism.Abstractions;

/// <summary>
/// Доступ плагинов к идентичности файлов библиотеки. Id файла — ключ содержимого
/// (fingerprint: размер + хеш краёв), поэтому записи, привязанные к id, переживают
/// переименование и перенос файлов. Реализуется хостом.
/// </summary>
public interface IMediaIdentity
{
    /// <summary>Файлы текущего скана: id + относительный путь. Id — для флага
    /// наличия и сборки мусора, путь — для правил автозаполнения.</summary>
    Task<IReadOnlyCollection<MediaFile>> GetLiveFilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Куда «переехало» содержимое: если последний известный путь файла
    /// <paramref name="missingId"/> теперь занят другим содержимым (файл докачался
    /// или перезаписан), вернуть его текущий id; иначе null.
    /// </summary>
    Task<string?> FindSuccessorAsync(string missingId, CancellationToken ct = default);
}

/// <summary>Файл библиотеки: ключ содержимого + путь относительно корня своей
/// медиапапки (прямые слеши, без ведущего слеша) — одинаковый на всех ОС.</summary>
public sealed record MediaFile(string Id, string RelativePath);
