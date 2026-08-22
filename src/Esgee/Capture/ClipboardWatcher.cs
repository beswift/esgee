using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Interop;
using Esgee.Interop;

namespace Esgee.Capture;

/// <summary>
/// v1 capture source: rides whatever already puts an image on the clipboard —
/// Win+Shift+S above all. Costs the user zero new muscle memory, and everything
/// downstream (save, shelf, drag-out) is identical to what our own capture
/// overlay will feed in later.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int KnownHashCapacity = 64;

    private readonly HwndSource _sink;
    private DateTimeOffset _ignoreUntil = DateTimeOffset.MinValue;
    private readonly HashSet<string> _knownHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _knownOrder = new();

    /// <summary>Raised on the UI thread with an unencoded capture.</summary>
    public event Action<CapturedImage>? Captured;

    public ClipboardWatcher()
    {
        // A message-only window: no pixels, exists purely to receive
        // WM_CLIPBOARDUPDATE. Cheaper and far more reliable than polling.
        _sink = new HwndSource(new HwndSourceParameters("esgee.clipboard")
        {
            ParentWindow = (IntPtr)(-3), // HWND_MESSAGE
            Width = 0,
            Height = 0,
        });
        _sink.AddHook(WndProc);
        Win32.AddClipboardFormatListener(_sink.Handle);
    }

    /// <summary>
    /// Call immediately *before* esgee itself writes to the clipboard, so a
    /// re-copy from the shelf doesn't loop straight back in as a fresh capture.
    /// Window-based rather than a bare flag: if the write throws and no
    /// WM_CLIPBOARDUPDATE ever arrives, a stale flag would otherwise swallow the
    /// user's next real capture.
    /// </summary>
    public void IgnoreNextChange() => _ignoreUntil = DateTimeOffset.Now.AddMilliseconds(750);

    /// <summary>
    /// Call with the SHA-256 (hex) of PNG bytes esgee has already archived or
    /// put on the clipboard itself. Unlike the time-window guard, this never
    /// expires: dictation tools (Wispr Flow) and clipboard managers save the
    /// clipboard, paste their own text, then RESTORE the saved contents
    /// minutes later — the restore re-publishes our image without the private
    /// marker format, and only content identity can recognize it. Both this
    /// and WndProc run on the UI thread, so the collections need no locking.
    /// </summary>
    public void NoteKnownContent(string sha256)
    {
        if (string.IsNullOrEmpty(sha256) || !_knownHashes.Add(sha256)) return;
        _knownOrder.Enqueue(sha256);
        while (_knownOrder.Count > KnownHashCapacity) _knownHashes.Remove(_knownOrder.Dequeue());
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg != Win32.WM_CLIPBOARDUPDATE) return IntPtr.Zero;

        try { Handle(); }
        catch (Exception ex) { Log.Warn($"clipboard read failed: {ex.Message}"); }

        return IntPtr.Zero;
    }

    private void Handle()
    {
        // Deliberately NOT reset on first hit: one SetDataObject raises several
        // WM_CLIPBOARDUPDATEs (OleSetClipboard + flush), and consuming the guard
        // on the first let the second through as a phantom duplicate capture.
        if (DateTimeOffset.Now < _ignoreUntil) return;

        var capture = Read();
        if (capture is null) return;

        var hash = Hash(capture);

        // Content esgee already knows, coming back without the marker: a
        // clipboard save/restore cycle by another app (dictation tools do this
        // on every insertion). Not a capture, no matter how much later it is.
        // This also collapses the multiple WM_CLIPBOARDUPDATEs a single copy
        // raises as the source app publishes each format, which a 3-second
        // same-hash window used to handle.
        if (_knownHashes.Contains(hash))
        {
            Log.Info("clipboard: ignoring image esgee already captured (re-published or echoed)");
            capture.Dispose();
            return;
        }
        NoteKnownContent(hash);

        Captured?.Invoke(capture);
    }

    private static CapturedImage? Read()
    {
        // The clipboard is a shared, lockable resource — another app holding it
        // open makes this throw. Retrying briefly is the standard fix.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var now = DateTimeOffset.Now;

                // esgee's own writes carry a private marker format. The time
                // window above only covers THIS process; the marker is what
                // catches a copy made by a standalone `esgee --archive`
                // window, which must never re-enter this pipeline as a
                // capture (share browsing writes nothing without a pull).
                if (System.Windows.Clipboard.ContainsData(DragSource.ClipboardMarker))
                    return null;

                // Prefer real PNG bytes. Avoids WPF's long-standing CF_DIB alpha
                // bug, which renders screenshots fully transparent.
                if (System.Windows.Clipboard.ContainsData("PNG") &&
                    System.Windows.Clipboard.GetData("PNG") is Stream png)
                {
                    using (png)
                    {
                        using var ms = new MemoryStream();
                        png.CopyTo(ms);
                        if (ms.Length > 0) return CapturedImage.FromPngBytes(ms.ToArray(), now);
                    }
                }

                // Fallback via WinForms, which handles DIB more sanely than WPF.
                if (System.Windows.Forms.Clipboard.ContainsImage() &&
                    System.Windows.Forms.Clipboard.GetImage() is Bitmap bmp)
                {
                    return CapturedImage.FromBitmap(bmp, now);
                }

                return null; // Clipboard holds something, but not an image.
            }
            // COMException derives from ExternalException; this catches both.
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(20);
            }
        }

        Log.Warn("clipboard stayed locked across 10 attempts; dropping capture");
        return null;
    }

    private static string Hash(CapturedImage c)
        => Convert.ToHexString(SHA256.HashData(c.ToPng()));

    public void Dispose()
    {
        Win32.RemoveClipboardFormatListener(_sink.Handle);
        _sink.Dispose();
    }
}
