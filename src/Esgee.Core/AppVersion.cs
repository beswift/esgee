using System.Reflection;

namespace Esgee;

/// <summary>x.y.z of whichever executable is hosting this library — the WPF
/// app and the headless node are both stamped from the git tag by CI, so
/// /ping and --doctor report the number the release actually wears. Local
/// builds report 0.0.0 (see the csproj comments) so a dev copy is never
/// mistaken for a release.</summary>
public static class AppVersion
{
    public static string Current { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly).GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";
}
