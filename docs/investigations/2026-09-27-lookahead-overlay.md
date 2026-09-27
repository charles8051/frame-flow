# Does a deeper video ring help an overlay?

**Date:** 2026-09-27
**Issues:** #231, a slice of #227. Answers the first open question in the
[video-lookahead spec](../feature-specs/video-lookahead/spec.md).

## Question

An inference branch posts its result when the graph runs it, which is ahead of the picture by
however much the player buffers. A deeper ring gives the branch more lead. Does that lead help an
overlay line up with the picture, or does keying the overlay off the presented frame decide it?

The issue named three arms: ring depth 3 with the overlay keyed off `FramePresented`, a deeper ring
keyed the same way, and a deeper ring with the overlay drawn from what the branch posted. One run per
depth measures both overlay rules, so the three arms come from runs at depths 3, 12 and 24.

## Verdict

**Keying the overlay off the presented frame lines it up at the shipping depth. A deeper ring adds
nothing to that, and an overlay drawn from what the branch posted runs further ahead of the picture
the deeper the ring.** The lead serves no overlay; the lookahead surface would serve throughput
consumers only, and none is named.

| Ring depth | Clip | Keyed: frames showing their own result | Keyed: age of the result shown, p95 | Posted: result shown ahead of the picture, p50 | Results for frames presented |
| --- | --- | ---: | ---: | ---: | ---: |
| 3 | 1080p60 | 97.7% | 0 ms | 83 ms | 590 / 598 |
| 12 | 1080p60 | 97.3% | 0 ms | 233 ms | 585 / 597 |
| 24 | 1080p60 | 95.1% | 0 ms | 433 ms | 573 / 597 |
| 3 | 1080p30 | 93.1% | 33 ms | 167 ms | 84 / 87 |
| 12 | 1080p30 | 88.6% | 33 ms | 467 ms | 80 / 88 |
| 24 | 1080p30 | 79.5% | 33 ms | 867 ms | 72 / 88 |

Arm 1 is the depth-3 keyed column. Arm 2 is the depth-12 and depth-24 keyed columns. Arm 3 is the
depth-12 and depth-24 posted columns.

## Findings

### 1. At depth 3, the keyed overlay already shows each frame's own result

With the branch about five frames ahead of the picture, a result is ready before its frame is shown
99% of the time (the lead's p50 is 92 ms at 60 fps). `PresentedResults` then shows the frame's own
result on 93 to 98% of frames, and the rest show the result one or two frames earlier.

### 2. A deeper ring does not improve that, and fewer frames get a result

The keyed share of frames with their own result falls a little as the ring deepens, and fewer
frames get a result at all: 590 of 598 at depth 3, 573 of 597 at depth 24, at 60 fps. The branch is
latest-wins, so a frame arriving while a run is in progress is dropped. The measurement does not
show why a deeper ring drops more; frames reaching the branch in larger bursts would do it.

### 3. An overlay drawn from what was posted leads the picture by the ring

Drawing whatever the branch posted last puts a later frame's result over the picture on 97 to 99% of
frames, by the ring depth plus two frames: 83 ms at depth 3 and 433 ms at depth 24 at 60 fps. That is
the drift the issue predicted. A deeper ring makes it worse, which is why the migration in #242 to
#245 keyed LiveCaptioning's overlays off `FramePresented`.

## Method

`spikes/LookaheadOverlayProbe` plays a clip through a `FrameFlowPlayer` whose configurator runs
yolov8n on DirectML on a branch with `InferenceOperators.Infer` (#436). Its video sink shows nothing
and raises `FramePresented` as the pacer hands each frame over. For every presented frame it records
the timestamp of the result `PresentedResults` shows and of the result posted last, and for every
result the wall time it was posted and the wall time its frame was shown.

- RTX 3080 Ti, D3D11VA decode with frames downloaded to system memory, Release build.
- No audio sink, so the pacer runs on the wall clock. Nothing played on the speakers.
- The ring depth is `ClockSelectVideoSink.DefaultCapacity`. The depth-12 and depth-24 runs raised
  that constant in a local build, which the issue anticipated until #236 lets the session pass a
  depth. The committed tree keeps 3.

Two limits:

- **Presentation is the pacer's hand-off, not a display swap.** A real presenter raises
  `FramePresented` after composition, which is later, and by then the branch may have posted more.
  The keyed rule selects by the presented frame's timestamp, so more results can only let more
  frames show their own. The posted rule shows the newest, so more results can only put it further
  ahead. Measured at display time, the gap between the rules would be at least as wide as here.
- **One model and one machine.** yolov8n on DirectML finishes well inside a frame here. A model
  slower than a frame would leave more frames without their own result under both rules; a deeper
  ring would not change that either, since the branch drops frames while a run is in progress.

## Reproducing

```bash
dotnet run --project spikes/LookaheadOverlayProbe -c Release -- tests/corpus/files/test-1080p60-h264-aac.mp4
```

It needs yolov8n in `%LOCALAPPDATA%\FrameFlow.Yolo\models` and a DirectML-capable GPU. For a deeper
ring, raise `ClockSelectVideoSink.DefaultCapacity` locally and rebuild.
