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

    /// <summary>
    /// Длина одной сессии транскодирования в минутах. Один процесс ffmpeg выдаёт
    /// непрерывный (бесшовный по звуку) диапазон такой длины и завершается; разрыв
    /// звука возможен только на границе сессий. Больше — реже разрывы, но больше
    /// расход CPU/диска на сессию.
    /// </summary>
    public int SessionMinutes { get; set; } = 15;

    /// <summary>
    /// Начальный «бёрст» быстрого транскодирования (сек): ffmpeg сначала быстро
    /// выдаёт этот запас (быстрая перемотка/старт), а дальше читает вход со скоростью
    /// в <see cref="MaxPlaybackRate"/> раз выше реального времени. Это не даёт процессу
    /// транскодировать всю сессию вперёд на полной скорости и снижает нагрузку на CPU.
    /// 0 — без пейсинга (макс. скорость).
    /// </summary>
    public int BufferBurstSeconds { get; set; } = 30;

    /// <summary>
    /// Максимальная скорость воспроизведения, которую должен «догонять» транскод.
    /// После бёрста сессия производит сегменты с этой кратностью к реальному времени,
    /// поэтому воспроизведение вплоть до этой скорости (напр. 2x) не буксует. Должна
    /// быть не меньше самой быстрой скорости в плеере. Общий объём работы кодирования
    /// от этого не растёт — меняется лишь плотность во времени.
    /// </summary>
    public double MaxPlaybackRate { get; set; } = 2.0;

    /// <summary>
    /// Через сколько секунд простоя (никто не запрашивает её сегменты) сессия
    /// транскодирования убивается фоновым уборщиком, освобождая CPU.
    /// </summary>
    public int SessionIdleSeconds { get; set; } = 25;

    /// <summary>Пресет скорости/качества кодирования x264/x265.</summary>
    public string EncoderPreset { get; set; } = "veryfast";

    /// <summary>Constant Rate Factor (качество, меньше = лучше/больше).</summary>
    public int Crf { get; set; } = 23;

    /// <summary>Битрейт аудио AAC в кбит/с.</summary>
    public int AudioBitrateKbps { get; set; } = 256;

    /// <summary>Частота дискретизации аудио на выходе, Гц.</summary>
    public int AudioSampleRate { get; set; } = 48000;
}
