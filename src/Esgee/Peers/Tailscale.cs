using System.Diagnostics;
using System.Text.Json;

namespace Esgee.Peers;

/// <summary>One tailnet machine, as reported by `tailscale status --json`.</summary>
public sealed record TailnetNode(string HostName, string Ip, bool Online, bool Self);

/// <summary>
/// Thin wrapper over the tailscale CLI. The CLI is the source of truth for
/// "what is my tailnet address" and "who else is on the tailnet" — no config
/// duplication, and it works with whatever login the user already has.
/// All calls shell out, so callers must stay off the UI thread.
/// </summary>
public static class Tailscale
{
    /// <summary>This machine's IPv4 tailnet address (100.x.y.z), or null when
    /// tailscale is not installed / not running / logged out.</summary>
    public static string? SelfIPv4()
    {
        var output = Run("ip -4");
        var line = output?.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
        return line is not null && System.Net.IPAddress.TryParse(line, out _) ? line : null;
    }

    /// <summary>All tailnet nodes (self included) with an IPv4 address.</summary>
    public static List<TailnetNode> Nodes()
    {
        var nodes = new List<TailnetNode>();
        var json = Run("status --json");
        if (json is null) return nodes;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("Self", out var self) && Parse(self, self: true) is { } me)
                nodes.Add(me);

            if (root.TryGetProperty("Peer", out var peers) &&
                peers.ValueKind == JsonValueKind.Object)
            {
                foreach (var peer in peers.EnumerateObject())
                    if (Parse(peer.Value, self: false) is { } node)
                        nodes.Add(node);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"tailscale status parse failed: {ex.Message}");
        }

        return nodes;
    }

    private static TailnetNode? Parse(JsonElement el, bool self)
    {
        var host = el.TryGetProperty("HostName", out var h) ? h.GetString() : null;
        var online = el.TryGetProperty("Online", out var o) && o.GetBoolean();

        string? ip = null;
        if (el.TryGetProperty("TailscaleIPs", out var ips) &&
            ips.ValueKind == JsonValueKind.Array)
        {
            ip = ips.EnumerateArray().Select(e => e.GetString())
                .FirstOrDefault(s => s is not null && !s.Contains(':'));
        }

        return host is null || ip is null ? null : new TailnetNode(host, ip, online, self);
    }

    private static string? Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("tailscale", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"tailscale CLI unavailable: {ex.Message}");
            return null;
        }
    }
}
