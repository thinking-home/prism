using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Prism.Library;

/// <summary>
/// Фоновое обслуживание библиотеки (при старте, раз в 5 минут и по требованию —
/// POST /api/library/scan будит цикл), два шага по порядку:
///
/// 1. <b>Ремап.</b> Если файл с записями исчез (id осиротел), а по его последнему
///    известному пути лежит другое содержимое (докачка/перезапись) — мета и членство
///    переезжают на новый id. Пока файл дописывается, записи просто переезжают по
///    цепочке при каждом проходе — карантинов и таймеров нет намеренно. Записи
///    преемника не перетираются: занятый id пропускается.
///
/// 2. <b>Правила автозаполнения.</b> К файлам, у которых нет НИ ОДНОЙ записи (ни
///    меты, ни членства), применяются все подходящие правила из конфига. Пометок
///    «обработано» нет намеренно: любая запись у файла — признак, что его уже
///    трогали (правило или руки), и правила к нему больше не прикасаются; поэтому
///    ручные правки, включая удаление отдельных ключей, не откатываются.
///
/// Ремап идёт строго первым: записи должны успеть переехать на новый id раньше,
/// чем правила примут файл-преемник за нетронутый и заполнят его заново.
/// Все проходы выполняет только этот цикл — параллельных проходов не бывает.
/// Поэтому и очистка библиотеки (<c>scan?replace=true</c>) делается здесь, в
/// начале прохода: из обработчика запроса она сносила группы прямо из-под
/// работающего прохода, и тот дописывал строки на уже удалённые id.
/// </summary>
public sealed class LibraryMaintenanceService(
    IDbContextFactory<LibraryDbContext> factory,
    IMediaIdentity identity,
    IConfiguration config,
    ILogger<LibraryMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // Правила читаются один раз при старте — изменение конфига требует
    // перезапуска, как и остальные настройки хоста.
    private readonly IReadOnlyList<CompiledRule> _rules = CompiledRule.Load(config, logger);

    // Сигнал «пройтись сейчас» от ручки /api/library/scan; ёмкость 1 — повторные
    // запросы во время прохода схлопываются в один дополнительный проход.
    private readonly SemaphoreSlim _wake = new(0, 1);

    // Запрошенная замена (scan?replace=true): очистку выполняет сам цикл в начале
    // ближайшего прохода. 0/1 через Interlocked — ставит ручка, снимает цикл.
    private int _replaceRequested;

    /// <summary>Будит цикл обслуживания, не дожидаясь таймера. Не ждёт завершения —
    /// итоги прохода пишутся в лог. <paramref name="replace"/> — очистить библиотеку
    /// перед раскладкой (режим отладки правил).</summary>
    public void RequestScan(bool replace = false)
    {
        if (replace) Interlocked.Exchange(ref _replaceRequested, 1);
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* проход уже запрошен */ }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Правил автозаполнения: {count}", _rules.Count);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Interlocked.Exchange(ref _replaceRequested, 0) == 1)
                    await ClearLibraryAsync(ct);

                var files = await identity.GetLiveFilesAsync(ct);
                var remapped = await RemapAsync(files, ct);
                var autofilled = await ApplyRulesAsync(files, ct);

                logger.LogInformation("Обслуживание библиотеки: файлов {files}, ремап {remapped}, автозаполнено {autofilled}",
                    files.Count, remapped, autofilled);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ошибка фонового обслуживания библиотеки");
            }

            // Ждём таймер или сигнал ручки — что случится раньше.
            try { await _wake.WaitAsync(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<int> RemapAsync(IReadOnlyCollection<MediaFile> files, CancellationToken ct)
    {
        var live = files.Select(f => f.Id).ToHashSet();

        await using var db = await factory.CreateDbContextAsync(ct);
        var itemKeys = await db.NodeItems.Select(i => i.FileKey).Distinct().ToListAsync(ct);
        var metaKeys = await db.Meta.Where(m => m.EntityType == MetaEntity.File)
            .Select(m => m.EntityKey).Distinct().ToListAsync(ct);

        var remapped = 0;
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
            remapped++;

            logger.LogInformation("Записи библиотеки перенесены на новое содержимое: {old} → {new}", orphan, successor);
        }
        return remapped;
    }

    // Применяет правила к нетронутым файлам; возвращает число заполненных. Мета и
    // членство только добавляются; если несколько правил дают один мета-ключ,
    // выигрывает первое по порядку. Раскладка каждого файла — включая созданные
    // для него группы — пишется одним сохранением.
    private async Task<int> ApplyRulesAsync(IReadOnlyCollection<MediaFile> files, CancellationToken ct)
    {
        if (_rules.Count == 0) return 0;

        await using var db = await factory.CreateDbContextAsync(ct);

        // «Тронутые» файлы: есть хоть одна запись — мета или членство, не важно,
        // от правила или от руки. К таким правила не прикасаются.
        var touched = (await db.NodeItems.Select(i => i.FileKey).Distinct().ToListAsync(ct))
            .Concat(await db.Meta.Where(m => m.EntityType == MetaEntity.File)
                .Select(m => m.EntityKey).Distinct().ToListAsync(ct))
            .ToHashSet();

        // Кэш «(родитель, имя) → id группы» на один проход: сезон из 20 серий не
        // должен искать свою группу 20 раз.
        var nodeCache = new Dictionary<(Guid? Parent, string Name), Guid>();

        var autofilled = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (touched.Contains(file.Id)) continue;

            var path = WithoutExtension(file.RelativePath);
            var nodeIds = new List<Guid>();
            var meta = new Dictionary<string, string>();
            foreach (var rule in _rules)
            {
                var values = rule.Match(path);
                if (values is null) continue;

                if (!string.IsNullOrWhiteSpace(rule.Rule.Node))
                {
                    var nodeId = await EnsureNodePathAsync(db, nodeCache,
                        CompiledRule.Substitute(rule.Rule.Node, values), ct);
                    if (nodeId is Guid id && !nodeIds.Contains(id)) nodeIds.Add(id);
                }
                foreach (var (key, template) in rule.Rule.Meta)
                {
                    var value = CompiledRule.Substitute(template, values).Trim();
                    if (value.Length > 0 && !meta.ContainsKey(key)) meta[key] = value;
                }
            }
            if (nodeIds.Count == 0 && meta.Count == 0) continue;

            db.NodeItems.AddRange(nodeIds.Select(n => new NodeItemRecord { NodeId = n, FileKey = file.Id }));
            db.Meta.AddRange(meta.Select(kv => new MetaRecord
            {
                EntityType = MetaEntity.File, EntityKey = file.Id, Key = kv.Key, Value = kv.Value,
            }));
            await db.SaveChangesAsync(ct);
            touched.Add(file.Id);
            autofilled++;

            logger.LogInformation("Автозаполнение {path}: группы [{nodes}], мета [{meta}]",
                file.RelativePath,
                string.Join(", ", nodeIds),
                string.Join(", ", meta.Select(m => $"{m.Key}={m.Value}")));
        }
        return autofilled;
    }

    // Находит или создаёт цепочку групп по пути вида «Сериалы/Патриот»; возвращает
    // id последней. Созданные группы остаются в контексте несохранёнными — их
    // запишет общее SaveChanges файла. null — после подстановки не осталось сегментов.
    private static async Task<Guid?> EnsureNodePathAsync(LibraryDbContext db,
        Dictionary<(Guid? Parent, string Name), Guid> cache, string path, CancellationToken ct)
    {
        Guid? parent = null;
        foreach (var name in path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parentKey = parent;
            if (!cache.TryGetValue((parentKey, name), out var id))
            {
                var node = await db.Nodes.FirstOrDefaultAsync(n => n.ParentId == parentKey && n.Name == name, ct);
                if (node is null)
                {
                    node = new LibraryNode { Id = Guid.NewGuid(), ParentId = parentKey, Name = name };
                    db.Nodes.Add(node);
                }
                id = node.Id;
                cache[(parentKey, name)] = id;
            }
            parent = id;
        }
        return parent;
    }

    // Очистка библиотеки перед раскладкой заново (scan?replace=true): группы,
    // членство и мета — включая записи файлов, которых сейчас нет на хостах.
    // Вызывается только из прохода, поэтому никто параллельно не пишет.
    private async Task ClearLibraryAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var items = await db.NodeItems.ToListAsync(ct);
        var meta = await db.Meta.ToListAsync(ct);
        var nodes = await db.Nodes.ToListAsync(ct);

        db.NodeItems.RemoveRange(items);
        db.Meta.RemoveRange(meta);
        db.Nodes.RemoveRange(nodes);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Библиотека очищена перед раскладкой: группы {nodes}, членство {items}, мета {meta}",
            nodes.Count, items.Count, meta.Count);
    }

    // Шаблоны пишутся без расширения файла — иначе каждый заканчивался бы «.mkv|.avi|…».
    private static string WithoutExtension(string relativePath) =>
        System.IO.Path.ChangeExtension(relativePath, null);
}
