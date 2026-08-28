using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Prism.Library;

/// <summary>
/// Идентичность файлов библиотеки: что сейчас лежит на хостах и куда «переехало»
/// содержимое. Id файла — ключ содержимого (fingerprint: размер + хеш краёв),
/// поэтому записи, привязанные к id, переживают переименование и перенос файлов.
/// Единственная реализация — HttpMediaIdentity ниже, поверх опроса хостов.
/// </summary>
public interface IMediaIdentity
{
    /// <summary>Файлы текущего скана: id + относительный путь. Id — для флага
    /// наличия и сборки мусора, путь — для правил автозаполнения.</summary>
    Task<IReadOnlyCollection<MediaFile>> GetLiveFilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Куда «переехало» содержимое: если последний известный путь файла
    /// <paramref name="missingId"/> теперь занят другим содержимым (файл докачался
    /// или перезаписан), вернуть его текущий id; иначе null.
    /// </summary>
    Task<string?> FindSuccessorAsync(string missingId, CancellationToken ct = default);
}

/// <summary>Файл библиотеки: ключ содержимого + путь относительно корня своей
/// медиапапки (прямые слеши, без ведущего слеша) — одинаковый на всех ОС.</summary>
public sealed record MediaFile(string Id, string RelativePath);

/// <summary>Хост Prism из конфига библиотеки (секция "Hosts").</summary>
public sealed class HostEntry
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
}

/// <summary>
/// Идентичность файлов для обслуживания библиотеки (ремап, правила, present).
/// Живые файлы берутся из агрегированного каталога (<see cref="HostCatalog"/> —
/// единственный компонент, опрашивающий хосты): недоступный хост выпадает из
/// объединения, его файлы становятся present:false, записи не трогаются —
/// «хост недоступен» и «файла нет на диске» — одно штатное состояние.
///
/// Преемник осиротевшего id (/api/media/{id}/successor) спрашивается ТОЛЬКО у
/// хостов, видевших файл по бухгалтерии «id → хост»; опроса всех хостов ради
/// отдельного файла нет ни при каких обстоятельствах (решение пользователя):
/// id без записей в бухгалтерии остаётся без ремапа. Недоступный хост =
/// пропуск: ремап откладывается до следующего прохода, а не ошибается.
/// </summary>
public sealed class HttpMediaIdentity(IHttpClientFactory httpFactory, HostCatalog catalog,
    FileHostLedger ledger, ILogger<HttpMediaIdentity> logger) : IMediaIdentity
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyCollection<MediaFile>> GetLiveFilesAsync(CancellationToken ct = default) =>
        (await catalog.GetMergedAsync(ct)).Select(i => new MediaFile(i.Id, i.RelativePath)).ToArray();

    public async Task<string?> FindSuccessorAsync(string missingId, CancellationToken ct = default)
    {
        var names = await ledger.HostsForAsync(missingId, ct);
        if (names.Count == 0)
        {
            logger.LogDebug("Для {id} нет бухгалтерии хостов — поиск преемника пропущен", missingId);
            return null;
        }

        var client = httpFactory.CreateClient(nameof(HttpMediaIdentity));
        client.Timeout = HostCatalog.HostTimeout;
        foreach (var host in catalog.Hosts.Where(h => names.Contains(h.Name)))
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

    private sealed record SuccessorResponse(string Id);
}
