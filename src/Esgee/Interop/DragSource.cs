using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Esgee.Store;

namespace Esgee.Interop;

/// <summary>
/// The reason esgee exists. ShareX closed this exact request as not-planned
/// (ShareX#6991) and nothing on Windows ships the macOS floating-thumbnail
/// drag-out, so we do it ourselves.
/// </summary>
public static class DragSource
{
    /// <summary>
    /// Immutable, cross-thread-safe input to the tiny STA-only DataObject build.
    /// File I/O and WIC decode happen before this exists; the bitmap is frozen so
    /// callers may prepare on a worker and consume on the dispatcher.
    /// </summary>
    public sealed record PreparedTransfer(Shot Shot, string DropPath, byte[]? Png, BitmapSource? Bitmap);

    /// <summary>Private clipboard format stamped on every DataObject esgee
    /// writes. The clipboard watcher skips content carrying it — the only
    /// self-echo signal that works ACROSS processes: a standalone
    /// `esgee --archive` window can't reach the resident watcher's
    /// IgnoreNextChange, and without this its "Copy to clipboard" on a share
    /// or peer item re-enters the resident pipeline as a fresh capture
    /// (docs/SHARES.md: nothing lands in your archive unless you pull it).</summary>
    public const string ClipboardMarker = "esgee.internal";

    /// <summary>
    /// Compatibility entry point for CLI diagnostics. Interactive UI paths use
    /// PrepareAsync so this file read and decode never occupy the dispatcher.
    /// </summary>
    public static DataObject BuildDataObject(Shot shot)
        => BuildDataObject(Prepare(shot));

    /// <summary>Reads and decodes away from the dispatcher. Videos deliberately
    /// remain file-drop-only, exactly as before.</summary>
    public static Task<PreparedTransfer> PrepareAsync(Shot shot,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Prepare(shot, cancellationToken), cancellationToken);

    public static PreparedTransfer Prepare(Shot shot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (shot.IsVideo)
            return new PreparedTransfer(shot, shot.GifPath ?? shot.Path, null, null);

        var png = File.ReadAllBytes(shot.Path);
        cancellationToken.ThrowIfCancellationRequested();
        BitmapSource? bitmap = null;
        try { bitmap = LoadFrozen(png); }
        catch (Exception ex) { Log.Warn($"bitmap format unavailable for drag: {ex.Message}"); }
        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedTransfer(shot, shot.Path, png, bitmap);
    }

    /// <summary>Builds the multi-format OLE object without file I/O or decode.
    /// Call on the UI STA for drag/drop or immediately before a clipboard write.</summary>
    public static DataObject BuildDataObject(PreparedTransfer transfer)
    {
        var data = new DataObject();
        data.SetData(ClipboardMarker, "1"); // drop targets ignore private formats

        // Recordings: CF_HDROP with the GIF when one exists — that's the thing
        // you paste into a chat — else the MP4. No PNG/bitmap formats: offering
        // a still frame makes image-first targets silently take the frame.
        if (transfer.Shot.IsVideo)
        {
            data.SetData(DataFormats.FileDrop, new[] { transfer.DropPath });
            return data;
        }

        data.SetData(DataFormats.FileDrop, new[] { transfer.DropPath });
        data.SetData("PNG", new MemoryStream(transfer.Png!));

        // Last-resort format for apps that only understand CF_DIB.
        if (transfer.Bitmap is not null) data.SetImage(transfer.Bitmap);

        return data;
    }

    private static BitmapSource LoadFrozen(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = stream;
        // OnLoad so neither the byte stream nor the source file stays live.
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
