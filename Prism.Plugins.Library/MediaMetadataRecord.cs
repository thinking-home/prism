namespace Prism.Plugins.Library;

/// <summary>Запись метаданных: фильм (Title) или эпизод (SeriesTitle+Season+Episode).</summary>
public class MediaMetadataRecord
{
    public string MediaId { get; set; } = "";
    public string Kind { get; set; } = "movie"; // "movie" | "episode"
    public string? Title { get; set; }
    public string? SeriesTitle { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
}
