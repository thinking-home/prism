using Microsoft.EntityFrameworkCore;
using Prism.Abstractions;

namespace Prism.Plugins.Library;

/// <summary>
/// Подмешивает к записям <c>/api/media</c> свободную мету файла из Prism_Meta.
/// Id файла — ключ содержимого, он же ключ меты, поэтому резолва не нужно.
/// Семантику ключей сервер не знает — потребитель берёт те, о которых знает сам
/// (например, <c>title</c>). Синглтон + фабрика контекста, чтобы безопасно
/// вызываться из ядра.
/// </summary>
public sealed class MetadataMetaSource(IDbContextFactory<LibraryDbContext> factory) : IMediaMetaSource
{
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>> GetMetaAsync(
        IReadOnlyCollection<string> mediaIds, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Meta
            .Where(m => m.EntityType == MetaEntity.File && mediaIds.Contains(m.EntityKey))
            .ToListAsync(ct);

        var result = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        foreach (var group in rows.GroupBy(r => r.EntityKey))
            result[group.Key] = group.ToDictionary(r => r.Key, object? (r) => r.Value);
        return result;
    }
}

/// <summary>Значения EntityType в таблице меты.</summary>
public static class MetaEntity
{
    public const string File = "file";
    public const string Node = "node";
}
