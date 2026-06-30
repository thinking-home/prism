using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Prism.Host.Media;

/// <summary>
/// Реализует VOD HLS «на лету». Плейлист рассчитывается заранее по длительности
/// файла (чтобы браузер мог перематывать в любую точку), а каждый сегмент .ts
/// создаётся по запросу: процесс ffmpeg перематывает к нужному месту исходника,
/// декодирует его и перекодирует окно в H264/H265 + AAC.
/// </summary>
public sealed class HlsTranscoder
{
    private readonly FFTools _tools;
    private readonly PlayerOptions _options;
    private readonly ILogger<HlsTranscoder> _logger;

    public HlsTranscoder(FFTools tools, PlayerOptions options, ILogger<HlsTranscoder> logger)
    {
        _tools = tools;
        _options = options;
        _logger = logger;
    }

    private double SegmentLength => Math.Max(1, _options.SegmentSeconds);

    public int SegmentCount(MediaInfo info) =>
        Math.Max(1, (int)Math.Ceiling(info.DurationSeconds / SegmentLength));

    /// <summary>Строит статический VOD-плейлист m3u8 для заданного файла.</summary>
    public string BuildPlaylist(MediaInfo info)
    {
        var seg = SegmentLength;
        var total = info.DurationSeconds;
        var count = SegmentCount(info);

        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:3\n");
        sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{(int)Math.Ceiling(seg)}\n");
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
        sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");

        for (var i = 0; i < count; i++)
        {
            var start = i * seg;
            var len = Math.Min(seg, total - start);
            if (len <= 0) len = seg;
            sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:{len.ToString("0.000", CultureInfo.InvariantCulture)},\n");
            sb.Append(CultureInfo.InvariantCulture, $"segment/{i}.ts\n");
        }

        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    /// <summary>
    /// Запускает ffmpeg для транскодирования сегмента <paramref name="index"/> и
    /// копирует полученный поток MPEG-TS в <paramref name="destination"/>.
    /// </summary>
    public async Task WriteSegmentAsync(MediaItem item, int index, Stream destination, CancellationToken ct)
    {
        if (_tools.FfmpegPath is null)
            throw new InvalidOperationException("ffmpeg недоступен.");

        var info = item.Info ?? throw new InvalidOperationException("Файл ещё не проанализирован.");
        var start = index * SegmentLength;
        var duration = SegmentLength;
        if (info.DurationSeconds > 0)
            duration = Math.Min(SegmentLength, Math.Max(0.1, info.DurationSeconds - start));

        var psi = new ProcessStartInfo(_tools.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        var a = psi.ArgumentList;
        a.Add("-nostdin");
        a.Add("-hide_banner");
        a.Add("-loglevel"); a.Add("error");

        // Быстрая перемотка входа + ограниченное окно чтения под этот сегмент.
        a.Add("-ss"); a.Add(start.ToString("0.000", CultureInfo.InvariantCulture));
        a.Add("-t"); a.Add(duration.ToString("0.000", CultureInfo.InvariantCulture));
        a.Add("-i"); a.Add(item.Path);

        // Берём первый видеопоток и, если есть, первый аудиопоток.
        a.Add("-map"); a.Add("0:v:0");
        if (info.HasAudio) { a.Add("-map"); a.Add("0:a:0?"); }

        // Кодирование видео в выбранный целевой кодек H264/H265.
        var (vcodec, extra) = VideoEncoderArgs();
        a.Add("-c:v"); a.Add(vcodec);
        foreach (var e in extra) a.Add(e);
        a.Add("-preset"); a.Add(_options.EncoderPreset);
        a.Add("-crf"); a.Add(_options.Crf.ToString(CultureInfo.InvariantCulture));
        a.Add("-pix_fmt"); a.Add("yuv420p");

        // Аудио в стерео AAC — повсеместно поддерживается браузерами.
        if (info.HasAudio)
        {
            a.Add("-c:a"); a.Add("aac");
            a.Add("-b:a"); a.Add("160k");
            a.Add("-ac"); a.Add("2");
        }

        // Сдвигаем таймстемпы каждого сегмента на его глобальную позицию, чтобы
        // плеер склеивал сегменты в единую непрерывную шкалу времени.
        a.Add("-output_ts_offset"); a.Add(start.ToString("0.000", CultureInfo.InvariantCulture));
        a.Add("-muxdelay"); a.Add("0");
        a.Add("-muxpreload"); a.Add("0");
        a.Add("-f"); a.Add("mpegts");
        a.Add("pipe:1");

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!proc.Start())
            throw new InvalidOperationException("Не удалось запустить ffmpeg.");
        proc.BeginErrorReadLine();

        try
        {
            await proc.StandardOutput.BaseStream.CopyToAsync(destination, 64 * 1024, ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
                _logger.LogWarning("ffmpeg для сегмента {idx} файла {file} завершился с кодом {code}: {err}",
                    index, item.FileName, proc.ExitCode, stderr.ToString().Trim());
        }
        catch (OperationCanceledException)
        {
            // Клиент перемотал/закрыл соединение: оперативно останавливаем транскод.
            TryKill(proc);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка стриминга сегмента {idx} файла {file}: {err}",
                index, item.FileName, stderr.ToString().Trim());
            TryKill(proc);
        }
    }

    private (string codec, string[] extra) VideoEncoderArgs()
    {
        return _options.OutputCodec.ToLowerInvariant() switch
        {
            // HEVC; тег hvc1 — то, что ожидают Safari/HLS.
            "h265" or "hevc" => ("libx265", ["-tag:v", "hvc1"]),
            // H.264 high profile, широко совместим с hls.js и нативными плеерами.
            _ => ("libx264", ["-profile:v", "high", "-level", "4.1"]),
        };
    }

    private void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* по возможности */ }
    }
}
