using Microsoft.EntityFrameworkCore;
using Prism.Abstractions;

namespace Prism.Plugins.Library;

/// <summary>
/// Отдаёт ядру доп. поля записи из хранилища метаданных: вычисленный <c>title</c>,
/// <c>kind</c> и (для эпизода) сериал/сезон/эпизод. Синглтон + фабрика контекста,
/// чтобы безопасно вызываться из корневого провайдера ядра.
/// </summary>
public sealed class MetadataMetaSource(IDbContextFactory<LibraryDbContext> factory) : IMediaMetaSource
{
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>> GetMetaAsync(
        IReadOnlyCollection<string> mediaIds, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Metadata.Where(x => mediaIds.Contains(x.MediaId)).ToListAsync(ct);

        var result = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        foreach (var r in rows)
        {
            var fields = new Dictionary<string, object?>
            {
                ["kind"] = r.Kind,
                ["seriesTitle"] = r.SeriesTitle,
                ["season"] = r.Season,
                ["episode"] = r.Episode,
                ["hasMetadata"] = true,
            };
            // Название переопределяем только если оно есть — иначе оставляем имя файла.
            var title = DisplayTitle(r);
            if (title is not null) fields["title"] = title;
            result[r.MediaId] = fields;
        }
        return result;
    }

    // Фильм → Title; эпизод → «Сериал · SxxExx».
    internal static string? DisplayTitle(MediaMetadataRecord r)
    {
        if (r.Kind == "episode" && !string.IsNullOrWhiteSpace(r.SeriesTitle))
        {
            var code = r is { Season: int s, Episode: int e } ? $" · S{s:00}E{e:00}" : "";
            return r.SeriesTitle + code;
        }
        return string.IsNullOrWhiteSpace(r.Title) ? null : r.Title;
    }
}
