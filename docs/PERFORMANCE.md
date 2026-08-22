# Performance map and refactor guardrails

This note records the August 2026 responsiveness audit. It is intentionally
specific: esgee's clipboard and card behavior is the product, so performance
work must preserve its transfer formats, echo defenses, and capture ordering.

## Pipeline

```text
native capture ─┐
                ├─> encode/store (worker) ─> shelf ─> OCR/sync queues
clipboard image ┘                              │
                                               └─> ordered clipboard service
                                                    ├─ prepare transfer (worker)
                                                    └─ OLE commit (UI STA)

archive local ────────────────────────────────> prepare ─> copy / drag
archive peer/share ─> materialize cache file ─> prepare ─> copy / drag
```

OS-facing drag and file-drop payloads always point at a real local file. A
browsed peer/share item is not ingested into the local archive unless the user
explicitly pulls it.

## Compatibility invariants

Still-image transfers offer all of:

- `esgee.internal`, which prevents cross-process copies from re-entering the
  resident clipboard watcher;
- `CF_HDROP` naming the archived/cache file;
- the original PNG bytes in the `PNG` format;
- a bitmap/CF_DIB fallback for older paste targets.

Recording transfers offer the marker and `CF_HDROP` only. The sibling GIF is
preferred when present; otherwise the MP4 is used. A recording must never offer
PNG or Bitmap, because image-first targets would paste a still frame instead.

Clipboard writes use `copy: true` so they survive process exit. The watcher's
750 ms self-echo guard is a time window on purpose and is set immediately before
the actual OLE write. It must not become a consume-once flag. Store-level SHA
deduplication remains the second defense against delayed echoes.

Clipboard requests are ordered by intent. If preparing an older image finishes
after a newer capture/click/text-copy request, the older request is discarded;
it must not overwrite the user's newer clipboard choice. Preparation is also
serialized and superseded work is cancelled between read/decode phases, so a
burst cannot fan out several full-resolution WIC decodes under memory pressure.
Each intent also records Windows' clipboard sequence number; if another app
copies newer image or text content during preparation, esgee discards its old
pending write instead of overwriting that external choice.

## Findings and implemented phase

### Shelf capacity spin (fixed)

The original `while (Children.Count >= MaxCards) first.Leave()` could never
finish. `Leave()` removes the card only from a 200 ms animation completion on
the same dispatcher; the loop prevented that callback from running and then
repeatedly targeted the already-leaving card.

Capacity now counts non-leaving cards, retires the required oldest active cards
once, and admits the newest immediately. Leaving visuals may temporarily make
the raw child count exceed capacity, while active cards remain bounded.
`MaxCards` is defensively clamped to at least one in both settings and the UI.

Regression verb: `esgee --check-shelf` pushes beyond capacity synchronously,
then exercises `ClearAll` followed by a new capture.

### Transfer preparation on the dispatcher (fixed)

The original `BuildDataObject` read the entire file and fully decoded a WPF
bitmap in every UI click/drag/capture continuation. Transfer preparation now
runs on a worker, reads the PNG once, decodes from those bytes, freezes the
bitmap, and hands a prepared transfer back to the STA. Building the DataObject
from that transfer performs no file I/O or image decode.

The actual `Clipboard.SetDataObject(copy: true)` remains on the existing UI STA
in this conservative phase. Logs separate `prepare` and `commit` durations:

```text
clipboard: copied shot 123 from capture (prepare 16 ms, commit 55 ms)
```

This distinction determines the next step: file/decode work and OLE flush have
different remedies.

### Cold archive drag (fixed)

Peer/share clients allow five minutes for large relay transfers. The archive
previously called `GetAwaiter().GetResult()` on that task from a mouse-move
handler, freezing the window for the whole timeout. Materialization and transfer
preparation are now awaited without blocking. `DoDragDrop` begins only if the
original press is still active. Per-gesture generations prevent a slow old
download from hijacking a later mouse press and dragging the wrong item.

### Archive thumbnail burst (bounded)

Opening an archive with peers/shares configured used to refresh once directly
and again when the machine switcher selected `This PC`, launching roughly 400
thumbnail jobs. Startup now creates one generation. Thumbnail decode/fetch is
limited to four concurrent jobs, stale generations exit before decoding, and
image assignment is posted at background dispatcher priority.

PR CI now builds both the WPF app and the headless Node, matching the local
verification requirement for shared Core changes.

## Deferred work and evidence thresholds

These are intentionally not part of the first patch. They change more runtime
semantics and should be driven by the new timing evidence.

1. **Dedicated STA clipboard writer.** Implement one FIFO STA thread with a WPF
   dispatcher only if `commit` itself repeatedly exceeds 250 ms or correlates
   with UI stalls. It must build/commit the DataObject on that STA, preserve
   request ordering, call the echo guard adjacent to the write, retry temporary
   clipboard contention, and shut down without delaying app exit.
2. **Clipboard watcher isolation.** `WM_CLIPBOARDUPDATE` still performs provider
   calls and up to 200 ms of lock retries on the main STA. Move hash/PNG encoding
   to a serialized worker first. If delayed-rendering providers are shown to
   block inside OLE calls, give reads their own message-pumping STA as well.
3. **Archive virtualization/cache.** The ordinary WrapPanel creates all 200
   tiles. If bounded jobs are still too costly, add generation cancellation,
   incremental batches, and a bounded frozen-thumbnail cache before attempting
   a custom virtualizing wrap panel.
4. **Composition pressure.** Under measured GPU starvation, change the shelf
   countdown from Width animation to a left-origin ScaleX transform and cap
   preview decode near display resolution. Retain shadows and exit animation
   until profiling shows they are material.

Do not globally force WPF software rendering based on the August incident alone.
The observed RTX 5090 saturation was a trigger/amplifier, while the shelf spin
and synchronous waits were deterministic application defects.

## Verification matrix

For every transfer refactor:

- build `src/Esgee/Esgee.csproj` and `src/Esgee.Node/Esgee.Node.csproj`;
- run `--check-drag` for a still, a video with GIF, and a video without GIF;
- confirm marker, existing file-drop path, byte-identical PNG, Bitmap presence
  for stills, and explicit PNG/Bitmap absence for recordings;
- run `--check-shelf`;
- drive a real native hotkey and assert `hotkey pressed`, the clipboard timing
  line, and the exact `captured ... -> path` line in `esgee.log`;
- verify a shelf copy does not create a new archive row;
- exercise local/cached/cold/offline peer and share copy/drag/pull behavior;
- repeat under GPU/VRAM saturation and compare prepare versus commit time.

Also cover click, copy button, drag/cancel, right-dismiss, pin/expiry, max-card
eviction, OCR text copy, archive refresh during mouse-down, recording GIF/MP4
selection, and installed-versus-dev binary provenance.
