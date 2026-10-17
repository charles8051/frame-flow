# ADR-0087: The interop presenter converts off the UI thread

## Status

**Accepted** (2026-10-17) and implemented with #781.

Motivating issue: #781 (`CompositionInteropVideoView` converts every view's frames on the UI thread,
so a window's frame time grows with its view count, and anything that waits for the next rendered
frame waits behind every view). Found alongside #780, where a switch's first present on a shared
decode device waited on the UI thread for the decoders' device lock.

**Related:**

- [ADR-0063](ADR-0063-nv12-pixel-shader-color-conversion.md): NV12 and P010 are converted by a pixel
  shader, not by `VideoProcessorBlt`. Its rejected alternative, "Off-thread `VideoProcessorBlt`",
  moved a hanging call off the UI thread without removing the contention. This record moves the
  whole conversion off it, for the UI thread's sake, and leaves ADR-0063's choice of pass alone.
- [ADR-0064](ADR-0064-zero-copy-converter-device-ownership.md): the converter owns its D3D11
  device and bridges each decode device through a shareable NV12 staging texture, copied on the
  decoder's `ID3D11Multithread`-protected immediate context. Unchanged. The copy now waits for that
  lock on a pool thread.
- [ADR-0085](ADR-0085-one-lifecycle-for-views.md) decision 2: the view keeps the frame it presented
  last and hands it back to its sink when it stops presenting. Decision 5: a present's commit
  compares the binding it started under.
- [ADR-0086](ADR-0086-work-in-flight-held-until-it-ends.md) decision 3: nothing is freed before the
  work that uses it has ended. A conversion in flight is such work.
- [Device loss reaches the device owner](gpu-device-loss-reaches-the-device-owner.md) (#759): a
  converter marks itself lost and the next present rebuilds it.
- #760: the video wall proposal, which would replace N views' presents with one.

## Context

Each frame's present did four things on the UI thread, in `Present`:

1. decided what to do with the converter: reuse, rebind, resize, rebuild, fit the ring to the view;
2. picked a free ring slot (`PresentPlanner`);
3. filled it: `D3D11Nv12SharedConverter.ConvertInto` copies the decode slice on the decode device
   under its keyed mutex, then runs the shader pass on the converter's own device under the ring
   slot's keyed mutex. The D3D12 converter draws and copies; the CPU uploader maps and copies;
4. imported the slot into the compositor once, and handed it to the surface with
   `UpdateWithKeyedMutexAsync`.

Step 3 is GPU and driver work. Its CPU cost is small when the GPU is idle, about 0.5 ms at 1080p on
the development machine, but it blocks in two places the view does not control:

- the decode device's multithread lock. FFmpeg's D3D11VA decoders hold it from `DecoderBeginFrame`
  to `DecoderEndFrame`. With players sharing one `HardwareDevice`, every decoder on it holds the same
  lock, and the copy waits behind all of them;
- the keyed mutexes' `AcquireSync`, up to 1000 ms each.

Measured with four 1080p30 players on one shared device, one view each, in one window
(`frameflow.presenter.convert_duration_ms`): p50 0.4 to 0.5 ms, p95 1.1 to 1.5 ms, max 17 to 24 ms
when the decoders are busy, as they are during a switch. On the UI thread that time added up across
views and delayed every other dispatcher job. A job posted at normal priority from another thread
waited p99 6.8 ms and up to 18 ms in steady playback, and up to 384 ms during a replace.

Steps 1, 2 and 4 must stay on the UI thread. The decisions read the view's bounds, placement and
render scaling, and the compositor's import and hand-off are thread-affine.

## Decision

### 1. One conversion per view runs on the thread pool

The UI thread makes the decisions and picks the slot as before. It then hands the fill, with the
frame, the slot and what the hand-off needs, to a `ConvertJob` that runs on the thread pool. When it
has run, its completion is posted to the UI thread at `Render` priority, and the UI thread imports
and hands off the slot as before. A conversion stamps the stall watchdog's Blt timestamp when it
starts, on its own thread.

Each conversion is a `Task.Run`, as ADR-0009 prefers to dedicated threads. Views convert in
parallel. A view whose conversion is stuck in a driver holds one pool thread and no UI time.

### 2. At most one conversion per view is in flight, and the UI thread leaves the producers alone meanwhile

While a view's conversion runs, its UI thread starts no other present and no redraw. A present
posted meanwhile returns at once and leaves the frame in the sink, which keeps the newest
(latest-wins, as when the UI thread was busy before). The completion sweeps the sink again, and runs
a redraw that was asked for meanwhile.

So every call on a converter, its device's immediate context, its ring and its keyed mutexes is made
by one thread at a time. A rebind, resize, rebuild or drop happens only between conversions. No lock
and no `ID3D11Multithread` protection on the converter's own device is needed. The UI thread still
reads a converter's sizes for the surface layout during a conversion, which the fill does not
change.

### 3. A teardown takes the conversion in flight

`StopPresenting` takes the job off the view:

- it keeps the job's frame as the frame presented last, so the sink takes back the newest frame the
  view took, and a paused picture comes back with it (ADR-0085 decision 2);
- the producers the teardown takes wait for the job's run as they wait for the compositor's in-flight
  presents (ADR-0086 decision 3), within the same bounded drain, after which the reaper holds them;
- the job's completion finds that it is no longer the view's and only releases its own reference to
  the frame. A new attachment presents meanwhile with new producers.

### 4. The conversion's time is a metric

`frameflow.presenter.convert_duration_ms` (meter `FrameFlow.Presenter`, tag `source`: `d3d11`,
`d3d12` or `cpu`) records every conversion. `CompositionInteropVideoView.LastConvertDuration` is the
view's last one, for a caller that wants a view's own cost.

## Consequences

### Positive

- The UI thread's cost per frame no longer includes the GPU copy, the shader pass or the waits on
  the decode device's lock and the keyed mutexes. With four views, a normal-priority job's wait fell
  from p99 6.8 ms and max 18 ms to p99 0.7 ms and max 1.0 ms in steady playback, and from max 384 ms
  to max 7.4 ms during a replace.
- Views convert in parallel, so a busy decode device delays only the views whose frames it
  decodes.
- A conversion that hangs in the driver hangs a pool thread. The stall watchdog still sees it, by
  the stale Blt stamp with the sink still accepting frames.

### Negative

- A present takes two dispatcher turns rather than one: the decision, and the hand-off. When the UI
  thread is idle the added latency is the conversion's own time; when it is busy, each turn waits
  for it. Frames still take the newest-wins path, so a slow UI thread costs frames, not latency
  that builds up.
- Converter builds and rebuilds, which compile a shader and create a device, still run on the UI
  thread. They happen on the first frame and on size, format, device or output changes.
- The completion runs on the UI thread at `Render` priority, as the present did, so a window with
  many views still hands off N slots per frame there. #760 would replace those with one.

### Neutral

- `ConvertForTest` is an internal seam: the headless backend has no GPU interop, so a test starts the
  conversion a present would start and checks its completion and its teardown.

## Alternatives considered

### Batch every view's conversion into one pass per frame

One UI-thread pass would convert every view's pending frame together. It saves dispatcher turns,
not UI-thread time: the copies and the waits on the decode device's lock are the same, and one
view's wait would hold every other view's present. It also needs a coordinator across views that
does not exist.

### A dedicated thread per view

A thread per view that owns its converter outright would move the decisions there too, and remove
the second dispatcher turn. The decisions read the view's layout, so the UI thread would have to
publish it as values, and the teardown, the redraw and the device-loss rebuild would all cross a
thread boundary. ADR-0009 also prefers pool tasks to dedicated threads. The pool job keeps every
decision where it was.

### Convert on the presenting (pacer) thread

The sink's `PresentAsync` runs on the player's pacer thread, which could fill the ring directly. It
would tie the pacer's timing to GPU waits, and the slot choice and the converter decisions, which
need the view's layout, would have to move with it.
