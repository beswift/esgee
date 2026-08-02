using System.Drawing;
using Esgee.Ui;
using DrawingRect = System.Drawing.Rectangle;

namespace Esgee.Capture;

/// <summary>
/// esgee's own capture source: hotkey → frozen frame → overlay → the same
/// pipeline the clipboard watcher feeds. No Snipping Tool anywhere in the loop,
/// so there's no background process to wedge.
/// </summary>
public sealed class CaptureController
{
    private readonly ShelfWindow _shelf;
    private readonly Settings _settings;
    private OverlayWindow? _overlay;

    // True from timed-capture countdown start until its overlay closes, so a
    // second hotkey mid-fuse can't stack a parallel capture flow.
    private bool _timing;

    /// <summary>Raised on the UI thread with the finished capture.</summary>
    public event Action<CapturedImage>? Captured;

    public CaptureController(ShelfWindow shelf, Settings settings)
        => (_shelf, _settings) = (shelf, settings);

    public async void Begin()
    {
        Log.Info("region capture requested");
        if (Busy) { Log.Info("capture already in progress; ignoring"); return; }

        try
        {
            await OpenOverlayAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"capture begin failed: {ex}");
            _overlay = null;
        }
    }

    /// <summary>Hotkey version of the overlay's 1–9 delay: fixed fuse, then the
    /// region overlay opens on a frame frozen at zero.</summary>
    public async void BeginTimed()
    {
        Log.Info("timed capture requested");
        if (Busy) return;

        _timing = true;
        try
        {
            await RunDelayedAsync(Math.Clamp(_settings.TimerSeconds, 1, 60));
        }
        catch (Exception ex)
        {
            Log.Error($"timed capture failed: {ex}");
        }
        finally
        {
            _timing = false;
        }
    }

    private bool Busy => _timing || _overlay is not null;

    /// <summary>Whole screen, zero ceremony: no overlay, straight to the pipeline.</summary>
    public async void BeginFullscreen()
    {
        Log.Info("fullscreen capture requested");
        if (Busy) return; // mid-selection; don't photograph the overlay

        try
        {
            var frame = await GrabFrameAsync();
            Captured?.Invoke(CapturedImage.FromBitmap(frame, DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            Log.Error($"fullscreen capture failed: {ex}");
        }
    }

    /// <summary>Re-shoots the last committed selection rect — fresh pixels, same
    /// frame. The ShareX trick for iterating on the same UI area. Falls back to
    /// the region overlay when there's no remembered rect.</summary>
    public async void BeginLastRegion()
    {
        Log.Info("last-region capture requested");
        if (Busy) return;

        if (_settings.LastRegion is not { Length: 4 } last)
        {
            Log.Info("no last region stored; opening overlay instead");
            Begin();
            return;
        }

        try
        {
            var frame = await GrabFrameAsync();
            var vb = VirtualBounds();
            var rect = new DrawingRect(last[0] - vb.X, last[1] - vb.Y, last[2], last[3]);
            rect.Intersect(new DrawingRect(0, 0, frame.Width, frame.Height));

            // Monitors may have been rearranged since the rect was saved.
            if (rect.Width < 1 || rect.Height < 1)
            {
                Log.Warn("stored last region is off-screen; opening overlay instead");
                frame.Dispose();
                Begin();
                return;
            }

            var crop = frame.Clone(rect, frame.PixelFormat);
            frame.Dispose();
            Captured?.Invoke(CapturedImage.FromBitmap(crop, DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            Log.Error($"last-region capture failed: {ex}");
        }
    }

    private async Task OpenOverlayAsync()
    {
        var frame = await GrabFrameAsync();
        var bounds = VirtualBounds();

        var overlay = new OverlayWindow(frame, bounds);
        _overlay = overlay;

        overlay.Captured += (crop, frameRect) =>
        {
            Finish();
            frame.Dispose();

            // Remember the spot for Ctrl+Shift+L, in screen coordinates so it
            // stays valid across sessions.
            _settings.LastRegion =
                [frameRect.X + bounds.X, frameRect.Y + bounds.Y, frameRect.Width, frameRect.Height];
            _settings.Save();

            // Ownership of the crop transfers to the pipeline, which disposes
            // the CapturedImage (and with it the bitmap) after encoding.
            Captured?.Invoke(CapturedImage.FromBitmap(crop, DateTimeOffset.Now));
        };

        overlay.Cancelled += () => { Finish(); frame.Dispose(); };

        overlay.DelayRequested += async seconds =>
        {
            Finish();
            frame.Dispose();
            // _timing covers the countdown gap where no overlay exists yet.
            _timing = true;
            try { await RunDelayedAsync(seconds); }
            finally { _timing = false; }
        };

        overlay.Show();
    }

    private async Task RunDelayedAsync(int seconds)
    {
        var pill = new CountdownWindow();
        pill.SetRemaining(seconds);
        pill.Show();

        try
        {
            for (var left = seconds; left > 0; left--)
            {
                pill.SetRemaining(left);
                await Task.Delay(1000);
            }
        }
        finally
        {
            pill.Close();
        }

        // Small settle so the pill is gone from the frame we grab.
        await Task.Delay(160);
        await OpenOverlayAsync();
    }

    /// <summary>Frozen frame of the whole virtual desktop, with esgee's own
    /// windows tucked out of the shot.</summary>
    private async Task<Bitmap> GrabFrameAsync()
    {
        var shelfWasVisible = _shelf.IsVisible;
        if (shelfWasVisible)
        {
            _shelf.Hide();
            await Task.Delay(140); // let the compositor actually remove it
        }

        try
        {
            var b = VirtualBounds();
            var bmp = new Bitmap(b.Width, b.Height);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(b.X, b.Y, 0, 0, bmp.Size);
            return bmp;
        }
        finally
        {
            if (shelfWasVisible) _shelf.Show();
        }
    }

    private static DrawingRect VirtualBounds()
    {
        var v = System.Windows.Forms.SystemInformation.VirtualScreen;
        return new DrawingRect(v.X, v.Y, v.Width, v.Height);
    }

    private void Finish() => _overlay = null;
}
