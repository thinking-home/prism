namespace Prism.Host.Media;

/// <summary>Аудиодорожка файла. <see cref="Index"/> — порядковый номер среди
/// аудиопотоков (для ffmpeg <c>-map 0:a:Index</c>).</summary>
public sealed record AudioTrack(int Index, string? Codec, string? Language, string? Title, int Channels);

/// <summary>Дорожка субтитров. <see cref="Index"/> — порядковый номер среди
/// субтитров (для <c>-map 0:s:Index</c>). <see cref="TextBased"/> — можно ли
/// извлечь в WebVTT (текстовые), иначе это графические субтитры (PGS/VOBSUB).</summary>
public sealed record SubtitleTrack(int Index, string? Codec, string? Language, string? Title, bool TextBased);

/// <summary>Результат анализа медиафайла утилитой ffprobe.</summary>
public sealed record MediaInfo
{
    public required double DurationSeconds { get; init; }
    public required string? Container { get; init; }
    public required string? VideoCodec { get; init; }
    public required string? AudioCodec { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int AudioChannels { get; init; }
    public IReadOnlyList<AudioTrack> AudioTracks { get; init; } = [];
    public IReadOnlyList<SubtitleTrack> SubtitleTracks { get; init; } = [];
    public bool HasAudio => AudioCodec is not null;

    /// <summary>
    /// True, если браузер может воспроизвести файл как есть (без транскодирования):
    /// контейнер mp4/webm с браузерной комбинацией видео+аудио кодеков.
    /// </summary>
    public bool IsBrowserNative
    {
        get
        {
            string[] nativeContainers = ["mov", "mp4", "m4a", "3gp", "3g2", "mj2", "webm", "matroska,webm"];
            string[] nativeVideo = ["h264", "vp8", "vp9", "av1"];
            string[] nativeAudio = ["aac", "mp3", "opus", "vorbis"];

            // ffprobe сообщает matroska и webm одним именем формата; браузерным
            // является только настоящий WebM, а дёшево отличить их нельзя, поэтому
            // всё семейство matroska безопаснее считать требующим ремукса.
            var container = Container ?? "";
            bool containerOk = nativeContainers.Contains(container) && container != "matroska,webm";
            bool videoOk = VideoCodec is not null && nativeVideo.Contains(VideoCodec);
            bool audioOk = !HasAudio || nativeAudio.Contains(AudioCodec!);

            return containerOk && videoOk && audioOk;
        }
    }
}
