using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Prism.Host.Media;

/// <summary>
/// Находит установленные в системе бинари ffmpeg / ffprobe и сообщает, доступно ли
/// транскодирование в принципе. «Если кодек установлен в системе» на практике
/// означает: ffmpeg присутствует и имеет декодер для исходного кодека.
/// </summary>
public sealed class FFTools
{
    private readonly ILogger<FFTools> _logger;

    public string? FfmpegPath { get; }
    public string? FfprobePath { get; }

    /// <summary>True, если в системе найдены и ffmpeg, и ffprobe.</summary>
    public bool Available => FfmpegPath is not null && FfprobePath is not null;

    public FFTools(PlayerOptions options, ILogger<FFTools> logger)
    {
        _logger = logger;
        FfmpegPath = Resolve("ffmpeg", options.FfmpegPath);
        FfprobePath = Resolve("ffprobe", options.FfprobePath);

        if (Available)
            _logger.LogInformation("Найден инструментарий ffmpeg: {ffmpeg} / {ffprobe}", FfmpegPath, FfprobePath);
        else
            _logger.LogWarning("ffmpeg/ffprobe не найдены в системе. Транскодирование отключено; " +
                               "напрямую можно отдавать только уже браузерные файлы.");
    }

    private string? Resolve(string tool, string? configured)
    {
        // 1. Явный путь из конфигурации.
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? configured : null;

        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? tool + ".exe" : tool;

        // 2. Поиск по всем каталогам из PATH.
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Некорректный элемент PATH — пропускаем.
            }
        }

        // 3. Стандартные места установки, которых может не быть в PATH службы.
        string[] common =
        [
            "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin",
            "/snap/bin", "/var/lib/snapd/snap/bin",
        ];
        foreach (var dir in common)
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Проверяет, есть ли у ffmpeg декодер для указанного кодека, то есть
    /// «установлен ли в системе» тот кодек, которым сжат файл.
    /// </summary>
    public async Task<bool> HasDecoderAsync(string codecName, CancellationToken ct = default)
    {
        if (FfmpegPath is null || string.IsNullOrWhiteSpace(codecName))
            return false;

        try
        {
            var psi = new ProcessStartInfo(FfmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-decoders");

            using var proc = Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            // Строки декодеров выглядят так: " V..... h264   H.264 / AVC ..."
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Length >= 1 &&
                    string.Equals(parts[1], codecName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось получить список декодеров ffmpeg");
        }

        return false;
    }
}
