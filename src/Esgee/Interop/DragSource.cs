using System.IO;
using System.Windows;
using Esgee.Store;

namespace Esgee.Interop;

/// <summary>
/// The reason esgee exists. ShareX closed this exact request as not-planned
/// (ShareX#6991) and nothing on Windows ships the macOS floating-thumbnail
/// drag-out, so we do it ourselves.
/// </summary>
public static class DragSource
{
    /// <summary>Private clipboard format stamped on every DataObject esgee
    /// writes. The clipboard watcher skips content carrying it — the only
    /// self-echo signal that works ACROSS processes: a standalone
    /// `esgee --archive` window can't reach the resident watcher's
    /// IgnoreNextChange, and without this its "Copy to clipboard" on a share
    /// or peer item re-enters the resident pipeline as a fresh capture
    /// (docs/SHARES.md: nothing lands in your archive unless you pull it).</summary>
    public const string ClipboardMarker = "esgee.internal";

    /// <summary>
    /// Offers the shot in three formats at once so any drop target is satisfied:
    /// Explorer and file pickers take CF_HDROP, image-native apps take the PNG
    /// stream, and older editors fall back to a bitmap.
    /// </summary>
    public static DataObject BuildDataObject(Shot shot)
    {
        var data = new DataObject();
        data.SetData(ClipboardMarker, "1"); // drop targets ignore private formats

        // Recordings: CF_HDROP with the GIF when one exists — that's the thing
        // you paste into a chat — else the MP4. (The MP4 is always reachable via
        // the card's folder button; both files sit side by side.) No PNG/bitmap
        // formats: offering a still frame would make image-first paste targets
        // silently take a frame instead of the clip.
        if (shot.IsVideo)
        {
            data.SetData(DataFormats.FileDrop, new[] { shot.GifPath ?? shot.Path });
            return data;
        }

        // CF_HDROP. Cheap because the file is already on disk — Microsoft warns
        // that rendering data during the drag loop stalls the cursor, so we
        // never do work here.
        data.SetData(DataFormats.FileDrop, new[] { shot.Path });

        // Preferred by chat clients, editors, and browsers.
        data.SetData("PNG", new MemoryStream(File.ReadAllBytes(shot.Path)));

        // Last-resort format for apps that only understand CF_DIB.
        try
        {
            data.SetImage(LoadFrozen(shot.Path));
        }
        catch (Exception ex)
        {
            Log.Warn($"bitmap format unavailable for drag: {ex.Message}");
        }

        return data;
    }

    private static System.Windows.Media.Imaging.BitmapSource LoadFrozen(string path)
    {
        var bmp = new System.Windows.Media.Imaging.BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(path);
        // OnLoad so we don't hold a lock on a file the user may move or delete.
        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
