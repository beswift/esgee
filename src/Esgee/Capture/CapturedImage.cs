using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Esgee.Capture;

/// <summary>
/// A capture that has left the clipboard but has not yet been encoded.
/// Splitting these two steps matters: clipboard reads must happen on the UI
/// thread, but PNG encoding a 3440x1440 grab takes long enough to drop frames,
/// so <see cref="ToPng"/> is meant to be called from a background thread.
/// </summary>
public sealed class CapturedImage : IDisposable
{
    private byte[]? _png;
    private readonly Bitmap? _bitmap;

    public int Width { get; }
    public int Height { get; }
    public DateTimeOffset TakenAt { get; }

    private CapturedImage(byte[]? png, Bitmap? bitmap, int w, int h, DateTimeOffset takenAt)
        => (_png, _bitmap, Width, Height, TakenAt) = (png, bitmap, w, h, takenAt);

    /// <summary>Preferred path: the source app already put real PNG bytes on the
    /// clipboard (Snipping Tool does), so we pass them through untouched.</summary>
    public static CapturedImage FromPngBytes(byte[] png, DateTimeOffset takenAt)
    {
        var (w, h) = ReadPngSize(png);
        return new CapturedImage(png, null, w, h, takenAt);
    }

    public static CapturedImage FromBitmap(Bitmap bmp, DateTimeOffset takenAt)
        => new(null, bmp, bmp.Width, bmp.Height, takenAt);

    /// <summary>Memoised — dedup hashing and the disk write both want these bytes,
    /// and encoding an ultrawide grab twice is a visible stall.</summary>
    public byte[] ToPng()
    {
        if (_png is not null) return _png;

        using var ms = new MemoryStream(capacity: 1 << 20);
        _bitmap!.Save(ms, ImageFormat.Png);
        return _png = ms.ToArray();
    }

    /// <summary>Pulls dimensions straight out of the IHDR chunk rather than
    /// decoding the whole image, which we'd otherwise pay for twice.</summary>
    private static (int W, int H) ReadPngSize(byte[] png)
    {
        // 8-byte signature, then a 4-byte length + "IHDR", then width/height BE.
        if (png.Length < 24) return (0, 0);
        var w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (w, h);
    }

    public void Dispose() => _bitmap?.Dispose();
}
