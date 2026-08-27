using Prism.Mqtt;

namespace Prism.Library;

/// <summary>
/// API плееров: список видимых в MQTT плееров и запуск воспроизведения. Клиенты
/// (веб, Android-контроллер, ThinkingHome) про MQTT не знают: резолв
/// «mediaId → абсолютный URL хоста» и публикацию команды делает библиотека.
/// </summary>
public static class PlayerEndpoints
{
    public static void MapPlayerEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Список плееров -------------------------------------------------------
        // online — эвристика по свежести retained-сообщений (см. PlayerRegistry).
        app.MapGet("/api/players", (PlayerRegistry registry) =>
        {
            if (!registry.Configured)
                return Results.Problem("MQTT-брокер не настроен (секция Mqtt).", statusCode: 503);

            return Results.Json(registry.Snapshot().Select(p => new
            {
                id = p.Id,
                name = p.Name,
                online = p.Online,
                lastSeenSecondsAgo = p.LastSeenSecondsAgo,
                status = p.Status,
                url = p.Url,
                positionSec = p.PositionSec,
                durationSec = p.DurationSec,
            }));
        });

        // ---- Включить файл на плеере ----------------------------------------------
        // Библиотека резолвит mediaId в абсолютный streamUrl хоста-владельца
        // (адресно, через бухгалтерию) и публикует open. Ответ 202 — команда
        // опубликована; доехала ли она до плеера, покажет его state (команды не
        // retained, оффлайн-плеер её не получит — online в /api/players в помощь).
        app.MapPost("/api/players/{id}/open",
            async (string id, OpenInput input, PlayerRegistry registry, HostCatalog catalog, CancellationToken ct) =>
        {
            if (!registry.Configured)
                return Results.Problem("MQTT-брокер не настроен (секция Mqtt).", statusCode: 503);
            if (string.IsNullOrWhiteSpace(input.MediaId))
                return Results.BadRequest("Не задан mediaId.");
            if (!registry.Knows(id))
                return Results.NotFound($"Плеер '{id}' не встречался в MQTT.");

            var dto = await catalog.GetItemAsync(input.MediaId, ct);
            if (dto is null)
                return Results.NotFound($"Файл '{input.MediaId}' не найден ни на одном доступном хосте.");
            if ((string?)dto["streamUrl"] is not { Length: > 0 } url)
                return Results.BadRequest($"Файл сейчас не воспроизводим (streamType: {(string?)dto["streamType"]}).");

            if (!registry.IsConnected)
                return Results.Problem("MQTT-брокер недоступен.", statusCode: 503);
            await registry.OpenAsync(id, url, ct);
            return Results.Accepted();
        });
    }
}

/// <summary>Входная модель запуска: id файла из каталога библиотеки.</summary>
public sealed record OpenInput(string MediaId);
