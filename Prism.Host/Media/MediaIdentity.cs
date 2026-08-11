using Prism.Abstractions;

namespace Prism.Host.Media;

/// <summary>
/// Реализация <see cref="IMediaIdentity"/> поверх <see cref="MediaLibrary"/>.
/// Чтение файлов (расчёт отпечатков) уводится в пул потоков, чтобы не блокировать
/// обработчики запросов.
/// </summary>
public sealed class MediaIdentity(MediaLibrary library) : IMediaIdentity
{
    public Task<IReadOnlyCollection<string>> GetLiveIdsAsync(CancellationToken ct = default) =>
        Task.Run(() => (IReadOnlyCollection<string>)library.Scan().Select(i => i.Id).ToHashSet(), ct);

    public Task<string?> FindSuccessorAsync(string missingId, CancellationToken ct = default) =>
        Task.Run(() => library.FindSuccessor(missingId), ct);
}
