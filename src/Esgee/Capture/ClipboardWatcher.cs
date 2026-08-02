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
    private readonly HwndSource _sink;
    private DateTimeOffset _ignoreUntil = DateTimeOffset.MinValue;
    private string? _lastHash;
    private DateTimeOffset _lastAt = DateTimeOffset.MinValue;

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

        // A single capture can raise WM_CLIPBOARDUPDATE more than once as the
        // source app publishes each format. Collapse those by content.
        var hash = Hash(capture);
        var now = DateTimeOffset.Now;
        if (hash == _lastHash && now - _lastAt < TimeSpan.FromSeconds(3))
        {
            capture.Dispose();
            return;
        }
        _lastHash = hash;
        _lastAt = now;

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
