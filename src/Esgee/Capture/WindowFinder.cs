using Esgee.Interop;

namespace Esgee.Capture;

/// <summary>
/// Snapshot of the top-level windows at overlay-open time, in z-order, so
/// hovering can snap the selection to whole windows — the capture mode
/// Win+Shift+S hides behind a toolbar click.
/// </summary>
internal static class WindowFinder
{
    private static readonly string[] ExcludedClasses =
        ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow"];

    public readonly record struct WindowRect(IntPtr Hwnd, Win32.RECT Bounds);

    /// <summary>Top-to-bottom z-order, so the first rect containing a point is
    /// the window the user actually sees under the cursor.</summary>
    public static List<WindowRect> Snapshot(params IntPtr[] exclude)
    {
        var list = new List<WindowRect>();
        var name = new char[64];

        Win32.EnumWindows((hwnd, _) =>
        {
            if (exclude.Contains(hwnd)) return true;
            if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return true;

            // Cloaked = UWP apps that are "open" but not actually on screen.
            if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                && cloaked != 0)
                return true;

            var ex = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
            if ((ex & Win32.WS_EX_TOOLWINDOW) != 0) return true;

            var len = Win32.GetClassName(hwnd, name, name.Length);
            var cls = new string(name, 0, len);
            if (ExcludedClasses.Contains(cls)) return true;

            // Extended frame bounds trim the invisible resize borders that
            // GetWindowRect includes — otherwise every window capture carries a
            // few transparent pixels of the app behind it.
            if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out Win32.RECT rect, System.Runtime.InteropServices.Marshal.SizeOf<Win32.RECT>()) != 0)
            {
                if (!Win32.GetWindowRect(hwnd, out rect)) return true;
            }

            if (rect.Width < 8 || rect.Height < 8) return true;

            list.Add(new WindowRect(hwnd, rect));
            return true;
        }, IntPtr.Zero);

        return list;
    }

    public static WindowRect? Hit(List<WindowRect> windows, int x, int y)
    {
        foreach (var w in windows)
            if (w.Bounds.Contains(x, y))
                return w;
        return null;
    }
}
