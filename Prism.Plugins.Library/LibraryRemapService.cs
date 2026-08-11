using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prism.Abstractions;

namespace Prism.Plugins.Library;

/// <summary>
/// Фоновый ремап записей библиотеки: если файл с записями исчез (id осиротел), а по
/// его последнему известному пути лежит другое содержимое (докачка/перезапись) —
/// мета и членство переезжают на новый id. Пока файл дописывается, записи просто
/// переезжают по цепочке при каждом проходе — карантинов и таймеров нет намеренно.
/// Записи преемника не перетираются: занятый id пропускается.
/// </summary>
public sealed class LibraryRemapService(
    IDbContextFactory<LibraryDbContext> factory,
    IMediaIdentity identity,
    ILogger<LibraryRemapService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RemapAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ошибка ремапа записей библиотеки");
            }

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RemapAsync(CancellationToken ct)
    {
        var live = (await identity.GetLiveIdsAsync(ct)).ToHashSet();

        await using var db = await factory.CreateDbContextAsync(ct);
        var itemKeys = await db.NodeItems.Select(i => i.FileKey).Distinct().ToListAsync(ct);
        var metaKeys = await db.Meta.Where(m => m.EntityType == MetaEntity.File)
            .Select(m => m.EntityKey).Distinct().ToListAsync(ct);

        var orphans = itemKeys.Union(metaKeys).Where(k => !live.Contains(k)).ToList();
        foreach (var orphan in orphans)
        {
            ct.ThrowIfCancellationRequested();

            var successor = await identity.FindSuccessorAsync(orphan, ct);
            if (successor is null) continue;

            var busy = await db.NodeItems.AnyAsync(i => i.FileKey == successor, ct) ||
                       await db.Meta.AnyAsync(m => m.EntityType == MetaEntity.File && m.EntityKey == successor, ct);
            if (busy) continue;

            // Составную часть первичного ключа менять нельзя — строки пересоздаются;
            // удаление сохраняется до вставки, чтобы не столкнуться по ключу.
            var items = await db.NodeItems.Where(i => i.FileKey == orphan).ToListAsync(ct);
            var meta = await db.Meta.Where(m => m.EntityType == MetaEntity.File && m.EntityKey == orphan).ToListAsync(ct);
            db.NodeItems.RemoveRange(items);
            db.Meta.RemoveRange(meta);
            await db.SaveChangesAsync(ct);

            db.NodeItems.AddRange(items.Select(i => new NodeItemRecord { NodeId = i.NodeId, FileKey = successor }));
            db.Meta.AddRange(meta.Select(m => new MetaRecord
            {
                EntityType = MetaEntity.File, EntityKey = successor, Key = m.Key, Value = m.Value,
            }));
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Записи библиотеки перенесены на новое содержимое: {old} → {new}", orphan, successor);
        }
    }
}
