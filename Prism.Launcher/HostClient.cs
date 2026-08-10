using System.Net;
using System.Net.Http;
using System.Text.Json;
using Prism.Common;

namespace Prism.Launcher;

/// <summary>Результат резолва файла хостом (подмножество записи /api/media).</summary>
public sealed record ResolvedMedia(string Id, string? StreamUrl, bool Playable, string Title);

/// <summary>
/// Клиент к API хоста Prism: по пути файла считает отпечаток (Prism.Common) и
/// спрашивает <c>/api/resolve</c> — есть ли такой файл в библиотеке и какой у него
/// URL потока. Идентификация по содержимому, путь на хост не уходит. Базовый
/// адрес один (из настроек) — по нему ходит и лаунчер, и приставка, поэтому это
/// должен быть адрес хоста в локальной сети.
/// </summary>
public sealed class HostClient(string baseUrl)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private string Base => baseUrl.TrimEnd('/');

    /// <summary>Абсолютный URL потока для приставки: базовый адрес хоста + относительный streamUrl.</summary>
    public string AbsoluteStreamUrl(string streamUrl) => $"{Base}{streamUrl}";

    /// <summary>Папки, которые раздаёт хост (из /api/info) — для понятного сообщения. Пусто, если хост недоступен.</summary>
    public async Task<IReadOnlyList<string>> GetMediaDirectoriesAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync($"{Base}/api/info", ct);
            if (!resp.IsSuccessStatusCode) return [];
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("mediaDirectories", out var dirs) || dirs.ValueKind != JsonValueKind.Array)
                return [];
            return dirs.EnumerateArray()
                .Select(d => d.GetString())
                .Where(d => !string.IsNullOrEmpty(d))
                .ToArray()!;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Резолвит файл. Возвращает <c>null</c>, если хост его не раздаёт (404).</summary>
    public async Task<ResolvedMedia?> ResolveAsync(string filePath, CancellationToken ct = default)
    {
        var fp = MediaFingerprinter.Compute(filePath);
        var url = $"{Base}/api/resolve?size={fp.Size}&fingerprint={fp.Hash}";

        using var resp = await Http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var streamUrl = root.TryGetProperty("streamUrl", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
        return new ResolvedMedia(
            root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            streamUrl,
            root.TryGetProperty("playable", out var p) && p.GetBoolean(),
            root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "");
    }
}
