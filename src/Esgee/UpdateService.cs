using Velopack;
using Velopack.Sources;

namespace Esgee;

/// <summary>
/// Self-update via GitHub Releases (Velopack). The resident instance checks
/// shortly after startup and then every 12 hours; a found update is downloaded
/// quietly and staged to apply when the process next exits, so machines stay
/// current without ever interrupting a capture. The tray's "Check for updates"
/// item drives the same manager interactively.
/// </summary>
internal sealed class UpdateService
{
    public const string RepoUrl = "https://github.com/beswift/esgee";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(12);

    private readonly UpdateManager _mgr = new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    private UpdateInfo? _staged;

    /// <summary>x.y.z from the assembly; CI stamps it from the git tag.
    /// Local (non-release) builds report 0.0.0.</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>False for dev builds run straight out of a publish folder —
    /// those have no Update.exe alongside and can't (and shouldn't) self-update.</summary>
    public bool IsInstalled => _mgr.IsInstalled;

    /// <summary>Fire-and-forget background loop for the resident instance.</summary>
    public void StartBackgroundChecks()
    {
        if (!IsInstalled)
        {
            Log.Info($"update checks off: v{CurrentVersion} is not a managed install (dev build)");
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(FirstCheckDelay);
            while (true)
            {
                try
                {
                    await CheckAndStageAsync();
                }
                catch (Exception ex)
                {
                    // Offline, rate-limited, GitHub down — all fine, try later.
                    Log.Warn($"update check failed (retrying in {RecheckInterval.TotalHours}h): {ex.Message}");
                }
                await Task.Delay(RecheckInterval);
            }
        });
    }

    /// <summary>One check: download + stage if something newer exists. Returns
    /// the new version string, or null when already current.</summary>
    public async Task<string?> CheckAndStageAsync()
    {
        if (_staged is not null) return _staged.TargetFullRelease.Version.ToString();

        var info = await _mgr.CheckForUpdatesAsync();
        if (info is null)
        {
            Log.Info($"update check: v{CurrentVersion} is current");
            return null;
        }

        var target = info.TargetFullRelease.Version.ToString();
        Log.Info($"update available: v{target} (running v{CurrentVersion}); downloading");
        await _mgr.DownloadUpdatesAsync(info);

        // Stage: Update.exe waits for this process to exit, then swaps
        // current/ in place. Next launch (autostart, typically) is the new
        // version — no restart is forced on the user.
        _mgr.WaitExitThenApplyUpdates(info, silent: true, restart: false);
        _staged = info;
        Log.Info($"update v{target} downloaded; applies on next restart");
        return target;
    }

    /// <summary>Apply a staged (or freshly found) update immediately and
    /// relaunch. Only called from the tray after the user says yes.</summary>
    public async Task UpdateNowAsync()
    {
        var info = _staged ?? await _mgr.CheckForUpdatesAsync();
        if (info is null) return;
        if (_staged is null) await _mgr.DownloadUpdatesAsync(info);
        Log.Info($"restarting into v{info.TargetFullRelease.Version}");
        _mgr.ApplyUpdatesAndRestart(info);
    }
}
