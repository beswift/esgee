namespace Esgee.Store;

/// <summary>One capture, already durable on disk by the time this exists.
/// <paramref name="Kind"/> is "image" (PNG) or "video" (MP4 recording).</summary>
public sealed record Shot(
    long Id,
    string Path,
    DateTimeOffset TakenAt,
    int Width,
    int Height,
    string Sha256,
    string Kind = "image",
    long DurationMs = 0)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public bool IsVideo => Kind == "video";

    /// <summary>What thumbnails should decode. For videos this is the frame
    /// ffmpeg extracted next to the MP4 ("....mp4.png" — a shape no real
    /// screenshot filename can collide with).</summary>
    public string ThumbPath => IsVideo ? Path + ".png" : Path;

    /// <summary>The sibling GIF, when the recording was short enough to get one.
    /// Checked live because the user may delete either file independently.</summary>
    public string? GifPath
    {
        get
        {
            if (!IsVideo) return null;
            var gif = System.IO.Path.ChangeExtension(Path, ".gif");
            return System.IO.File.Exists(gif) ? gif : null;
        }
    }

    public string DurationText
    {
        get
        {
            var t = TimeSpan.FromMilliseconds(DurationMs);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }
    }
}
