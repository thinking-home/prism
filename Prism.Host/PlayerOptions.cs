namespace Prism.Host;

/// <summary>
/// Конфигурация плеера во время выполнения. Читается из appsettings.json
/// (секция "Player"), может переопределяться переменными среды и командной строкой.
/// </summary>
public sealed class PlayerOptions
{
    /// <summary>Папка, которая сканируется на наличие медиафайлов для раздачи.</summary>
    public string MediaDirectory { get; set; } = "videos";

    /// <summary>Явный путь к бинарю ffmpeg. Пусто — автоопределение.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Явный путь к бинарю ffprobe. Пусто — автоопределение.</summary>
    public string? FfprobePath { get; set; }

    /// <summary>
    /// Целевой видеокодек, который получает браузер. "h264" (libx264, максимальная
    /// поддержка) или "h265" (libx265 / HEVC, меньше размер, но ограниченная
    /// поддержка браузерами).
    /// </summary>
    public string OutputCodec { get; set; } = "h264";

    /// <summary>Длина одного HLS-сегмента в секундах.</summary>
    public int SegmentSeconds { get; set; } = 6;

    /// <summary>Пресет скорости/качества кодирования x264/x265.</summary>
    public string EncoderPreset { get; set; } = "veryfast";

    /// <summary>Constant Rate Factor (качество, меньше = лучше/больше).</summary>
    public int Crf { get; set; } = 23;
}
