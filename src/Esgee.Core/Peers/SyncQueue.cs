using System.IO;
using System.Threading.Channels;
using Esgee.Store;

namespace Esgee.Peers;

/// <summary>
/// Background push of every new capture to SyncTargetPeer. Everything about
/// this class is designed to stay OUT of the capture path: Enqueue is a
/// non-blocking channel write, the worker owns all network I/O, and a dead or
/// offline target just means the queue drains later. The receiver dedupes by
/// sha256, so at-least-once delivery (retries, restarts) is harmless.
///
/// Delivery ledger is the sync_pushed table; a startup backlog sweep enqueues
/// anything not yet pushed, so captures taken while the target was offline —
/// or before sync was enabled — catch up automatically.
/// </summary>
public sealed class SyncQueue : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15),
    ];

    private readonly ShotStore _store;
    private readonly Settings _settings;
    private readonly Channel<long> _queue = Channel.CreateUnbounded<long>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private int _pending;
    private volatile bool _offline;

    /// <summary>The SyncTargetPeer setting verbatim — also the sync_pushed key.</summary>
    public string Target { get; }

    /// <summary>Approximate items not yet delivered — tray display only.</summary>
    public int Pending => _pending;

    public bool Offline => _offline;

    public event Action? StateChanged;

    public SyncQueue(ShotStore store, Settings settings)
    {
        _store = store;
        _settings = settings;
        Target = settings.SyncTargetPeer;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Called from the capture pipeline. Non-blocking, never throws.</summary>
    public void Enqueue(long shotId)
    {
        if (_queue.Writer.TryWrite(shotId))
        {
            Interlocked.Increment(ref _pending);
            StateChanged?.Invoke();
        }
    }

    /// <summary>Enqueue everything not yet pushed — run once at startup.</summary>
    public void EnqueueBacklog()
    {
        try
        {
            var backlog = _store.NotPushed(Target, TargetMachineName(), limit: 500);
            if (backlog.Count == 0) return;
            Log.Info($"sync: backlog sweep found {backlog.Count} unpushed capture(s)");
            foreach (var shot in backlog) Enqueue(shot.Id);
        }
        catch (Exception ex)
        {
            Log.Warn($"sync: backlog sweep failed: {ex.Message}");
        }
    }

    /// <summary>"machine" from "machine", "host:port", or a full URL (the
    /// URL's host) — used to skip pushing a capture back to the machine it
    /// came from.</summary>
    private string TargetMachineName()
    {
        var t = Target;
        if (Uri.TryCreate(t, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https")
            return uri.Host;
        var colon = t.LastIndexOf(':');
        return colon > 0 ? t[..colon] : t;
    }

    private async Task PumpAsync()
    {
        PeerClient? client = null;
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                var delivered = false;
                var attempt = 0;
                while (!delivered && !_cts.IsCancellationRequested)
                {
                    try
                    {
                        client ??= Connect();
                        if (client is null) throw new InvalidOperationException(
                            $"cannot resolve sync target '{Target}'");

                        delivered = await PushOneAsync(client, id);
                        if (_offline) { _offline = false; StateChanged?.Invoke(); }
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        client?.Dispose();
                        client = null; // re-resolve — the peer's IP/port may change

                        var wait = Backoff[Math.Min(attempt, Backoff.Length - 1)];
                        if (!_offline)
                        {
                            // One line when we go offline, not one per retry.
                            Log.Warn($"sync: push to {Target} failed ({ex.Message}); " +
                                     $"retrying with backoff");
                            _offline = true;
                            StateChanged?.Invoke();
                        }
                        attempt++;
                        await Task.Delay(wait, _cts.Token);
                    }
                }

                Interlocked.Decrement(ref _pending);
                StateChanged?.Invoke();
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>Resolve the target (tailnet machine name, host[:port], or a
    /// full URL — docs/PROTOCOL.md "Addressing") and build a client. Null when
    /// the name isn't on the tailnet right now or the entry is malformed.</summary>
    private PeerClient? Connect()
        => PeerClient.ResolveTargetUrl(Target, _settings.PeerPort) is { } baseUrl
            ? new PeerClient(new PeerInfo(TargetMachineName(), baseUrl), _settings.PeerToken)
            : null;

    /// <summary>True = delivered (or permanently skippable). Throws on
    /// transient failure so the caller's backoff loop retries.</summary>
    private async Task<bool> PushOneAsync(PeerClient client, long id)
    {
        var shot = _store.GetById(id);
        if (shot is null || !File.Exists(shot.Path))
        {
            Log.Warn($"sync: shot {id} vanished before push; skipping");
            _store.MarkPushed(id, Target);
            return true;
        }

        // Give the local OCR a moment to finish so the sidecar carries text and
        // the receiver never re-OCRs. If it's genuinely stuck, send without —
        // the receiver leaves ocr_done=0 and its own sweep fills the hole.
        string? ocrText = null;
        var engine = "";
        if (shot.Kind == "image")
        {
            for (var waited = 0; waited < 30_000; waited += 1000)
            {
                var (done, text, ver) = _store.GetOcr(id);
                if (done) { ocrText = text ?? ""; engine = ver; break; }
                await Task.Delay(1000, _cts.Token);
            }
        }

        var meta = new IngestMeta(
            shot.Sha256, shot.TakenAt.ToString("o"), shot.Width, shot.Height,
            shot.Kind, shot.DurationMs, ocrText, engine.Length > 0 ? engine : null,
            shot.Origin.Length > 0 ? shot.Origin : Environment.MachineName,
            shot.FileName);

        var result = await client.IngestAsync(meta, shot.Path,
            shot.GifPath,
            shot.IsVideo && File.Exists(shot.ThumbPath) ? shot.ThumbPath : null);

        _store.MarkPushed(id, Target);
        Log.Info($"sync: pushed shot {id} to {Target} " +
                 $"(remote id {result.Id}{(result.Duplicate ? ", deduplicated" : "")})");
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { await _pump; } catch { }
        _cts.Dispose();
    }
}
