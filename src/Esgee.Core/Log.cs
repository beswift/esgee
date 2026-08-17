using System.IO;

namespace Esgee;

/// <summary>Deliberately tiny. A tray app with no window needs a paper trail.</summary>
public static class Log
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "esgee", "esgee.log");

    private static readonly Lock Gate = new();

    static Log() => Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

    // Control characters plus the Unicode line/paragraph separators - the
    // full set a log viewer might treat as "this line ended".
    private static bool IsLineBreaker(char c)
        => char.IsControl(c) || c == (char)0x2028 || c == (char)0x2029;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERR ", msg);

    private static void Write(string level, string msg)
    {
        try
        {
            // Client-supplied text (display names, search queries) rides
            // inside messages, and this file is what operators and agents
            // reconstruct history from - an embedded newline would forge
            // whole audit lines, so one event is always exactly one line
            // (and control characters can't play terminal tricks either).
            if (msg.Any(IsLineBreaker))
                msg = string.Concat(msg.Select(c => IsLineBreaker(c) ? ' ' : c));
            lock (Gate)
                File.AppendAllText(Path, $"{DateTimeOffset.Now:HH:mm:ss.fff} {level} {msg}{Environment.NewLine}");
        }
        catch { /* logging must never take the app down */ }
    }
}
