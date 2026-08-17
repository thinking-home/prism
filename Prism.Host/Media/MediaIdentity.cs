using Prism.Abstractions;

namespace Prism.Host.Media;

/// <summary>
/// Реализация <see cref="IMediaIdentity"/> поверх <see cref="MediaLibrary"/>.
/// Чтение файлов (расчёт отпечатков) уводится в пул потоков, чтобы не блокировать
/// обработчики запросов.
/// </summary>
public sealed class MediaIdentity(MediaLibrary library) : IMediaIdentity
{
    public Task<IReadOnlyCollection<MediaFile>> GetLiveFilesAsync(CancellationToken ct = default) =>
        Task.Run(() => (IReadOnlyCollection<MediaFile>)library.Scan()
            .Select(i => new MediaFile(i.Id, i.RelativePath))
            .ToArray(), ct);

    public Task<string?> FindSuccessorAsync(string missingId, CancellationToken ct = default) =>
        Task.Run(() => library.FindSuccessor(missingId), ct);
}
