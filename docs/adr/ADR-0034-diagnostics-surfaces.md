# ADR-0034: Diagnostics surfaces as the safe cross-thread read pattern

## Status

Accepted

## Context

Two recent threads pointed at the same hole:

1. **The OpenAL audio race.** A downstream consumer was reading the
   audio sink's "current presentation time" and "samples played" from a
   different thread than the OpenAL callback that updated them. With no
   synchronization, the consumer could observe a torn pair —
   `(_currentPts: new, _samplesPlayed: old)` — and the master-clock
   calculation derived from those two fields was occasionally garbage.
   We fixed it reactively by adding a lock around the mutation path.
   The lock is correct; the *shape* of the API is what trapped us.
2. **Player diagnostics are guesses.** The Avalonia player example
   needs frame counters (decoded / presented / dropped) and the
   hwaccel binding name to render real metrics. Today
   `IVideoDecoder.HardwareBackend` exists but isn't reachable from the
   controller surface, and there are no counters anywhere. The example
   estimates FPS from position-vs-wallclock — useful but soft, and
   "are we dropping frames?" is unanswerable.

These two are the same problem. Any time consumer code reaches across
a thread boundary to ask another subsystem "what's your current state?",
the consumer either races (the OpenAL case) or settles for a guess
(the player case). FrameFlow has no canonical pattern for that
question.

There is *one* existing example that already does it right:
`FrameFlowBootstrapResult.Capabilities` (ADR-0033) is an immutable
record produced once and read freely. We want the same shape, applied
to *runtime* state, at every subsystem boundary worth observing.

## Decision

### Diagnostics surfaces are the safe cross-thread read pattern

Every subsystem with interesting mutable state exposes a
`Get{Subsystem}Diagnostics()` method that returns an immutable
snapshot record. **The snapshot method is the only sanctioned reader
of that state from outside the subsystem.** Consumer code that wants
to know "where is the audio sink now?" or "how many frames has the
decoder produced?" goes through the snapshot. Raw field access stays
private.

This serves two distinct populations:

- **Observers** (player UIs, integration tests, log scrapers) get
  structured, coherent state to render or assert on.
- **Pipeline participants** (master-clock readers, sync coordinators,
  loop restart logic) get a thread-safe read path for state that lives
  on a different worker.

Both populations have the same shape of need. One method serves them.

### Five rules for snapshots

1. **Snapshots are immutable records.** `sealed record` with
   positional constructor. Caller cannot mutate — eliminates the
   temptation to reach into the subsystem.
2. **Reads are internally synchronized.** The snapshot method handles
   whatever atomicity is needed; consumers see one call that returns a
   coherent value. See the [synchronization
   guidance](#synchronization-guidance) below.
3. **Snapshots are cheap.** Hot-path increments are lockless
   (`Interlocked.Increment` / `Volatile.Write` on a single field).
   Snapshot construction is a handful of reads plus one record
   allocation. Targeted cost: under 1µs uncontended on any modern
   machine. Safe to poll at 60 Hz; not safe to call from inside a
   per-frame loop.
4. **Surface what's already captured.** Diagnostics report state that
   the subsystem already maintains for its own correctness. Don't
   invent new counters to fill a record field — if the subsystem
   doesn't already track it, decide whether it should before adding
   the field.
5. **Snapshots evolve additively.** New fields land at the end of the
   positional constructor. Removals require a major-version bump per
   semver. Records expose `init`-only positional members so consumers
   can pattern-match without breaking when new fields appear.

### Synchronization guidance

There are four sane choices. Pick the cheapest one that gives you the
atomicity your snapshot's invariants require.

| Tool | When to reach for it | Approx. uncontended cost |
|---|---|---|
| `Interlocked.Read` / `Interlocked.Increment` | Single 32/64-bit field. No multi-field invariant. | ~5 ns |
| `Volatile.Read` / `Volatile.Write` | Single field, ordering matters but contention is rare. | ~2 ns |
| `lock` (`Monitor`) | Coherent multi-field snapshot. Critical section is **synchronous**. | ~25 ns |
| `SemaphoreSlim.WaitAsync` | Critical section needs to `await` (I/O, async call). | ~100–200 ns + an allocation |

**`SemaphoreSlim` is the wrong default.** It costs ~5–10× a `lock` and
only earns its keep when you need to hold the gate across an `await`.
Snapshot reads are synchronous by construction — they read fields and
allocate a record — so `lock` is unambiguously the right tool.

Where a single counter answers the question (frames decoded, packets
read), use `Interlocked` and skip the lock entirely.

For very high-frequency writers feeding read-mostly observers, a
**seqlock** (writer bumps a version counter pre and post update;
reader retries until it observes an even, matching pair) avoids
contention entirely. Not needed for current subsystems but documented
here as the escape hatch if we ever hit one.

### Surfaces to land

These are the subsystem boundaries that get a `Get{X}Diagnostics()`
method. The fields listed are the starting set — additions land
through the additive-evolution rule above.

| Surface | Snapshot fields (initial) | Concurrency model |
|---|---|---|
| **`IPlaybackController`** | Rollup of all per-subsystem snapshots + controller-derived A/V drift, current state, position, repeat mode. | `lock` over current session reference; sub-snapshots resolved while the lock is held. |
| **`IAudioSink`** | Presentation time, samples played, sample rate, channel count, queue depth, underflow count, state (idle/active/paused). | `lock` — coherent PTS + samples pair. **This is the OpenAL race fix made into an API.** |
| **`IVideoSink`** | Frames presented, last present PTS, last present wallclock, surface dimensions, dropped frames (sink-side). | `lock` — coherent PTS + wallclock pair. |
| **`IDemuxSession`** | Packets read per stream, bytes read, current container position, EOF flags per stream. | `Interlocked` per counter; `lock` for the EOF-per-stream array. |
| **`IVideoDecoder`** | Frames decoded, decode errors, last decode latency, hwaccel backend kind (`HardwareDecodeBackendKind?`). | `Interlocked` per counter; backend kind is set once at open and read-only after. |
| **`IAudioDecoder`** | Buffers decoded, decode errors, last decode latency. | Same. |
| **`PipelineController`** | Video / audio packet queue depths, pending frames count, backpressure events, worker fault state. | `Interlocked` per counter; lockless queue-depth snapshots from the channel. |

Notable additions implied by this list:

- The hwaccel-backend field on `VideoDecoder` (added in ADR-0033 but
  not surfaced beyond the partial class) finally has a public home:
  `VideoDecoderDiagnosticsSnapshot.HardwareBackend`. The Avalonia
  player example reads it through the controller-level rollup and
  renders it in the status strip.
- A/V drift becomes a first-class field on the controller-level
  snapshot. It's computed by the snapshot method from the
  `AudioSinkDiagnosticsSnapshot.PresentationTime` and
  `VideoSinkDiagnosticsSnapshot.LastPresentPts` — atomically, because
  both sub-snapshots are read under the same controller lock.

### What does NOT get a diagnostics surface

- Pure value types and records (`MediaInfo`, `DemuxPacket`,
  `PcmAudioBuffer`). They're immutable already; observation is reading
  the value.
- One-shot factories (`DemuxSessionFactory`,
  `PlaybackControllerFactory`). No persistent state worth observing.
- Anything fully covered by structured logging where consumers don't
  need to query state — e.g., bootstrap initialization sequence is
  logged at `Information`; we don't need a `BootstrapperDiagnostics`
  on top.

### Rollup contract

`IPlaybackController.GetDiagnostics()` is the player-UI entry point.
It composes the per-subsystem snapshots into a single
`PlaybackDiagnosticsSnapshot`. The rollup must produce a coherent
read across subsystems — i.e., the audio sink's PTS and the video
sink's last-presented PTS in the same snapshot must be sampled close
enough in time that drift calculation is meaningful. The
implementation acquires a controller-level lock, reads the
controller-owned state (position, state, repeat mode), then calls
into each owned subsystem's `GetDiagnostics()` under the same lock.

Per-subsystem snapshots remain individually accessible for focused
diagnostics and integration tests that only care about one subsystem.

## Consequences

### Positive

- **Eliminates the OpenAL-class race by construction.** Consumers who
  would otherwise reach in and race instead call the snapshot method
  and get atomicity from the subsystem's own synchronization.
- **One pattern across the project.** "I want to observe X from over
  here" has the same answer everywhere — call `Get{X}Diagnostics()`.
- **Test invariants get structured assertions.** Today the
  ContentCaptureHarness integration tests check captured frame buffers
  and audio sample arrays. With this in place they'll also assert
  `VideoFramesDecoded == expected_count`, `VideoFramesDropped == 0`
  (or `< threshold`), `HardwareBackend == expected_backend` on
  hwaccel-aware CI runners.
- **Hwaccel surfaces cleanly.** The selected backend kind, currently
  stranded on `VideoDecoder`, lands in `VideoDecoderDiagnosticsSnapshot`
  and rolls up into the controller's snapshot for the player UI.
- **Player diagnostics become precise.** The Avalonia example
  replaces "estimated FPS from position-vs-wallclock" with
  `(VideoFramesPresented delta / interval)` and adds a dropped-frames
  line — by far the most useful "is playback healthy?" indicator.

### Negative

- **API surface grows.** Each subsystem adds one method + one record.
  Net cost is bounded (~7 records total in the list above) but it's
  real surface area to maintain.
- **Discipline required on snapshot evolution.** Records grow only
  additively; removals are breaking. Easy to mess up if contributors
  don't know the rule. Mitigated by documentation here and a brief
  comment on each record type.
- **Temptation to over-populate.** Once a snapshot exists, every
  curious number wants to live there. Rule 4 ("surface what's already
  captured") is the discipline; reviewers enforce.
- **Snapshot allocation per call.** Records allocate. At 2 Hz polling
  this is negligible; if anyone polls at hundreds of Hz, they should
  reach for the lower-level `Interlocked.Read` on a single counter
  instead.

### Neutral

- Snapshots compose, but only at the controller level. No cross-cutting
  "global FrameFlow diagnostics" — by design. If a future tool wants
  one, it composes from the controller's rollup.

## Alternatives considered

### Push-based events for every state change

`IObservable<DiagnosticsChangeEvent>` style. Rejected because (a) it
floods subscribers when the underlying state changes at frame rate,
(b) it doesn't actually solve the OpenAL race — the change event
itself still needs a coherent payload, which is what the snapshot
provides anyway, and (c) it inverts ownership: consumers couple to a
subscription lifecycle instead of pulling on demand.

A consumer who wants a *stream* of snapshots can write
`Observable.Interval(TimeSpan.FromMilliseconds(500)).Select(_ => controller.GetDiagnostics())`
themselves. We won't bake it into the API.

### Per-field properties with `Volatile.Read`

`int FrameCount { get; }` etc. Rejected because (a) it doesn't give
coherent multi-field reads (the OpenAL bug remains for any two-field
invariant), (b) it pollutes the interface with many small properties
that almost always get read together, and (c) it doesn't support
future evolution — adding a new property is a breaking interface
change in C# (well, technically not, but it adds shapes consumers can
depend on individually).

### One global diagnostics service injected via DI

`IFrameFlowDiagnostics` registered as a singleton, queried by everyone.
Rejected because (a) it couples diagnostics to a separate global
lifetime, distinct from controller lifetime — terrible for multi-
controller scenarios, (b) it requires the diagnostics service to know
about every subsystem, and (c) it removes the natural per-subsystem
testing seam.

The controller-level rollup is the right scope for "global" — but
it's owned by the controller it describes, not a separate service.

### Logging only

Counted as the no-op alternative. Rejected because structured queries
need structured state. The Avalonia example can't render `"frames
dropped: 17 (1.2%)"` from log text without re-parsing it; integration
tests can't `Assert.Equal(expected, observed)` against log lines.

### `IPlaybackDiagnostics` as a separate sub-interface

`controller.Diagnostics.Snapshot()` instead of
`controller.GetDiagnostics()`. Considered, deferred. The sub-interface
shape is cleaner if the diagnostics surface grows a lot — e.g., if we
add streaming snapshots or per-counter accessors. For v1, a single
method is simpler and less ceremony; we can promote to a sub-interface
in a follow-up ADR if the surface genuinely earns it.

## Implementation order

Bottom-up; each step is independently testable.

1. **Per-subsystem snapshot records and getters.** One PR per subsystem
   or grouped by layer (`Decoding`, `Playback`, `Audio.OpenAL`,
   `Avalonia`). Pure additive change — no consumers yet.
2. **`PipelineController` aggregation.** Adds a method that returns
   the pipeline-internal counters; the controller-level rollup reads
   from it.
3. **`IPlaybackController.GetDiagnostics()`.** Rolls up
   per-subsystem snapshots + controller-owned state under the
   controller lock.
4. **AvaloniaPlayer example wiring.** Replace estimated FPS with
   `(VideoFramesPresented_delta / interval)`. Add dropped-frames line.
   Add hwaccel-backend chip in the status strip (`HwBadge` already
   exists, currently shows policy mode; will show *bound* backend
   instead).
5. **Integration-test assertions.** Add `VideoFramesDecoded ==
   expected_count` (corpus-driven) and `VideoFramesDropped == 0` to
   the existing `PlayToCompletion` tests. On hwaccel-enabled CI
   runners, assert `HardwareBackend` is not null.
6. **(Follow-up, not this ADR)** Crossbar's `FramePipeline<T>` can
   gain a sibling diagnostics surface using the same pattern. Out of
   scope here because Crossbar lives in a separate repo and version
   train; tracked in the Crossbar issue tracker.

## References

- ADR-0008: Result types and exception boundaries
- ADR-0009: Threading and concurrency model
- ADR-0010: Logging and diagnostics strategy (this ADR is its
  structured-state counterpart)
- ADR-0024: Playback controller as public API surface
- ADR-0027: Public API surface cleanup
- ADR-0033: Hardware decode selection (the `HardwareBackend` field
  this ADR finally surfaces)
- The OpenAL audio race (no ADR; resolved with the state lock prior
  to this work)
