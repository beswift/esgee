using Velopack;

namespace Esgee;

/// <summary>
/// Explicit entry point (see StartupObject in the csproj) so Velopack's hooks
/// run before WPF spins up. On install/update/uninstall, Update.exe relaunches
/// the app with special arguments that VelopackApp handles (shortcuts, then
/// exit) — WPF must not have started by then.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
