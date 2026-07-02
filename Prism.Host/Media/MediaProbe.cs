using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Prism.Host.Media;

/// <summary>Обёртка над CLI ffprobe для извлечения метаданных контейнера/кодеков/длительности.</summary>
public sealed class MediaProbe
{
    private readonly FFTools _tools;
    private readonly ILogger<MediaProbe> _logger;

    public MediaProbe(FFTools tools, ILogger<MediaProbe> logger)
    {
        _tools = tools;
        _logger = logger;
    }

    public async Task<MediaInfo?> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        if (_tools.FfprobePath is null)
            return null;

        var psi = new ProcessStartInfo(_tools.FfprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-print_format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-show_format");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add(filePath);

        try
        {
            using var proc = Process.Start(psi)!;
            var json = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("ffprobe завершился с ошибкой для {file} (код {code})", filePath, proc.ExitCode);
                return null;
            }

            return Parse(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка ffprobe для {file}", filePath);
            return null;
        }
    }

    private static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? container = null;
        double duration = 0;
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("format_name", out var fn))
                container = fn.GetString();
            if (format.TryGetProperty("duration", out var d) &&
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                duration = parsed;
        }

        string? videoCodec = null;
        int width = 0, height = 0;
        var audioTracks = new List<AudioTrack>();
        var subtitleTracks = new List<SubtitleTrack>();

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;

                if (type == "video" && videoCodec is null)
                {
                    videoCodec = codec;
                    if (s.TryGetProperty("width", out var w)) width = w.GetInt32();
                    if (s.TryGetProperty("height", out var h)) height = h.GetInt32();

                    // Иногда длительность лежит в потоке, а не в секции format.
                    if (duration <= 0 && s.TryGetProperty("duration", out var sd) &&
                        double.TryParse(sd.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pd))
                        duration = pd;
                }
                else if (type == "audio")
                {
                    var channels = s.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0;
                    audioTracks.Add(new AudioTrack(audioTracks.Count, codec,
                        Tag(s, "language"), Tag(s, "title"), channels));
                }
                else if (type == "subtitle")
                {
                    subtitleTracks.Add(new SubtitleTrack(subtitleTracks.Count, codec,
                        Tag(s, "language"), Tag(s, "title"), IsTextSubtitle(codec)));
                }
            }
        }

        var first = audioTracks.FirstOrDefault();

        return new MediaInfo
        {
            DurationSeconds = duration,
            Container = container,
            VideoCodec = videoCodec,
            AudioCodec = first?.Codec,
            Width = width,
            Height = height,
            AudioChannels = first?.Channels ?? 0,
            AudioTracks = audioTracks,
            SubtitleTracks = subtitleTracks,
        };
    }

    private static string? Tag(JsonElement stream, string name)
    {
        if (stream.TryGetProperty("tags", out var tags) &&
            tags.TryGetProperty(name, out var v))
            return v.GetString();
        return null;
    }

    // Текстовые субтитры можно извлечь в WebVTT; графические (PGS/VOBSUB/DVB) — нет.
    private static readonly HashSet<string> TextSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text", "stl",
        "subviewer", "subviewer1", "microdvd", "mpl2", "pjs", "realtext", "sami", "jacosub", "vplayer",
    };

    private static bool IsTextSubtitle(string? codec) => codec is not null && TextSubtitleCodecs.Contains(codec);
}
