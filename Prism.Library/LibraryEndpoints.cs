using Microsoft.EntityFrameworkCore;

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
        // ---- Агрегированный каталог: слияние хостов + мета ------------------------
        // DTO хостов отдаются как есть (абсолютные streamUrl, host/hostUrl —
        // см. HostCatalog), сверху подмешивается мета из БД: ключи меты побеждают
        // одноимённые поля хоста (например, title) — как было у плагина.
        app.MapGet("/api/media",
            async (HostCatalog catalog, IDbContextFactory<LibraryDbContext> factory, CancellationToken ct) =>
        {
            var items = await catalog.GetMergedAsync(ct);

            await using var db = await factory.CreateDbContextAsync(ct);
            // Вся мета файлов одним запросом: библиотека домашняя, а фильтр по
            // сотням id раздул бы SQL сильнее, чем таблица.
            var meta = await db.Meta.Where(m => m.EntityType == MetaEntity.File).ToListAsync(ct);
            var metaById = meta.ToLookup(m => m.EntityKey);
            foreach (var item in items)
                foreach (var m in metaById[item.Id])
                    item.Dto[m.Key] = m.Value;

            return Results.Json(items.Select(i => i.Dto));
        });

        // ---- Карточка одного файла (адресно через бухгалтерию «id → хост») -------
        app.MapGet("/api/media/{id}",
            async (string id, HostCatalog catalog, IDbContextFactory<LibraryDbContext> factory, CancellationToken ct) =>
        {
            var dto = await catalog.GetItemAsync(id, ct);
            if (dto is null) return Results.NotFound();

            await using var db = await factory.CreateDbContextAsync(ct);
            var meta = await db.Meta
                .Where(m => m.EntityType == MetaEntity.File && m.EntityKey == id)
                .ToListAsync(ct);
            foreach (var m in meta)
                dto[m.Key] = m.Value;

            return Results.Json(dto);
        });

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
        // ?replace=true — режим отладки правил: очистку выполняет сам цикл в начале
        // следующего прохода. Чистить здесь нельзя: проход мог уже идти, и удаление
        // из-под него оставляло группы с несуществующим родителем и членство на
        // удалённые id (у прохода в кэше остаются id только что удалённых групп).
        app.MapPost("/api/library/scan", (LibraryMaintenanceService maintenance, bool? replace) =>
        {
            maintenance.RequestScan(replace == true);
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
            // Бухгалтерия «id → хост» мёртвых файлов тоже забывается: gc — это
            // явное «забыть эти файлы», после него преемника искать не для чего.
            var deadHosts = (await db.FileHosts.ToListAsync(ct)).Where(r => !live.Contains(r.FileKey)).ToList();

            db.NodeItems.RemoveRange(deadItems);
            db.Meta.RemoveRange(deadMeta);
            db.FileHosts.RemoveRange(deadHosts);
            await db.SaveChangesAsync(ct);
            return Results.Json(new
            {
                removedItems = deadItems.Count,
                removedMeta = deadMeta.Count,
                removedHostLinks = deadHosts.Count,
            });
        });
    }

    // Полная очистка библиотеки: группы, членство и мета — и файлов, и групп.
    // Записи файлов, которых сейчас нет на диске, тоже удаляются: это отладка
    // правил, а не бережное обновление, поэтому мета такого файла не вернётся,
    // пока его хост/диск не подключат обратно.
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
