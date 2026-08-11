namespace Prism.Abstractions;

/// <summary>
/// Доступ плагинов к идентичности файлов библиотеки. Id файла — ключ содержимого
/// (fingerprint: размер + хеш краёв), поэтому записи, привязанные к id, переживают
/// переименование и перенос файлов. Реализуется хостом.
/// </summary>
public interface IMediaIdentity
{
    /// <summary>Id всех файлов текущего скана (для флага наличия и сборки мусора).</summary>
    Task<IReadOnlyCollection<string>> GetLiveIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Куда «переехало» содержимое: если последний известный путь файла
    /// <paramref name="missingId"/> теперь занят другим содержимым (файл докачался
    /// или перезаписан), вернуть его текущий id; иначе null.
    /// </summary>
    Task<string?> FindSuccessorAsync(string missingId, CancellationToken ct = default);
}
