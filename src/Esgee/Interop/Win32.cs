using System.Runtime.InteropServices;

namespace Esgee.Interop;

/// <summary>
/// The small slice of Win32 esgee needs. Kept in one place so the eventual
/// Linux port has an obvious seam to cut along.
/// </summary>
internal static partial class Win32
{
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int WM_HOTKEY = 0x0312;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;
    public const uint MOD_NOREPEAT = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hwnd, int id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassName(IntPtr hwnd, [Out] char[] name, int maxCount);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);

    // Classic DllImport: LibraryImport can't marshal delegate callbacks.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lparam);

    /// <summary>Excludes a window from screen capture (BitBlt, DXGI, gdigrab)
    /// while it stays visible on the monitor — the documented use case is
    /// exactly ours: recording controls that shouldn't appear in the recording.
    /// Win10 2004+.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    /// <summary>
    /// Makes a window that never takes focus. Without this, clicking a shelf card
    /// yanks the caret out of whatever you were typing in — which is exactly the
    /// interruption esgee exists to avoid.
    /// </summary>
    public static void MakeNoActivate(IntPtr hwnd)
    {
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)ex);
    }

    /// <summary>NoActivate plus click-through — for the countdown pill, which
    /// must never eat a click meant for the app under it.</summary>
    public static void MakeGhost(IntPtr hwnd)
    {
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)ex);
    }

    /// <summary>Asks DWM for a dark title bar so the archive window doesn't get
    /// a glaring white caption strip in an otherwise dark app.</summary>
    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        var on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }
}
