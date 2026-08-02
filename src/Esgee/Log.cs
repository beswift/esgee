using System.IO;

namespace Esgee;

/// <summary>Deliberately tiny. A tray app with no window needs a paper trail.</summary>
internal static class Log
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "esgee", "esgee.log");

    private static readonly Lock Gate = new();

    static Log() => Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERR ", msg);

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path, $"{DateTimeOffset.Now:HH:mm:ss.fff} {level} {msg}{Environment.NewLine}");
        }
        catch { /* logging must never take the app down */ }
    }
}
