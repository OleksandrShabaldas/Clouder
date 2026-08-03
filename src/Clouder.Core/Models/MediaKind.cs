namespace Clouder.Core.Models;

/// <summary>Broad file categories used to filter and group transfers.</summary>
public enum MediaKind
{
    Document,
    Image,
    Video,
    Audio,
    Archive,
    Other
}

public static class MediaKindClassifier
{
    private static readonly Dictionary<string, MediaKind> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = MediaKind.Document, [".doc"] = MediaKind.Document, [".docx"] = MediaKind.Document,
        [".txt"] = MediaKind.Document, [".rtf"] = MediaKind.Document, [".odt"] = MediaKind.Document,
        [".md"] = MediaKind.Document, [".xls"] = MediaKind.Document, [".xlsx"] = MediaKind.Document,
        [".csv"] = MediaKind.Document, [".ppt"] = MediaKind.Document, [".pptx"] = MediaKind.Document,
        [".epub"] = MediaKind.Document, [".json"] = MediaKind.Document, [".xml"] = MediaKind.Document,

        [".jpg"] = MediaKind.Image, [".jpeg"] = MediaKind.Image, [".png"] = MediaKind.Image,
        [".gif"] = MediaKind.Image, [".bmp"] = MediaKind.Image, [".webp"] = MediaKind.Image,
        [".tif"] = MediaKind.Image, [".tiff"] = MediaKind.Image, [".svg"] = MediaKind.Image,
        [".heic"] = MediaKind.Image, [".raw"] = MediaKind.Image, [".psd"] = MediaKind.Image,

        [".mp4"] = MediaKind.Video, [".mkv"] = MediaKind.Video, [".avi"] = MediaKind.Video,
        [".mov"] = MediaKind.Video, [".wmv"] = MediaKind.Video, [".flv"] = MediaKind.Video,
        [".webm"] = MediaKind.Video, [".m4v"] = MediaKind.Video, [".mpg"] = MediaKind.Video,
        [".mpeg"] = MediaKind.Video,

        [".mp3"] = MediaKind.Audio, [".wav"] = MediaKind.Audio, [".flac"] = MediaKind.Audio,
        [".ogg"] = MediaKind.Audio, [".aac"] = MediaKind.Audio, [".m4a"] = MediaKind.Audio,
        [".wma"] = MediaKind.Audio, [".opus"] = MediaKind.Audio,

        [".zip"] = MediaKind.Archive, [".rar"] = MediaKind.Archive, [".7z"] = MediaKind.Archive,
        [".tar"] = MediaKind.Archive, [".gz"] = MediaKind.Archive, [".bz2"] = MediaKind.Archive,
        [".xz"] = MediaKind.Archive, [".iso"] = MediaKind.Archive
    };

    public static MediaKind Classify(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) return MediaKind.Other;
        return ByExtension.GetValueOrDefault(ext, MediaKind.Other);
    }

    public static string DisplayName(MediaKind kind) => kind switch
    {
        MediaKind.Document => "Documents",
        MediaKind.Image => "Images",
        MediaKind.Video => "Videos",
        MediaKind.Audio => "Audio",
        MediaKind.Archive => "Archives",
        _ => "Other"
    };

    /// <summary>Segoe Fluent icon glyph for the category.</summary>
    public static string Glyph(MediaKind kind) => kind switch
    {
        MediaKind.Document => "",
        MediaKind.Image => "",
        MediaKind.Video => "",
        MediaKind.Audio => "",
        MediaKind.Archive => "",
        _ => ""
    };
}
