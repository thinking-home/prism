using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Prism.Host.Media;

/// <summary>
/// Извлекает выбранную текстовую дорожку субтитров в WebVTT (по запросу, с
/// кэшированием во временный файл) и нарезает её на HLS-сегменты. Графические
/// субтитры (PGS/VOBSUB/DVB) не поддерживаются — их нельзя сконвертировать в текст.
/// </summary>
public sealed class SubtitleService
{
    private readonly FFTools _tools;
    private readonly ILogger<SubtitleService> _logger;
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _cache = new();
    // Разобранные реплики дорожки — для нарезки на сегменты.
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<Cue>?>>> _cues = new();

    /// <summary>Одна реплика: границы по времени и блок WebVTT дословно.</summary>
    private sealed record Cue(double StartSec, double EndSec, string Block);

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

    /// <summary>
    /// WebVTT-сегмент дорожки для HLS: реплики, пересекающие окно сегмента
    /// <paramref name="index"/>, с заголовком <c>X-TIMESTAMP-MAP</c> — без него
    /// ExoPlayer не привязывает реплики к шкале времени и не показывает их.
    /// Времена реплик — глобальные (шкала фильма), как и PTS сегментов видео,
    /// поэтому отображение MPEGTS:0 ↔ LOCAL:0. null — дорожку нельзя извлечь.
    /// </summary>
    public async Task<string?> GetVttSegmentAsync(MediaItem item, int subIndex, double segmentSeconds, int index)
    {
        var key = $"{item.Id}-{subIndex}";
        var cues = await _cues.GetOrAdd(key,
            _ => new Lazy<Task<IReadOnlyList<Cue>?>>(() => ParseTrackAsync(item, subIndex, key))).Value;
        if (cues is null) return null;

        var from = index * segmentSeconds;
        var to = from + segmentSeconds;

        var sb = new StringBuilder();
        sb.Append("WEBVTT\n");
        sb.Append("X-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000\n");
        foreach (var cue in cues)
        {
            // Реплика, пересекающая границу, попадает в оба сегмента — плееры
            // склеивают её по совпадающим временам, это штатно для HLS.
            if (cue.EndSec <= from || cue.StartSec >= to) continue;
            sb.Append('\n').Append(cue.Block).Append('\n');
        }
        return sb.ToString();
    }

    private async Task<IReadOnlyList<Cue>?> ParseTrackAsync(MediaItem item, int subIndex, string cacheKey)
    {
        var path = await GetVttPathAsync(item, subIndex);
        if (path is null)
        {
            _cues.TryRemove(cacheKey, out _); // неуспех не кэшируем — можно повторить
            return null;
        }

        try
        {
            return ParseCues(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось разобрать WebVTT дорожки {idx} из {file}", subIndex, item.FileName);
            _cues.TryRemove(cacheKey, out _);
            return null;
        }
    }

    // Простой разбор WebVTT: блоки разделены пустой строкой; репликой считается
    // блок со строкой таймингов «start --> end» (заголовок WEBVTT, NOTE и STYLE
    // пропускаются). Блок сохраняется дословно — настройки позиций не теряются.
    private static List<Cue> ParseCues(string vtt)
    {
        var cues = new List<Cue>();
        foreach (var raw in vtt.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var block = raw.Trim('\n');
            var lines = block.Split('\n');
            var timing = Array.FindIndex(lines, l => l.Contains("-->"));
            if (timing < 0) continue;

            var parts = lines[timing].Split("-->", 2);
            if (parts.Length < 2 ||
                !TryParseTime(parts[0], out var start) || !TryParseTime(parts[1], out var end))
                continue;

            cues.Add(new Cue(start, end, block));
        }
        return cues;
    }

    // «HH:MM:SS.mmm» или «MM:SS.mmm»; после времени могут идти настройки реплики.
    private static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        var token = text.Trim().Split(' ', 2)[0];
        var parts = token.Split(':');
        if (parts.Length is < 2 or > 3) return false;

        double result = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return false;
            result = result * 60 + value;
        }
        seconds = result;
        return true;
    }

    // Неуспех не кэшируем навсегда — убираем запись, чтобы можно было повторить.
    private string? Fail(string key)
    {
        _cache.TryRemove(key, out _);
        return null;
    }
}
