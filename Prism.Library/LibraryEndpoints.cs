using Microsoft.EntityFrameworkCore;
using Prism.Abstractions;

namespace Prism.Library;

/// <summary>
/// HTTP API библиотеки: дерево групп, членство файлов (по ключу содержимого),
/// свободная мета ключ-значение файлов и групп, обслуживание и сборка мусора.
/// Перенесено из плагина Prism.Plugins.Library без изменения семантики; пути
/// ручек сохранены — клиенты переезжают со сменой только базового URL.
/// </summary>
public static class LibraryEndpoints
{
    public static void MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Всё дерево одним запросом: группы (с метой) + членство --------------
        // Библиотека домашняя — пагинация не нужна, дерево строит клиент.
        // present=false — запись о файле, которого сейчас нет ни на одном доступном
        // хосте (файла нет на диске или хост недоступен — одно состояние): это
        // штатно, автоматически такие записи не удаляются (см. gc).
        app.MapGet("/api/library/tree",
            async (IDbContextFactory<LibraryDbContext> factory, IMediaIdentity identity, CancellationToken ct) =>
        {
            var live = (await identity.GetLiveFilesAsync(ct)).Select(f => f.Id).ToHashSet();

            await using var db = await factory.CreateDbContextAsync(ct);
            var nodes = await db.Nodes.ToListAsync(ct);
            var items = await db.NodeItems.ToListAsync(ct);
            var meta = await db.Meta.Where(m => m.EntityType == MetaEntity.Node).ToListAsync(ct);
            var metaByNode = meta.ToLookup(m => m.EntityKey);

            return Results.Json(new
            {
                nodes = nodes.Select(n => new
                {
                    id = n.Id,
                    parentId = n.ParentId,
                    name = n.Name,
                    meta = metaByNode[n.Id.ToString()].ToDictionary(m => m.Key, m => m.Value),
                }),
                items = items.Select(i => new
                {
                    nodeId = i.NodeId,
                    mediaId = i.FileKey,
                    present = live.Contains(i.FileKey),
                }),
            });
        });

        // ---- Группы --------------------------------------------------------------
        app.MapPost("/api/library/nodes",
            async (NodeInput input, IDbContextFactory<LibraryDbContext> factory, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.BadRequest("Имя группы не может быть пустым.");

            await using var db = await factory.CreateDbContextAsync(ct);
            if (input.ParentId is Guid parent && !await db.Nodes.AnyAsync(n => n.Id == parent, ct))
                return Results.BadRequest("Родительская группа не существует.");

            var node = new LibraryNode { Id = Guid.NewGuid(), ParentId = input.ParentId, Name = input.Name.Trim() };
            db.Nodes.Add(node);
            await db.SaveChangesAsync(ct);
            return Results.Json(new { id = node.Id, parentId = node.ParentId, name = node.Name });
        });

        // Переименование и/или перенос ветки (перенос = смена ParentId).
        app.MapPut("/api/library/nodes/{id:guid}",
            async (Guid id, NodeInput input, IDbContextFactory<LibraryDbContext> factory, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.BadRequest("Имя группы не может быть пустым.");

            await using var db = await factory.CreateDbContextAsync(ct);
            var node = await db.Nodes.FindAsync([id], ct);
            if (node is null) return Results.NotFound();

            if (input.ParentId is Guid parent)
            {
                // Группа не может переехать под саму себя или своего потомка.
                var all = await db.Nodes.ToDictionaryAsync(n => n.Id, ct);
                if (!all.ContainsKey(parent))
                    return Results.BadRequest("Родительская группа не существует.");
                for (Guid? p = parent; p is Guid cur; p = all.TryGetValue(cur, out var n) ? n.ParentId : null)
                    if (cur == id) return Results.BadRequest("Нельзя перенести группу внутрь самой себя.");
            }

            node.Name = input.Name.Trim();
            node.ParentId = input.ParentId;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Удаление группы с потомками, их членством и метой. Файлы не трогаются —
        // они просто выпадают из удалённых групп.
        app.MapDelete("/api/library/nodes/{id:guid}",
            async (Guid id, IDbContextFactory<LibraryDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var all = await db.Nodes.ToListAsync(ct);
            if (all.All(n => n.Id != id)) return Results.NotFound();

            // Собираем поддерево обходом в ширину (дерево целиком уже в памяти).
            var doomed = new HashSet<Guid> { id };
            for (var added = true; added;)
            {
                added = false;
                foreach (var n in all)
                    if (n.ParentId is Guid p && doomed.Contains(p) && doomed.Add(n.Id))
                        added = true;
            }

            var doomedKeys = doomed.Select(d => d.ToString()).ToArray();
            db.Nodes.RemoveRange(all.Where(n => doomed.Contains(n.Id)));
            db.NodeItems.RemoveRange(db.NodeItems.Where(i => doomed.Contains(i.NodeId)));
            db.Meta.RemoveRange(db.Meta.Where(m => m.EntityType == MetaEntity.Node && doomedKeys.Contains(m.EntityKey)));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- Членство файлов в группах -------------------------------------------
        // Существование файла не проверяется намеренно: осиротевшие связи — штатное
        // состояние (хост отключён, файл переезжает); чистка — явной командой (gc).
        app.MapPut("/api/library/nodes/{id:guid}/items/{mediaId}",
            async (Guid id, string mediaId, IDbContextFactory<LibraryDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!await db.Nodes.AnyAsync(n => n.Id == id, ct)) return Results.NotFound();
            if (!await db.NodeItems.AnyAsync(i => i.NodeId == id && i.FileKey == mediaId, ct))
            {
                db.NodeItems.Add(new NodeItemRecord { NodeId = id, FileKey = mediaId });
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        });

        app.MapDelete("/api/library/nodes/{id:guid}/items/{mediaId}",
            async (Guid id, string mediaId, IDbContextFactory<LibraryDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!await db.Nodes.AnyAsync(n => n.Id == id, ct)) return Results.NotFound();
            var row = await db.NodeItems.FindAsync([id, mediaId], ct);
            if (row is not null)
            {
                db.NodeItems.Remove(row);
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        });

        // ---- Мета файла (id файла и есть ключ содержимого) -----------------------
        // Существование файла не проверяется — мету можно класть и «впрок»
        // (например, до подключения хоста); осиротевшую уберёт gc.
        app.MapPut("/api/media/{id}/meta",
            async (string id, Dictionary<string, string?> input,
                IDbContextFactory<LibraryDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await MergeMetaAsync(db, MetaEntity.File, id, input, ct);
            return Results.NoContent();
        });

        app.MapDelete("/api/media/{id}/meta",
            async (string id, IDbContextFactory<LibraryDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            db.Meta.RemoveRange(db.Meta.Where(m => m.EntityType == MetaEntity.File && m.EntityKey == id));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- Мета группы ---------------------------------------------------------
        app.MapPut("/api/library/nodes/{id:guid}/meta",
            async (Guid id, Dictionary<string, string?> input,
                IDbContextFactory<LibraryDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!await db.Nodes.AnyAsync(n => n.Id == id, ct)) return Results.NotFound();
            await MergeMetaAsync(db, MetaEntity.Node, id.ToString(), input, ct);
            return Results.NoContent();
        });

        app.MapDelete("/api/library/nodes/{id:guid}/meta",
            async (Guid id, IDbContextFactory<LibraryDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!await db.Nodes.AnyAsync(n => n.Id == id, ct)) return Results.NotFound();
            var key = id.ToString();
            db.Meta.RemoveRange(db.Meta.Where(m => m.EntityType == MetaEntity.Node && m.EntityKey == key));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- Прогнать обслуживание немедленно -------------------------------------
        // Будит фоновый цикл (ремап + правила автозаполнения), чтобы не ждать
        // таймер — например, сразу после добавления файлов. Завершения не ждёт:
        // итоги прохода пишутся в лог.
        // ?replace=true — режим отладки правил: библиотека очищается здесь же (это
        // несколько DELETE, ждать нечего), а дальше обычный проход раскладывает её
        // заново. Поэтому замена возможна только с ручки: фоновый цикл про неё не
        // знает и сам ничего не удаляет.
        app.MapPost("/api/library/scan",
            async (LibraryMaintenanceService maintenance, IDbContextFactory<LibraryDbContext> factory,
                ILoggerFactory loggers, bool? replace, CancellationToken ct) =>
        {
            if (replace == true) await ClearLibraryAsync(factory, loggers.CreateLogger(typeof(LibraryEndpoints)), ct);
            maintenance.RequestScan();
            return Results.Accepted();
        });

        // ---- Сборка мусора: удалить записи файлов, которых нет на диске ----------
        // Только по явной команде — автоматической чистки нет намеренно.
        app.MapPost("/api/library/gc",
            async (IMediaIdentity identity, IDbContextFactory<LibraryDbContext> factory, CancellationToken ct) =>
        {
            var live = (await identity.GetLiveFilesAsync(ct)).Select(f => f.Id).ToHashSet();

            await using var db = await factory.CreateDbContextAsync(ct);
            var deadItems = (await db.NodeItems.ToListAsync(ct)).Where(i => !live.Contains(i.FileKey)).ToList();
            var deadMeta = (await db.Meta.Where(m => m.EntityType == MetaEntity.File).ToListAsync(ct))
                .Where(m => !live.Contains(m.EntityKey)).ToList();

            db.NodeItems.RemoveRange(deadItems);
            db.Meta.RemoveRange(deadMeta);
            await db.SaveChangesAsync(ct);
            return Results.Json(new { removedItems = deadItems.Count, removedMeta = deadMeta.Count });
        });
    }

    // Полная очистка библиотеки: группы, членство и мета — и файлов, и групп.
    // Записи файлов, которых сейчас нет на диске, тоже удаляются: это отладка
    // правил, а не бережное обновление, поэтому мета такого файла не вернётся,
    // пока его хост/диск не подключат обратно.
    private static async Task ClearLibraryAsync(IDbContextFactory<LibraryDbContext> factory,
        ILogger logger, CancellationToken ct)
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

    // Слияние меты: значение null удаляет ключ, остальные — вставка/замена.
    private static async Task MergeMetaAsync(LibraryDbContext db, string entityType, string entityKey,
        Dictionary<string, string?> input, CancellationToken ct)
    {
        var names = input.Keys.ToArray();
        var existing = await db.Meta
            .Where(m => m.EntityType == entityType && m.EntityKey == entityKey && names.Contains(m.Key))
            .ToListAsync(ct);

        foreach (var (name, value) in input)
        {
            var row = existing.FirstOrDefault(r => r.Key == name);
            if (value is null)
            {
                if (row is not null) db.Meta.Remove(row);
            }
            else if (row is null)
            {
                db.Meta.Add(new MetaRecord { EntityType = entityType, EntityKey = entityKey, Key = name, Value = value });
            }
            else
            {
                row.Value = value;
            }
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Входная модель группы (создание и правка).</summary>
public sealed record NodeInput(Guid? ParentId, string Name);
