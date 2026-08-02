using System.Windows.Interop;
using Esgee.Interop;

namespace Esgee.Capture;

/// <summary>
/// One global hotkey, registered with a fallback chain. Owning our own hotkey is
/// the point: Win+Shift+S dies whenever the Snipping Tool background process
/// wedges (which it did, on this machine, within a week).
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int BaseId = 0xE5E0;

    private readonly HwndSource _sink;
    private readonly Dictionary<int, string> _actions = [];
    private readonly List<(string Action, string Chord)> _bound = [];

    /// <summary>Successfully registered (action, chord) pairs, in order.</summary>
    public IReadOnlyList<(string Action, string Chord)> Bound => _bound;

    /// <summary>Fires with the action name of the chord that was pressed.</summary>
    public event Action<string>? Pressed;

    /// <summary>
    /// Registers every parseable chord; duplicates keep the first binding.
    /// Registration success is NOT proof a chord fires: PrintScreen registers
    /// fine while the shell still swallows it until the next sign-out
    /// (PrintScreenKeyForSnippingEnabled is read at logon) — which is why the
    /// same action can be bound to more than one chord.
    /// </summary>
    public HotkeyManager(IEnumerable<(string Chord, string Action)> bindings)
    {
        _sink = new HwndSource(new HwndSourceParameters("esgee.hotkey")
        {
            ParentWindow = (IntPtr)(-3), // HWND_MESSAGE
            Width = 0,
            Height = 0,
        });
        _sink.AddHook(WndProc);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (chord, action) in bindings)
        {
            if (string.IsNullOrWhiteSpace(chord) || !seen.Add(chord)) continue;
            if (!TryParse(chord, out var mods, out var vk))
            {
                Log.Warn($"hotkey '{chord}' does not parse; skipping");
                continue;
            }

            var id = BaseId + _actions.Count;
            if (Win32.RegisterHotKey(_sink.Handle, id, mods | Win32.MOD_NOREPEAT, vk))
            {
                _actions[id] = action;
                _bound.Add((action, chord));
                Log.Info($"hotkey registered: {chord} -> {action}");
            }
            else
            {
                Log.Warn($"hotkey {chord} unavailable (likely claimed by another app)");
            }
        }

        if (_bound.Count == 0)
            Log.Error("no capture hotkey could be registered; capture only reachable from tray");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && _actions.TryGetValue((int)w, out var action))
        {
            handled = true;
            Log.Info($"hotkey pressed -> {action}");
            Pressed?.Invoke(action);
        }
        return IntPtr.Zero;
    }

    /// <summary>Parses "Ctrl+Shift+S" / "PrintScreen" / "Win+F9" style chords.</summary>
    private static bool TryParse(string chord, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(chord)) return false;

        var parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Win32.MOD_CONTROL; break;
                case "shift": mods |= Win32.MOD_SHIFT; break;
                case "alt": mods |= Win32.MOD_ALT; break;
                case "win": mods |= Win32.MOD_WIN; break;
                default: return false;
            }
        }

        var key = parts[^1].ToLowerInvariant();
        vk = key switch
        {
            "printscreen" or "prtscn" => 0x2C,
            "pause" => 0x13,
            "insert" => 0x2D,
            _ when key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) => char.ToUpperInvariant(key[0]),
            _ when key.StartsWith('f') && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 24
                => (uint)(0x70 + f - 1),
            _ => 0
        };
        return vk != 0;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) Win32.UnregisterHotKey(_sink.Handle, id);
        _sink.Dispose();
    }
}
