# ADR-0066: The sink contract shape — no shared base, and the substrate adapter

> Drafted on branch `claude/sink-contract-adr`; assigned ADR-0066 on merge to
> `main` per this repository's "number-at-merge" rule.

## Status

Accepted. Records the model the sink interfaces already follow, documents the
`AsSinkNode` adapter mechanism, and **rejects** the `ISink<T>` shared base
proposed in #93.

**Amended 2026-08-26** after an independent review. The rejection stands; the
cost figure that accompanied it in §2 was wrong and has been corrected. See
§2's "Correction" note — the reparent is nearly free, which strengthens the
case for waiting rather than weakening it.

**Date:** 2026-08-26
**Tracking:** `charles8051/frame-flow` #93, closing the #92 sink-contract epic.
**Related:**
- ADR-0030 (unify frame contracts with Crossbar) — establishes the frame
  contracts the adapter carries. It does **not** describe the adapter itself,
  which is the gap §3 closes.
- ADR-0044 (sink ownership and disposal) — the ownership rule the adapter
  implements. Unchanged.
- ADR-0035 (master clock interface split) and ADR-0065 (capability discovery) —
  the two capability splits that this ADR generalizes into a rule.
- ADR-0049 (FrameFlow.Graph fork) — removed the upstream churn that reverted
  the two earlier unification attempts.

## Context

`IVideoSink` and `IAudioSink` share a suffix, a method name, and a disposal
contract. Nothing else. They have no common base type, and their remaining
members do not overlap:

| | `IVideoSink` | `IAudioSink` |
|---|---|---|
| dataflow | `PresentAsync(IVideoFrame, ct)` | `PresentAsync(IAudioBuffer, ct)` |
| resources | `FramePool`, `OnFormatChangedAsync` | `ActivateAsync` / `PauseAsync` / `ResumeAsync` / `DeactivateAsync` |
| side-implemented | — | `IClockSource` (ADR-0035), `IVolumeControl` (ADR-0065) |
| diagnostics | `VideoSinkDiagnosticsSnapshot` | `AudioSinkDiagnosticsSnapshot` |

### They were never designed as a pair

`IAudioSink` was born in the scaffold commit `28f3e83` (2026-03-28) under
`src/FrameFlow.Audio/`, shaped as an audio device driver: `Capabilities` /
`StartAsync` / `WriteAsync` / `PauseAsync` / `ResumeAsync` / `StopAsync` /
`GetPlaybackTime()`.

`IVideoSink` arrived ten days later in `12d2de7` (2026-04-07) under
`src/FrameFlow.Media/`, shaped by the frame-memory problem: `FramePool` /
`SupportedMemoryDomains` / `PresentAsync` / `OnFormatChangedAsync`. Neither
commit references the other.

### Unification was attempted twice and reverted twice

Both attempts tracked upstream Crossbar type churn rather than a FrameFlow need:

1. `abeda22` / `cb3c178` — `IAudioSink` implements `Crossbar.IFrameSink<T>`;
   `WriteAsync` marked `[Obsolete]`.
2. `709c368` — both drop the `IFrameSink<T>` base for a `FrameConsumer<T>`
   delegate property, because Crossbar deleted the interface. The commit states
   the intended model: sinks become *"exclusively the resource/lifecycle facet;
   the dataflow facet is a delegate property."*
3. `04ab378` — `Consumer` deleted and `PresentAsync` promoted back to a
   first-class interface method, because Crossbar deleted the delegate.

What survived is the cosmetic half of a unification whose structural half was
abandoned: matching method names, a matching ADR-0044 disposal contract, and a
matching ADR-0034 `GetDiagnostics()` shape, with no shared type underneath.

ADR-0049 forked `FrameFlow.Graph` with no ongoing sync, so the churn that
reverted attempts 1 and 2 is gone. That is what made a third attempt worth
evaluating rather than assuming.

## Decision

### 1. A sink is one dataflow method plus whatever resources its medium requires

This is the model. Everything else follows from it.

Video output is a surface: it owns a frame pool and has a format that can change
mid-stream. Audio output is a device: it has a transport, and may publish a
sample-counter clock and a gain stage. The differing members are the media, not
an accident of two files written ten days apart.

This is already stated in both interfaces' XML docs (#96). This ADR makes it
durable, so the next reader who notices the asymmetry finds a decision rather
than re-deriving the confusion.

### 2. No shared base interface. `ISink<T>` is rejected

#93 proposed `ISink<in T> : IAsyncDisposable { ValueTask PresentAsync(T, ct); }`
with both interfaces reparented onto it. Rejected, because nothing consumes it.

Every site that touches both sinks handles them separately, by concrete type,
because what it does with each differs:

- `PlaybackGraph.cs:89-100` builds a different chain per medium: different
  decoder, different node type, different wrapper.
- `SubstrateSession.cs:191-192` rolls up two different snapshot types.
- `SinkAdapters` already shares its body — see §3 — through a **delegate**
  parameter, not an interface. This was the one concrete consumer #93 pointed
  at, and #94 closed it without needing a base type.

Contravariance on `in T` would be sound. It would also be unused.

#### Correction: the reparent is nearly free

This ADR originally claimed the reparent "touches 26 implementations across 21
files." **That is false**, and an independent review caught it by building the
change rather than reasoning about it.

Interface inheritance in C# is transitive. A class that satisfies
`IVideoSink.PresentAsync(IVideoFrame, ct)` already satisfies an inherited
`ISink<IVideoFrame>.PresentAsync` of the same signature. Reparenting therefore
costs:

```
 M src/FrameFlow.Media/IAudioSink.cs      base changed, redundant member removed
 M src/FrameFlow.Media/IVideoSink.cs      same
 ?? src/FrameFlow.Media/ISink.cs          new
```

Two files changed, one added, **zero of the 26 implementations touched**.
Verified on `a91525e` in a disposable worktree: `dotnet build FrameFlow.slnx`
clean, `FrameFlow.Media.Tests` 69/69.

The 26 implementations across 22 files are a real inventory of what implements
these interfaces. They are not a migration cost, and citing them as one
overstated the case for rejecting.

#### Why the rejection stands anyway

The decision rests on §2's opening sentence alone: nothing consumes it.

The corrected cost does not argue for adding it now. It argues the opposite.
Because the reparent is nearly free, it will be just as nearly free on the day a
consumer appears — so there is no option value in adding the type early. What
adding it early does buy is a public type on a pre-1.0 library's surface with
nothing behind it: documented, visible in IntelliSense, and inviting the next
reader to ask what it is for. ADR-0006 warns against exactly that trade.

Cheap-to-add and cheap-to-defer is a case for deferring, not for speculating.

**What would change this answer.** A consumer that genuinely needs to hold "some
sink" without knowing which medium — a generic teardown loop, a registry keyed
by sink, a diagnostics rollup that does not switch on the snapshot type. None
exists today. If one appears, introduce `ISink<T>` then, with the consumer in
the same change so the base type has a reason to exist on the day it lands.

This is the third evaluation. Recording the rejection is the point: without it,
a fourth reader sees two interfaces that look like they should share a base and
starts the cycle again. The trigger condition above is what a fourth evaluation
should test, rather than re-deriving the cost.

### 3. The substrate adapter, and what ADR-0030 does not say

`SinkAdapters.AsSinkNode` bridges both interfaces onto the substrate's
`SinkNode<T>`. Until now no ADR documented the mechanism, and the XML docs cited
ADR-0030, which covers the frame *contracts* the adapter carries but says
nothing about the adapter. This section closes that gap (#102, item 3).

**Shape.** Two public extension methods, one per sink interface, sharing a
private generic body:

```csharp
public static SinkNode<VideoFrameRef> AsSinkNode(this IVideoSink sink, string id = "video-sink")
    => Adapt<VideoFrameRef, IVideoFrame>(id, static r => r.Detach(), sink.PresentAsync);

private static SinkNode<TRef> Adapt<TRef, TPayload>(
    string id,
    Func<TRef, TPayload?> detach,
    Func<TPayload, CancellationToken, ValueTask> present)
    where TRef : class, IRefCounted
    where TPayload : class;
```

**Why the public overloads stay separate.** `SinkNode<T>`
(`src/FrameFlow.Graph/Nodes.cs:165`) and `GraphChain<T>.To`
(`GraphChain.cs:64`) are invariant, so one public generic would need two type
parameters, and C# cannot partially infer them. Every call site would have to
spell both out. The overloads keep `videoSink.AsSinkNode()` inferring.

**Why the helper takes a delegate rather than an interface.** It needs
`PresentAsync` and nothing else. A delegate parameter expresses exactly that,
and it is why §2's rejection costs nothing.

**Ownership transfer (ADR-0044).** The substrate hands the adapter a refcounted
wrapper holding one ref. The adapter `Detach`es the payload and hands it to the
sink, which owns it and disposes it after presenting. Detaching rather than
`AddRef`ing matters twice: `IVideoFrame.AddRef` is rejected by one-shot decoder
frames and converter outputs, and the substrate's subsequent wrapper-dispose
becomes a no-op because the wrapper's slot is null. Exactly one reference is
outstanding at every point.

A null `Detach` result is not an error. It means the wrapper was drained
upstream, so there is nothing to present and the body returns without touching
the sink.

**Lifecycle is not the adapter's.** Callers construct, activate, dispose, and
(for audio) wire the clock themselves. The adapter is a data-plane shim.

### 4. Capabilities are side-implemented interfaces, not members and not flags

Generalizing ADR-0035 and ADR-0065 into a rule, so the next capability does not
need its own debate:

- A capability that only some sinks have is its own interface, implemented
  alongside. `IClockSource` and `IVolumeControl` are the two instances.
- It is **not** a member on the sink interface with a documented no-op fallback.
  That was `AudioSinkCapabilities.SupportsVolumeControl`, and its failure mode
  was a write that looked like it worked.
- It is **not** a boolean on a capability record. Three of that record's four
  flags duplicated information the type system already carried.
- Consumers holding a sink discover it by type test. Consumers holding an
  `IMediaPlayer` cannot — they never see the sink — so the player surface
  carries a `Supports…` property defined as exactly that type test (ADR-0065 §2).

A capability stays a member when every sink of that medium has it. The audio
transport is the example: `ActivateAsync` and friends are called at nine sites
across `SubstrateSession`, `MediaPlayer`, and `MediaPlaylistPlayer`. Making them
optional would convert nine direct calls into nine type tests to buy symmetry
with a video-surface transport that does not exist.

## Consequences

`IVideoSink` and `IAudioSink` stay as they are. #93 closes without code changes.

The XML docs on both interfaces should cite this ADR for the dataflow contract
instead of ADR-0030, which is the last of #102's items.

The asymmetry is now a recorded decision rather than an observation. A future
reader who wants to unify them has three reverted or rejected attempts and a
stated trigger condition to weigh first.

## Alternatives considered

**`ISink<in T>` with both interfaces reparented.** The subject of #93. Rejected
in §2: the variance is sound, and the type has no consumer. Not rejected on
cost — the reparent is nearly free, and §2's "Correction" explains why that
argues for deferring rather than for adding it now.

**`ISink<T>` carrying `GetDiagnostics()` too.** Worse. The snapshot types differ
and a common base for them would exist only to be downcast.

**Rename the interfaces to stop implying symmetry** — `IVideoSurface` /
`IAudioDevice`. Tempting, and closer to what they are. Rejected because "sink"
is the substrate's word for a graph terminal, and both types are graph terminals
first. The naming is accurate at the layer that matters; §1's documentation is
the cheaper fix for the confusion.

**Do nothing.** Rejected. The unification question has now been opened three
times. An ADR that says "no, and here is what would change the answer" is what
stops a fourth.
