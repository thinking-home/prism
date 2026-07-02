using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Prism.Host.Media;

/// <summary>
/// Извлекает выбранную текстовую дорожку субтитров в WebVTT (по запросу, с
/// кэшированием во временный файл). Графические субтитры (PGS/VOBSUB/DVB) не
/// поддерживаются — их нельзя сконвертировать в текст.
/// </summary>
public sealed class SubtitleService
{
    private readonly FFTools _tools;
    private readonly ILogger<SubtitleService> _logger;
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _cache = new();

    public SubtitleService(FFTools tools, ILogger<SubtitleService> logger)
    {
        _tools = tools;
        _logger = logger;
        _dir = Path.Combine(Path.GetTempPath(), "prism-subs");
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Путь к .vtt-файлу дорожки или null, если её нельзя извлечь.</summary>
    public Task<string?> GetVttPathAsync(MediaItem item, int subIndex)
    {
        var key = $"{item.Id}-{subIndex}";
        return _cache.GetOrAdd(key, _ => new Lazy<Task<string?>>(() => ExtractAsync(item, subIndex, key))).Value;
    }

    private async Task<string?> ExtractAsync(MediaItem item, int subIndex, string key)
    {
        var info = item.Info;
        if (_tools.FfmpegPath is null || info is null ||
            subIndex < 0 || subIndex >= info.SubtitleTracks.Count ||
            !info.SubtitleTracks[subIndex].TextBased)
            return Fail(key);

        var outPath = Path.Combine(_dir, key + ".vtt");

        var psi = new ProcessStartInfo(_tools.FfmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var a = psi.ArgumentList;
        a.Add("-nostdin"); a.Add("-hide_banner"); a.Add("-loglevel"); a.Add("error");
        a.Add("-y");
        a.Add("-i"); a.Add(item.Path);
        a.Add("-map"); a.Add($"0:s:{subIndex}");
        a.Add("-f"); a.Add("webvtt");
        a.Add(outPath);

        try
        {
            using var proc = new Process { StartInfo = psi };
            var stderr = new StringBuilder();
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            proc.Start();
            proc.BeginErrorReadLine();
            // Без токена запроса: операция общая и кэшируемая, прерванный запрос
            // (например, при переключении дорожки) не должен её отменять.
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0 && File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                return outPath;

            _logger.LogWarning("Не удалось извлечь субтитры {idx} из {file}: {err}",
                subIndex, item.FileName, stderr.ToString().Trim());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка извлечения субтитров {idx} из {file}", subIndex, item.FileName);
        }

        return Fail(key);
    }

    // Неуспех не кэшируем навсегда — убираем запись, чтобы можно было повторить.
    private string? Fail(string key)
    {
        _cache.TryRemove(key, out _);
        return null;
    }
}
