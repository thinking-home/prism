using Microsoft.EntityFrameworkCore;

namespace Prism.Library;

/// <summary>
/// Бухгалтерия «id файла → имена видевших его хостов» (Prism_FileHost) с кэшем
/// в памяти: загружается из БД один раз, дальше БД трогается только для
/// дозаписи новых пар — в установившемся режиме опросы ничего не пишут. Общая
/// для идентичности (адресный поиск преемника) и каталога (адресная карточка
/// файла): все хосты ради отдельного файла не опрашиваются ни при каких
/// обстоятельствах. Ошибка записи не роняет вызвавшего — бухгалтерия
/// пополнится следующим опросом.
/// </summary>
public sealed class FileHostLedger(IDbContextFactory<LibraryDbContext> factory, ILogger<FileHostLedger> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, HashSet<string>>? _hostsById;

    /// <summary>Имена хостов, видевших файл; пусто — файл бухгалтерии неизвестен.</summary>
    public async Task<IReadOnlyCollection<string>> HostsForAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var known = await EnsureLoadedAsync(ct);
            // Копия: живое множество может меняться параллельной дозаписью.
            return known.TryGetValue(id, out var names) ? [.. names] : [];
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Дозаписывает новые пары «id → хост»; уже известные пропускаются.</summary>
    public async Task RecordAsync(IEnumerable<(string Id, string Host)> pairs, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var known = await EnsureLoadedAsync(ct);
            var fresh = new List<FileHostRecord>();
            var seen = new HashSet<(string, string)>(); // дубли внутри самой пачки
            foreach (var (id, host) in pairs)
                if (seen.Add((id, host)) && (!known.TryGetValue(id, out var set) || !set.Contains(host)))
                    fresh.Add(new FileHostRecord { FileKey = id, Host = host });
            if (fresh.Count == 0) return;

            await using var db = await factory.CreateDbContextAsync(ct);
            db.FileHosts.AddRange(fresh);
            await db.SaveChangesAsync(ct);

            foreach (var r in fresh)
            {
                if (!known.TryGetValue(r.FileKey, out var set))
                    known[r.FileKey] = set = [];
                set.Add(r.Host);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Не удалось записать бухгалтерию «id → хост»");
        }
        finally
        {
            _lock.Release();
        }
    }

    // Вызывается только под _lock.
    private async Task<Dictionary<string, HashSet<string>>> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_hostsById is not null) return _hostsById;
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.FileHosts.ToListAsync(ct);
        return _hostsById = rows.GroupBy(r => r.FileKey)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Host).ToHashSet());
    }
}
