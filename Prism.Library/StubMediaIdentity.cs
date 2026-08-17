using Prism.Abstractions;

namespace Prism.Library;

/// <summary>
/// Временная заглушка идентичности файлов — до следующего шага (HTTP-опрос
/// хостов из конфига, п.14 TODO, шаг 2). Файлов не видит: дерево и мета
/// работают, present у всех записей false, ремап и правила проходят вхолостую.
/// С этой заглушкой нельзя запускать /api/library/gc на живой БД — он посчитает
/// мёртвыми все записи файлов.
/// </summary>
internal sealed class StubMediaIdentity : IMediaIdentity
{
    public Task<IReadOnlyCollection<MediaFile>> GetLiveFilesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyCollection<MediaFile>>([]);

    public Task<string?> FindSuccessorAsync(string missingId, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}
