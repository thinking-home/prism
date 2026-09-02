using System.Collections.Concurrent;
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

    // Списки компонентов ffmpeg (декодеры/кодировщики) не меняются за время жизни
    // процесса — бинарь один и тот же. Разобранный список кэшируется по флагу,
    // чтобы resolve каждого файла не запускал «ffmpeg -decoders» заново.
    private readonly ConcurrentDictionary<string, Lazy<Task<HashSet<string>?>>> _components = new();

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

        // 2. Рядом с приложением: инсталлятор Windows кладёт вложенную сборку
        //    в подпапку ffmpeg/. Приоритетнее PATH — это та версия, с которой
        //    приложение поставлялось.
        foreach (var dir in (string[])[Path.Combine(AppContext.BaseDirectory, "ffmpeg"), AppContext.BaseDirectory])
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate))
                return candidate;
        }

        // 3. Поиск по всем каталогам из PATH.
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

        // 4. Стандартные места установки, которых может не быть в PATH службы.
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
    public Task<bool> HasDecoderAsync(string codecName, CancellationToken ct = default) =>
        HasComponentAsync("-decoders", codecName, ct);

    /// <summary>Проверяет, есть ли у ffmpeg кодировщик с указанным именем (например, libfdk_aac).</summary>
    public Task<bool> HasEncoderAsync(string encoderName, CancellationToken ct = default) =>
        HasComponentAsync("-encoders", encoderName, ct);

    private async Task<bool> HasComponentAsync(string listFlag, string name, CancellationToken ct)
    {
        if (FfmpegPath is null || string.IsNullOrWhiteSpace(name))
            return false;

        // Список намеренно читается без привязки к токену запроса: результат общий
        // для всех вызовов (как ffprobe в ResolveCoreAsync).
        var ffmpeg = FfmpegPath;
        var set = await _components
            .GetOrAdd(listFlag, f => new Lazy<Task<HashSet<string>?>>(() => ListComponentsAsync(ffmpeg, f)))
            .Value;

        if (set is null)
        {
            // Запуск ffmpeg не удался — неудачу не кэшируем, следующий вызов попробует снова.
            _components.TryRemove(listFlag, out _);
            return false;
        }
        return set.Contains(name);
    }

    private async Task<HashSet<string>?> ListComponentsAsync(string ffmpegPath, string listFlag)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add(listFlag);

            using var proc = Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            // Строки выглядят так: " V..... h264   H.264 / AVC ..." — имя во 2-й колонке.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Length >= 1)
                    names.Add(parts[1]);
            }

            _logger.LogDebug("Получен список компонентов ffmpeg {flag}: {count}", listFlag, names.Count);
            return names;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось получить список компонентов ffmpeg ({flag})", listFlag);
            return null;
        }
    }
}
