using System.Text.Json.Serialization;

namespace Prism.Host.Media;

/// <summary>Аудиодорожка файла. <see cref="Index"/> — номер в общем списке дорожек
/// (вшитые идут первыми, поэтому для них это же и номер потока — <c>-map 0:a:Index</c>).
/// <see cref="Path"/> — путь к отдельному аудиофайлу рядом с видео; null — дорожка
/// вшита в контейнер.</summary>
public sealed record AudioTrack(int Index, string? Codec, string? Language, string? Title, int Channels,
    string? Path = null);

/// <summary>Дорожка субтитров. <see cref="Index"/> — номер в общем списке дорожек
/// файла (вшитые идут первыми, поэтому для них это же и номер потока —
/// <c>-map 0:s:Index</c>). <see cref="TextBased"/> — можно ли извлечь в WebVTT
/// (текстовые), иначе это графические субтитры (PGS/VOBSUB). <see cref="Path"/> —
/// путь к отдельному файлу субтитров рядом с видео; null — дорожка вшита в контейнер.</summary>
public sealed record SubtitleTrack(int Index, string? Codec, string? Language, string? Title, bool TextBased,
    string? Path = null);

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
    // Вычисляемые свойства не пишем в персистентный кэш (MediaInfoCache).
    [JsonIgnore]
    public bool HasAudio => AudioCodec is not null;

    /// <summary>
    /// True, если браузер может воспроизвести файл как есть (без транскодирования):
    /// контейнер mp4/webm с браузерной комбинацией видео+аудио кодеков.
    /// </summary>
    [JsonIgnore]
    public bool IsBrowserNative
    {
        get
        {
            string[] nativeContainers = ["mov", "mp4", "m4a", "3gp", "3g2", "mj2", "webm"];
            string[] nativeVideo = ["h264", "vp8", "vp9", "av1"];
            string[] nativeAudio = ["aac", "mp3", "opus", "vorbis"];

            // ffprobe отдаёт format_name СПИСКОМ имён своего демуксера через запятую
            // (у mp4 это «mov,mp4,m4a,3gp,3g2,mj2»), поэтому сравнивать надо по
            // элементам, а не строку целиком.
            var names = (Container ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);

            // Matroska и WebM обслуживает один демуксер («matroska,webm»), а браузерным
            // является только настоящий WebM; дёшево отличить их нельзя, поэтому всё
            // семейство matroska считаем требующим ремукса.
            bool containerOk = names.Any(nativeContainers.Contains) && !names.Contains("matroska");
            bool videoOk = VideoCodec is not null && nativeVideo.Contains(VideoCodec);
            bool audioOk = !HasAudio || nativeAudio.Contains(AudioCodec!);

            return containerOk && videoOk && audioOk;
        }
    }
}
