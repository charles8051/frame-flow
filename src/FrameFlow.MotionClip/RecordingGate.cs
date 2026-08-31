// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;

namespace FrameFlow.MotionClip;

/// <summary>
/// State-machine operator that consumes display-resolution BGRA32 frames and
/// emits a <see cref="ClipSegment"/> downstream when a motion-driven clip
/// boundary fires (post-roll quiet window or per-clip frame cap). The encoder
/// is a separate graph node (<see cref="ClipEncoderSink"/>) connected via a
/// <see cref="EdgeOptions.Buffered"/> edge — that's how "queue the next clip
/// while saving" falls out of the substrate instead of needing a Saving state.
/// </summary>
/// <remarks>
/// <para>
/// State: <c>Idle → Building → Idle</c> — no Saving. As soon as a segment is
/// emitted, the gate is ready to start the next one; back-pressure on the
/// gate→encoder edge handles asynchrony.
/// </para>
/// <para>
/// <see cref="ClipEndReason.MaxFramesReached"/> bounds clip length to prevent
/// the "infinite recording under continuous motion" state. Previous design
/// had no cap; if motion fired faster than the post-roll window expired the
/// recorder stayed in Recording indefinitely.
/// </para>
/// <para>
/// <b>No preview coupling.</b> The gate is preview-agnostic. Live preview is
/// a sibling consumer of the upstream display-resolution stream — wired in
/// <see cref="RecorderPipeline.BuildGraph"/> as a fan-out branch with an
/// explicit <see cref="VideoFrameExtensions.CloneCpu"/> cloner (ADR-0054).
/// Slow UI rendering can't back-pressure the gate's pump because the two
/// branches run on independent pumps with independent edge policy.
/// </para>
/// </remarks>
public sealed class RecordingGate : IDisposable
{
    private readonly RecordingGateOptions _options;
    private readonly GateLimits _limits;
    private readonly MotionDetector _motion;
    private readonly PreRollBuffer _preRoll;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;

    // The pure gate state (§5.3): phase + post-roll-remaining + frame-count, threaded as an
    // immutable value through GateCore.Advance. The frame list, the clone, and the trigger
    // timestamp below are the shell's — the messy edges the core deliberately doesn't model.
    private GateState _gate = GateState.Initial;
    private List<IVideoFrame>? _clip;
    private DateTime _triggeredAt;
    private int _preRollAtTrigger;
    private bool _disposed;

    public RecordingGate(
        RecordingGateOptions options,
        ILogger logger,
        TimeProvider? clock = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _limits = new GateLimits(options.PostRollFrames, options.MaxFramesPerClip);
        _logger = logger;
        // Shell-owned clock: the trigger timestamp is stamped here, never read inside the
        // pure fold (mirrors LoopStallEvaluator's clock-passed-in style). Defaults to the
        // system clock; tests can inject a fake.
        _clock = clock ?? TimeProvider.System;
        // Build the sector mask once at construction. Default (null/empty
        // sector list) → all-armed mask, identical-to-historic behaviour.
        // 320×180 matches MotionDetector's internal downsample buffer.
        SectorMask = new MotionSectorMask(320, 180, options.MotionSectors);
        _motion = new MotionDetector(
            pixelThreshold: options.MotionPixelThreshold,
            motionThreshold: options.MotionThreshold,
            warmupFrames: options.WarmupFrames,
            sectorMask: SectorMask
        );
        _preRoll = new PreRollBuffer(options.PreRollFrames);

        // One-time visibility on which numpad sectors are armed. When all
        // 9 are armed the line says "all" so it doesn't suggest masking
        // is in effect when it isn't.
        LogMaskConfigured(
            _logger,
            SectorMask.AllArmed ? "all" : string.Join(",", SectorMask.ArmedSectors),
            SectorMask.ArmedSectors.Count,
            null
        );
    }

    /// <summary>
    /// The numpad-grid mask the detector is using. Exposed so the windowed
    /// shell's preview overlay renders against the same source of truth.
    /// </summary>
    public MotionSectorMask SectorMask { get; }

    /// <summary><see langword="true"/> while a clip is being assembled.</summary>
    public bool IsBuilding => Volatile.Read(ref _phaseFlag) != 0;

    /// <summary>Most recent changed-pixel ratio from the motion detector (for status display).</summary>
    public double LastMotionRatio => _motion.LastMotionRatio;

    /// <summary>Frame count of the in-progress clip (<c>0</c> when not building).</summary>
    public int FramesInCurrentClip => _clip?.Count ?? 0;

    // Cross-thread-visible mirror of _gate.Phase (0 = Idle, 1 = Building) for IsBuilding,
    // which status UIs read off the pump thread. _gate itself is touched only by the single
    // pump (ProcessAsync) / shutdown (Flush); this flag is the one field a reader races, so
    // it carries the Volatile, exactly as the old _state did.
    private int _phaseFlag;

    /// <summary>
    /// Build the operator node bound to this gate's state. Call once per
    /// pipeline construction.
    /// </summary>
    public OperatorNode<VideoFrameRef, ClipSegment> Build(string id = "recording-gate") =>
        new(id, ProcessAsync);

    /// <summary>
    /// Drop the motion-detector's reference frame so the first frame of a
    /// fresh capture session doesn't trigger spurious motion against the last
    /// frame of the previous one. Call at the start of each session.
    /// </summary>
    public void ResetMotion() => _motion.ResetReference();

    /// <summary>
    /// Force-emit any in-progress clip immediately and reset the state machine
    /// to <c>Idle</c>. Returns the emitted segment for the caller to feed
    /// directly to the encoder, since the graph has already stopped and the
    /// segment can't flow through the substrate. Returns <see langword="null"/>
    /// when nothing is building. Use on camera disconnect / app shutdown.
    /// </summary>
    public ClipSegment? Flush()
    {
        if (_gate.Phase != GatePhase.Building || _clip is null)
            return null;
        // Force-finalise whatever is building, regardless of the fold's own boundaries.
        return EmitSegment(ClipEndReason.Flushed);
    }

    private ValueTask<ClipSegment?> ProcessAsync(VideoFrameRef input, CancellationToken ct)
    {
        IVideoFrame frame = input.Frame;

        bool moved = _motion.Process(frame);

        // The whole trigger / pre-roll / post-roll / max-frames decision is the pure fold; the
        // shell only performs the action it names on the frame list it owns (§5.3).
        ClipDecision decision = GateCore.Advance(_gate, moved, _preRoll.Count, _limits);

        // Ownership note: an emitted ClipSegment is returned to the graph substrate, which
        // delivers it to the encoder sink body and disposes the operator's output ref after
        // that body returns (ClipEncoderSink.EnqueueAsync AddRefs for the worker). So this
        // method TRANSFERS ownership of the segment on return — it must not dispose it here.
        // Now that there is a single emit path (EmitOnBuildFrame), the transfer is one seam.
#pragma warning disable CA2000 // segment ownership transfers to the returned-to substrate
        ClipSegment? emit = decision.Action switch
        {
            ClipAction.StartClip => StartBuilding(frame, decision.State),
            ClipAction.EmitClip => EmitOnBuildFrame(frame, decision),
            _ => ContinueOnFrame(frame, decision.State),
        };
#pragma warning restore CA2000

        return ValueTask.FromResult(emit);
    }

    /// <summary>
    /// <see cref="ClipAction.Continue"/>: Idle buffers the frame into the pre-roll ring;
    /// Building appends it to the in-progress clip. Threads the next gate state. Emits nothing.
    /// </summary>
    private ClipSegment? ContinueOnFrame(IVideoFrame frame, GateState next)
    {
        if (next.Phase == GatePhase.Building)
            _clip!.Add(frame.CloneCpu());
        else
            _preRoll.Add(frame);

        SetState(next);
        return null;
    }

    /// <summary>
    /// <see cref="ClipAction.EmitClip"/>: the current frame is the last frame of the clip
    /// (append it), then finalise and emit. The fold's next state is <c>Idle</c>.
    /// </summary>
    private ClipSegment EmitOnBuildFrame(IVideoFrame frame, ClipDecision decision)
    {
        _clip!.Add(frame.CloneCpu());
        SetState(decision.State); // Idle
        return EmitSegment(decision.Reason!.Value);
    }

    /// <summary>
    /// <see cref="ClipAction.StartClip"/>: snapshot+clear the pre-roll into a fresh clip,
    /// append the trigger frame, stamp the trigger time from the shell clock, and begin
    /// Building. Returns <see langword="null"/> (a clip just opened; nothing to emit yet).
    /// </summary>
    private ClipSegment? StartBuilding(IVideoFrame trigger, GateState next)
    {
        IReadOnlyList<IVideoFrame> snapshot = _preRoll.SnapshotAndClear();
        _clip = new List<IVideoFrame>(snapshot.Count + _options.PostRollFrames);
        _clip.AddRange(snapshot);
        _clip.Add(trigger.CloneCpu());
        _preRollAtTrigger = snapshot.Count;
        _triggeredAt = _clock.GetUtcNow().UtcDateTime;
        SetState(next);
        LogRecordingStarted(_logger, snapshot.Count, _motion.LastMotionRatio, null);
        return null;
    }

    private ClipSegment EmitSegment(ClipEndReason reason)
    {
        List<IVideoFrame> frames = _clip!;
        var segment = new ClipSegment(frames, _triggeredAt, _preRollAtTrigger, reason);
        _clip = null;
        SetState(GateState.Initial);
        LogSegmentEmitted(_logger, frames.Count, reason.ToString(), null);
        return segment;
    }

    /// <summary>Threads the new gate value and publishes the cross-thread phase mirror.</summary>
    private void SetState(GateState next)
    {
        _gate = next;
        Volatile.Write(ref _phaseFlag, next.Phase == GatePhase.Building ? 1 : 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preRoll.Dispose();
        if (_clip is { } orphan)
        {
            foreach (IVideoFrame f in orphan)
                f.Dispose();
            _clip = null;
        }
    }

    // ── Logging ───────────────────────────────────────────────────────

    private static readonly Action<ILogger, int, double, Exception?> LogRecordingStarted =
        LoggerMessage.Define<int, double>(
            LogLevel.Information,
            new EventId(1, nameof(LogRecordingStarted)),
            "Motion detected → recording (pre-roll: {PreRollFrames} frames, ratio={MotionRatio:P1})"
        );

    private static readonly Action<ILogger, int, string, Exception?> LogSegmentEmitted =
        LoggerMessage.Define<int, string>(
            LogLevel.Debug,
            new EventId(2, nameof(LogSegmentEmitted)),
            "Clip segment ready ({FrameCount} frames, reason={Reason})"
        );

    private static readonly Action<ILogger, string, int, Exception?> LogMaskConfigured =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(3, nameof(LogMaskConfigured)),
            "Motion sectors armed: {ArmedSectors} ({ArmedCount}/9)"
        );
}

/// <summary>Configuration for <see cref="RecordingGate"/>.</summary>
public sealed record RecordingGateOptions
{
    /// <summary>Frames retained in the pre-roll ring (the look-back window).</summary>
    public required int PreRollFrames { get; init; }

    /// <summary>Frames of "no motion" required to finalise a clip.</summary>
    public required int PostRollFrames { get; init; }

    /// <summary>
    /// Hard cap on a single clip's frame count. When reached, the segment is
    /// emitted regardless of motion state — bounds memory and prevents the
    /// "infinite recording under continuous motion" failure mode.
    /// </summary>
    public required int MaxFramesPerClip { get; init; }

    /// <summary>
    /// Changed-pixel ratio (fraction of the frame) that counts as motion.
    /// Lower = more sensitive. Default 0.02 (2 %).
    /// </summary>
    public double MotionThreshold { get; init; } = 0.02;

    /// <summary>Per-pixel luma delta that counts as "changed" (default 25 / 255).</summary>
    public int MotionPixelThreshold { get; init; } = 25;

    /// <summary>
    /// Frames after each camera (re)connect during which the motion detector
    /// suppresses triggers and keeps re-seeding its reference. Bridges the
    /// camera's auto-exposure / AWB / sensor-gain ramp so it can't be mistaken
    /// for motion. Default 30 (≈1 s at 30 fps); set to <c>FrameRate</c> from
    /// the caller for a frame-rate-independent "1 second" window.
    /// </summary>
    public int WarmupFrames { get; init; } = 30;

    /// <summary>
    /// Numpad-numbered sectors of a 3×3 grid (1-9) that the motion detector
    /// watches. <see langword="null"/> or empty = watch the whole frame
    /// (all 9 sectors armed, identical to the historic behaviour).
    /// </summary>
    /// <remarks>
    /// Layout (numpad convention):
    /// <code>
    ///   7 8 9
    ///   4 5 6
    ///   1 2 3
    /// </code>
    /// When a subset is supplied, the changed-pixel ratio is computed over
    /// the armed-sector area only, NOT the full frame — so
    /// <see cref="MotionThreshold"/> keeps consistent semantics regardless
    /// of how many sectors are armed.
    /// </remarks>
    public IReadOnlyList<int>? MotionSectors { get; init; }
}
