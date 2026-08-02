using Microsoft.Win32;

namespace Esgee;

/// <summary>
/// esgee is only useful if it's already running when you hit Win+Shift+S, so
/// start-on-login is close to mandatory rather than a nicety.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "esgee";

    private static string ExePath =>
        Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()!.Location;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is not null;
            }
            catch (Exception ex)
            {
                Log.Warn($"autostart read failed: {ex.Message}");
                return false;
            }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);

            if (enabled) key.SetValue(ValueName, $"\"{ExePath}\"");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);

            Log.Info($"autostart {(enabled ? "enabled" : "disabled")}");
        }
        catch (Exception ex)
        {
            Log.Error($"autostart write failed: {ex.Message}");
        }
    }
}
