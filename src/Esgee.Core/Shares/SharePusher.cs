using System.IO;
using Esgee.Store;

namespace Esgee.Shares;

/// <summary>
/// The explicit per-capture push behind every share affordance — the card's
/// share icon and the archive tile's "Push to share". Deliberately NOT a
/// queue: a share receives a capture only through a human act
/// (docs/SHARES.md "The invariant"), so each push is one background task that
/// either lands or reports failure to the affordance that started it. No
/// retry ledger, no backlog sweep, nothing that could ever push on its own.
/// </summary>
public sealed class SharePusher
{
    private readonly ShotStore _store;
    private readonly Settings _settings;

    public SharePusher(ShotStore store, Settings settings)
    {
        _store = store;
        _settings = settings;
    }

    public bool Any => _settings.Shares.Length > 0;

    /// <summary>Shares to offer, last-used first — so a bare click on the
    /// icon and the top menu item mean the same share.</summary>
    public IReadOnlyList<ShareEntry> Ordered()
        => _settings.Shares
            .OrderByDescending(s => s.Name.Equals(_settings.DefaultShare,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Pushes one capture to one share. Images MUST arrive with their OCR
    /// sidecar: a share node runs no OCR engine and no client ever backfills
    /// (docs/SHARES.md "The node is a Linux binary"), so an image pushed with
    /// ocr_text null would stay invisible to /share/search forever. The rule
    /// here enforces that: wait briefly for the local queue (it is normally
    /// seconds deep), then REFUSE — throw, so the affordance that started the
    /// push reports it — rather than mint a permanently unsearchable item.
    /// The one deliberate exception is OCR disabled in settings: no text will
    /// ever exist, so the capture ships textless immediately and SHARES.md
    /// documents that trade-off. Never touches the capture pipeline; callers
    /// run it on a worker and own reporting. Duplicate=true on the result is
    /// a success — the sha256 was already shared and the existing item came
    /// back.
    /// </summary>
    public async Task<ShareItemDto> PushAsync(Shot shot, ShareEntry share,
        CancellationToken ct = default)
    {
        string? ocrText = null;
        var engine = "";
        if (shot.Kind == "image")
        {
            var (done, text, ver) = _store.GetOcr(shot.Id);
            if (!done && _settings.OcrEnabled)
            {
                for (var waited = 0; !done && waited < 30_000; waited += 1000)
                {
                    await Task.Delay(1000, ct);
                    (done, text, ver) = _store.GetOcr(shot.Id);
                }
                if (!done)
                    throw new InvalidOperationException(
                        "OCR hasn't finished for this capture — a share can never fill that " +
                        "hole itself, so the item would stay unsearchable; try again shortly");
            }
            if (done) { ocrText = text ?? ""; engine = ver; }
        }

        // Note what the meta CANNOT carry: origin, machine name, local id —
        // sharing publishes the capture, not the shape of the archive.
        var meta = new SharePostMeta(
            shot.Sha256, shot.TakenAt.ToString("o"), shot.Width, shot.Height,
            shot.Kind, shot.DurationMs, ocrText, engine.Length > 0 ? engine : null,
            shot.FileName);

        using var client = new ShareClient(share.BaseUrl, share.MemberToken, share.Name);
        var item = await client.PostItemAsync(meta, shot.Path,
            shot.GifPath,
            shot.IsVideo && File.Exists(shot.ThumbPath) ? shot.ThumbPath : null);

        MarkUsed(share);
        Log.Info($"share {share.Name}: pushed shot {shot.Id} as {item.Item}" +
                 (item.Duplicate == true ? " (already shared)" : ""));
        return item;
    }

    /// <summary>Successful pushes move the share to the front of every future
    /// menu — "last-used first" is remembered, not per-session.</summary>
    private void MarkUsed(ShareEntry share)
    {
        if (string.Equals(_settings.DefaultShare, share.Name, StringComparison.Ordinal)) return;
        _settings.DefaultShare = share.Name; // this process's menus reorder now

        // TryUpdate, never Save(): a SharePusher also lives in standalone
        // `esgee --archive` processes, whose Settings snapshot can be hours
        // older than the file — a whole-object Save from one of those would
        // roll back everything the resident app persisted since, member
        // tokens included.
        Settings.TryUpdate(s => s.DefaultShare = share.Name);
    }
}
