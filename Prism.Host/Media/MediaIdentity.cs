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
            .Select(i => new MediaFile(i.Id, Relative(i.Path)))
            .ToArray(), ct);

    public Task<string?> FindSuccessorAsync(string missingId, CancellationToken ct = default) =>
        Task.Run(() => library.FindSuccessor(missingId), ct);

    // Путь относительно корня медиапапки, в которой лежит файл; слеши всегда
    // прямые, чтобы правила автозаполнения писались одинаково на всех ОС.
    private string Relative(string path)
    {
        foreach (var root in library.MediaDirectories)
        {
            var rel = Path.GetRelativePath(root, path);
            if (rel != path && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                return rel.Replace('\\', '/');
        }
        return Path.GetFileName(path); // файл вне корней — не должно случаться
    }
}
