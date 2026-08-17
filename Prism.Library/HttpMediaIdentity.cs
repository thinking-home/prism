using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prism.Abstractions;

namespace Prism.Library;

/// <summary>Хост Prism из конфига библиотеки (секция "Hosts").</summary>
public sealed class HostEntry
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
}

/// <summary>
/// Идентичность файлов поверх HTTP API хостов из конфига. Живые файлы — слияние
/// /api/media всех доступных хостов (дедуп по id: копия одного контента на двух
/// хостах — одна запись). Недоступный хост просто выпадает из объединения — его
/// файлы становятся present:false, записи не трогаются: «хост недоступен» и
/// «файла нет на диске» — одно штатное состояние.
///
/// Попутно ведётся бухгалтерия «id → хост» (Prism_FileHost): при опросе
/// запоминается, какие хосты видели файл, поэтому преемник осиротевшего id
/// (/api/media/{id}/successor) спрашивается ТОЛЬКО у знавших его хостов —
/// обычно это один запрос. Опроса всех хостов ради отдельного файла нет ни при
/// каких обстоятельствах (решение пользователя): id без записей в бухгалтерии
/// остаётся без ремапа. Недоступный хост = пропуск: ремап откладывается до
/// следующего прохода, а не ошибается.
/// </summary>
public sealed class HttpMediaIdentity(IHttpClientFactory httpFactory,
    IReadOnlyList<HostEntry> hosts, IDbContextFactory<LibraryDbContext> factory,
    ILogger<HttpMediaIdentity> logger) : IMediaIdentity
{
    // Хосты отвечают на /api/media из кэша мгновенно (п.14 шаг 1), поэтому таймаут
    // короткий: мёртвый хост не должен подвешивать дерево дольше этих секунд.
    private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Нормализованная копия конфига: пустые записи выбрасываются, слеш на конце
    // не мешает склейке путей, безымянный хост представляется своим URL.
    private readonly IReadOnlyList<HostEntry> _hosts = hosts
        .Where(h => !string.IsNullOrWhiteSpace(h.BaseUrl))
        .Select(h => new HostEntry
        {
            Name = string.IsNullOrWhiteSpace(h.Name) ? h.BaseUrl.TrimEnd('/') : h.Name,
            BaseUrl = h.BaseUrl.TrimEnd('/'),
        })
        .ToArray();

    // Бухгалтерия в памяти (id → имена видевших хостов): загружается из БД один
    // раз, дальше БД трогается только для дозаписи новых пар — в установившемся
    // режиме опрос не пишет ничего.
    private readonly SemaphoreSlim _bookkeeping = new(1, 1);
    private Dictionary<string, HashSet<string>>? _hostsById;

    public async Task<IReadOnlyCollection<MediaFile>> GetLiveFilesAsync(CancellationToken ct = default)
    {
        var client = CreateClient();

        // Хосты опрашиваются параллельно: мёртвый хост стоит один таймаут, а не сумму.
        var polls = _hosts.Select(async host =>
        {
            try
            {
                var files = await client.GetFromJsonAsync<HostFile[]>(host.BaseUrl + "/api/media", Json, ct) ?? [];
                return (host, files);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Хост {name} ({url}) недоступен: {message}", host.Name, host.BaseUrl, ex.Message);
                return (host, files: []);
            }
        });
        var results = await Task.WhenAll(polls);

        var byId = new Dictionary<string, MediaFile>();
        foreach (var (_, files) in results)
            foreach (var f in files)
                byId.TryAdd(f.Id, new MediaFile(f.Id, f.RelativePath)); // копии между хостами схлопываются

        await RecordHostsAsync(results, ct);
        return byId.Values;
    }

    public async Task<string?> FindSuccessorAsync(string missingId, CancellationToken ct = default)
    {
        // Строго адресный опрос: только хосты, которые видели этот файл. Если
        // записей нет (id старше бухгалтерии — например, БД времён плагина),
        // преемник не ищется вовсе — решение пользователя: ни при каких
        // обстоятельствах не опрашивать все хосты ради отдельного файла. Такие
        // орфаны остаются present:false до ручной правки или gc.
        var known = await EnsureLoadedAsync(ct);
        if (!known.TryGetValue(missingId, out var names))
        {
            logger.LogDebug("Для {id} нет бухгалтерии хостов — поиск преемника пропущен", missingId);
            return null;
        }
        var candidates = _hosts.Where(h => names.Contains(h.Name)).ToArray();

        var client = CreateClient();
        foreach (var host in candidates)
        {
            try
            {
                using var response = await client.GetAsync(
                    $"{host.BaseUrl}/api/media/{Uri.EscapeDataString(missingId)}/successor", ct);
                if (response.StatusCode == HttpStatusCode.NotFound) continue;
                response.EnsureSuccessStatusCode();

                var successor = await response.Content.ReadFromJsonAsync<SuccessorResponse>(Json, ct);
                if (!string.IsNullOrEmpty(successor?.Id)) return successor.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Хост {name} ({url}): не удалось спросить преемника {id}: {message}",
                    host.Name, host.BaseUrl, missingId, ex.Message);
            }
        }
        return null;
    }

    // Дозаписывает новые пары «id → хост». Ошибка записи не роняет опрос:
    // бухгалтерия — оптимизация, без неё сработает фолбэк опроса всех.
    private async Task RecordHostsAsync((HostEntry host, HostFile[] files)[] results, CancellationToken ct)
    {
        var known = await EnsureLoadedAsync(ct);
        await _bookkeeping.WaitAsync(ct);
        try
        {
            var fresh = new List<FileHostRecord>();
            foreach (var (host, files) in results)
                foreach (var f in files)
                    if (!known.TryGetValue(f.Id, out var set) || !set.Contains(host.Name))
                        fresh.Add(new FileHostRecord { FileKey = f.Id, Host = host.Name });
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
            _bookkeeping.Release();
        }
    }

    private async Task<Dictionary<string, HashSet<string>>> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_hostsById is not null) return _hostsById;
        await _bookkeeping.WaitAsync(ct);
        try
        {
            if (_hostsById is null)
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                var rows = await db.FileHosts.ToListAsync(ct);
                _hostsById = rows.GroupBy(r => r.FileKey)
                    .ToDictionary(g => g.Key, g => g.Select(r => r.Host).ToHashSet());
            }
            return _hostsById;
        }
        finally
        {
            _bookkeeping.Release();
        }
    }

    private HttpClient CreateClient()
    {
        var client = httpFactory.CreateClient(nameof(HttpMediaIdentity));
        client.Timeout = HostTimeout;
        return client;
    }

    /// <summary>Часть DTO /api/media хоста, нужная библиотеке; остальные поля игнорируются.</summary>
    private sealed record HostFile(string Id, string RelativePath);

    private sealed record SuccessorResponse(string Id);
}
