using System.Net;
using System.Text.Json.Nodes;

namespace Prism.Library;

/// <summary>Запись агрегированного каталога: DTO хоста как есть (с абсолютными
/// URL и атрибуцией хоста) + разобранные поля для внутренних нужд.</summary>
public sealed record CatalogItem(string Id, string RelativePath, JsonObject Dto);

/// <summary>
/// Агрегированный каталог: единственный компонент, опрашивающий /api/media
/// хостов. Список — параллельный опрос всех хостов из конфига со слиянием по id
/// (копия одного контента на двух хостах — одна запись; выигрывает хост,
/// стоящий в конфиге раньше). DTO хоста передаётся клиенту как есть, но
/// streamUrl становится абсолютным (указывает на хост-владельца — стрим идёт
/// с хоста напрямую), и добавляются поля host (имя) и hostUrl (базовый URL —
/// из него клиент строит остальные относительные ручки хоста, напр. субтитры).
/// Карточка одного файла — адресно, по бухгалтерии «id → хост»: все хосты ради
/// отдельного файла не опрашиваются. Недоступный хост выпадает из списка (его
/// файлы отсутствуют, записи библиотеки не трогаются) — то же состояние, что
/// «файла нет на диске».
/// </summary>
public sealed class HostCatalog(IHttpClientFactory httpFactory, IReadOnlyList<HostEntry> hosts,
    FileHostLedger ledger, ILogger<HostCatalog> logger)
{
    // Хосты отвечают на /api/media из кэша мгновенно (п.14 шаг 1), поэтому таймаут
    // короткий: мёртвый хост не должен подвешивать список дольше этих секунд.
    internal static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(5);

    // Нормализованная копия конфига: пустые записи выбрасываются, слеш на конце
    // не мешает склейке путей, безымянный хост представляется своим URL.
    private readonly IReadOnlyList<HostEntry> _hosts = Normalize(hosts);

    /// <summary>Хосты из конфига (нормализованные) — общие для всех HTTP-компонентов.</summary>
    internal IReadOnlyList<HostEntry> Hosts => _hosts;

    internal static IReadOnlyList<HostEntry> Normalize(IReadOnlyList<HostEntry> hosts) => hosts
        .Where(h => !string.IsNullOrWhiteSpace(h.BaseUrl))
        .Select(h => new HostEntry
        {
            Name = string.IsNullOrWhiteSpace(h.Name) ? h.BaseUrl.TrimEnd('/') : h.Name,
            BaseUrl = h.BaseUrl.TrimEnd('/'),
        })
        .ToArray();

    /// <summary>Слитый список файлов всех доступных хостов; попутно пополняет
    /// бухгалтерию «id → хост».</summary>
    public async Task<IReadOnlyList<CatalogItem>> GetMergedAsync(CancellationToken ct = default)
    {
        var client = CreateClient();

        // Хосты опрашиваются параллельно: мёртвый хост стоит один таймаут, а не сумму.
        var polls = _hosts.Select(async host =>
        {
            try
            {
                var text = await client.GetStringAsync(host.BaseUrl + "/api/media", ct);
                return (host, items: JsonNode.Parse(text)?.AsArray() ?? []);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Хост {name} ({url}) недоступен: {message}", host.Name, host.BaseUrl, ex.Message);
                return (host, items: new JsonArray());
            }
        });
        var results = await Task.WhenAll(polls); // порядок результатов = порядок конфига

        var merged = new List<CatalogItem>();
        var seen = new HashSet<string>();
        var pairs = new List<(string Id, string Host)>();
        foreach (var (host, items) in results)
        {
            foreach (var node in items)
            {
                if (node is not JsonObject dto || (string?)dto["id"] is not { Length: > 0 } id) continue;
                pairs.Add((id, host.Name));
                if (!seen.Add(id)) continue; // копия на другом хосте — уже взята

                Absolutize(dto, host);
                merged.Add(new CatalogItem(id, (string?)dto["relativePath"] ?? "", dto));
            }
        }

        await ledger.RecordAsync(pairs, ct);
        return merged;
    }

    /// <summary>Карточка одного файла — адресно, только с хостов, видевших его по
    /// бухгалтерии. null — файл неизвестен или его хосты недоступны.</summary>
    public async Task<JsonObject?> GetItemAsync(string id, CancellationToken ct = default)
    {
        var names = await ledger.HostsForAsync(id, ct);
        var client = CreateClient();
        foreach (var host in _hosts.Where(h => names.Contains(h.Name)))
        {
            try
            {
                using var response = await client.GetAsync(
                    $"{host.BaseUrl}/api/media/{Uri.EscapeDataString(id)}", ct);
                if (response.StatusCode == HttpStatusCode.NotFound) continue; // файл уехал с этого хоста
                response.EnsureSuccessStatusCode();

                if (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) is not JsonObject dto) continue;
                Absolutize(dto, host);
                return dto;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Хост {name} ({url}): не удалось получить карточку {id}: {message}",
                    host.Name, host.BaseUrl, id, ex.Message);
            }
        }
        return null;
    }

    // Абсолютные URL и атрибуция хоста-владельца в DTO.
    private static void Absolutize(JsonObject dto, HostEntry host)
    {
        if ((string?)dto["streamUrl"] is { } url && url.StartsWith('/'))
            dto["streamUrl"] = host.BaseUrl + url;
        dto["host"] = host.Name;
        dto["hostUrl"] = host.BaseUrl;
    }

    private HttpClient CreateClient()
    {
        var client = httpFactory.CreateClient(nameof(HostCatalog));
        client.Timeout = HostTimeout;
        return client;
    }
}
