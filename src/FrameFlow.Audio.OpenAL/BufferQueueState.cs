// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Audio.OpenAL;

/// <summary>
/// The kind of source state the shell observed when deciding whether a staging upload
/// can proceed — the device-state input to <see cref="BufferQueueState"/>'s pure
/// decisions, decoupled from Silk.NET's <c>SourceState</c> enum so the value layer
/// carries no OpenAL types.
/// </summary>
public enum AlSourceState
{
    /// <summary>The source is playing (or has never been started) — buffers will drain.</summary>
    PlayingOrInitial,

    /// <summary>The source is paused — no buffer will be processed until resume.</summary>
    Paused,

    /// <summary>The source is stopped (underran or deactivated) — no buffer will drain.</summary>
    Stopped,
}

/// <summary>
/// What <see cref="BufferQueueState.PlanUpload"/> decided the shell should do with the
/// pending staging buffer this attempt — the value-level expression of the sink's
/// <c>FlushStep</c>, computed from scalar device reads instead of inline against the
/// live OpenAL source.
/// </summary>
public enum UploadDecision
{
    /// <summary>Nothing to upload (staging empty / sink inert). Stop the flush loop.</summary>
    Nothing,

    /// <summary>A free buffer exists — upload the staging bytes now.</summary>
    Upload,

    /// <summary>No free buffer and the source is still draining — await a buffer return, then retry.</summary>
    NeedBuffer,

    /// <summary>No free buffer and the source is paused/stopped — no buffer will return; abandon the flush.</summary>
    Abort,
}

/// <summary>
/// Result of <see cref="BufferQueueState.ObserveUnderrun"/>: the threaded-through next
/// state plus whether an underrun was just detected (so the shell logs and bumps its
/// lock-free underrun counter).
/// </summary>
public readonly record struct UnderrunOutcome(BufferQueueState Next, bool Underran);

/// <summary>
/// Result of <see cref="BufferQueueState.ObserveQueueDepth"/>: the threaded-through next
/// state plus whether the pre-buffer gate just opened (so the shell calls
/// <c>SourcePlay</c>).
/// </summary>
public readonly record struct StartOutcome(BufferQueueState Next, bool ShouldStartPlayback);

/// <summary>
/// The <b>pure core</b> of the OpenAL sink's buffer-queue control logic (§5.2): the PCM
/// coalesce gate, the underrun decision, the backpressure/upload decision, and the
/// pre-buffer playback-start gate, all as total transforms over scalar inputs. No IO, no
/// OpenAL handle, no lock.
/// </summary>
/// <remarks>
/// <para>
/// <b>What lifts and what stays.</b> Three concerns the sink kept fused are split here.
/// The OpenAL buffer <i>handles</i> (the <c>_freeBuffers</c> queue, the staging
/// <c>short[]</c> bytes) are device resources and stay in the shell, which owns the
/// <c>Dequeue → BufferData → SourceQueueBuffers</c> sequence and every
/// <c>AL_SOURCE_STATE</c> / <c>AL_BUFFERS_QUEUED</c> read. The lock-free diagnostic
/// <i>tallies</i> (<c>_underrunCount</c> / <c>_backpressureCount</c>) stay as the sink's
/// <c>Interlocked</c>/<c>Volatile</c> fields, because the public <c>UnderrunCount</c> /
/// <c>BackpressureCount</c> properties read them from other threads <i>without</i> the
/// sink lock — moving them into this lock-protected value would change that threading
/// contract. What lifts out is the <b>decision state</b>: the staging fill level, the
/// source-started latch, and the four predicates that drive them. Same split as
/// <see cref="AudioClockState"/> — the value owns threaded state, the shell owns the live
/// device reads and the lock-free counters.
/// </para>
/// <para>
/// <b>Behaviour preserved exactly.</b> Each transform reproduces the corresponding inline
/// predicate in <c>TryFlushStagingBufferOnce</c> / <c>PresentAsync</c>:
/// </para>
/// <list type="bullet">
/// <item><see cref="AppendStaging"/> + <see cref="ShouldFlush"/> — the
/// <c>_stagingCount += samples.Length; flushNeeded = _stagingCount &gt;= CoalesceTargetSamples</c>
/// gate.</item>
/// <item><see cref="ObserveUnderrun"/> — the once-per-flush
/// <c>if (firstPass &amp;&amp; _sourceStarted &amp;&amp; state == Stopped) { underrun++; _sourceStarted = false; }</c>
/// (the tally bump stays in the shell; this clears the latch and reports the event).</item>
/// <item><see cref="PlanUpload"/> — the
/// <c>_freeBuffers.Count == 0 ? (paused/stopped ? Abort : NeedBuffer) : Upload</c> branch,
/// plus the empty-staging / inert early-out.</item>
/// <item><see cref="ObserveQueueDepth"/> — the
/// <c>if (!_sourceStarted &amp;&amp; queued &gt;= PreBufferCount) SourcePlay()</c> pre-buffer
/// gate, latching <see cref="SourceStarted"/>.</item>
/// </list>
/// <para>
/// Immutable; state is threaded through the outcome records and nothing is mutated, so the
/// queue decisions are exhaustively unit-testable with no device — the gap §5.2 targets,
/// where the only buffer-queue coverage today is the device-gated end-to-end backpressure
/// test that "passes trivially" with no audio device.
/// </para>
/// </remarks>
public readonly record struct BufferQueueState
{
    private readonly int _coalesceTargetSamples;
    private readonly int _preBufferCount;

    /// <summary>
    /// Creates the initial queue state for a sink configured with the given coalesce
    /// target and pre-buffer depth (the sink's <c>CoalesceTargetSamples</c> /
    /// <c>PreBufferCount</c> constants). Staging empty, source not started.
    /// </summary>
    public static BufferQueueState Create(int coalesceTargetSamples, int preBufferCount)
    {
        if (coalesceTargetSamples < 1)
            throw new ArgumentOutOfRangeException(
                nameof(coalesceTargetSamples),
                coalesceTargetSamples,
                "coalesce target must be >= 1."
            );
        if (preBufferCount < 1)
            throw new ArgumentOutOfRangeException(
                nameof(preBufferCount),
                preBufferCount,
                "pre-buffer count must be >= 1."
            );
        return new BufferQueueState(coalesceTargetSamples, preBufferCount, stagingCount: 0, sourceStarted: false);
    }

    private BufferQueueState(
        int coalesceTargetSamples,
        int preBufferCount,
        int stagingCount,
        bool sourceStarted
    )
    {
        _coalesceTargetSamples = coalesceTargetSamples;
        _preBufferCount = preBufferCount;
        StagingCount = stagingCount;
        SourceStarted = sourceStarted;
    }

    /// <summary>
    /// Number of valid interleaved samples currently coalesced in the staging buffer —
    /// the sink's <c>_stagingCount</c>. The actual <c>short[]</c> bytes live in the shell;
    /// this tracks only the fill level the flush gate keys off.
    /// </summary>
    public int StagingCount { get; init; }

    /// <summary>
    /// Whether playback has been started on the source (the pre-buffer gate has fired and
    /// no underrun has since stopped it) — the sink's <c>_sourceStarted</c>.
    /// </summary>
    public bool SourceStarted { get; init; }

    /// <summary>
    /// Appends <paramref name="sampleCount"/> interleaved samples to the staging fill
    /// level (the sink's <c>_stagingCount += samples.Length</c>). The bytes themselves are
    /// copied into the shell's <c>short[]</c>; this only advances the count.
    /// </summary>
    public BufferQueueState AppendStaging(int sampleCount)
    {
        if (sampleCount < 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount, "sampleCount must be >= 0.");
        return this with { StagingCount = StagingCount + sampleCount };
    }

    /// <summary>
    /// Whether the coalesced staging buffer has reached the upload threshold — the sink's
    /// <c>_stagingCount &gt;= CoalesceTargetSamples</c> flush gate.
    /// </summary>
    public bool ShouldFlush => StagingCount >= _coalesceTargetSamples;

    /// <summary>
    /// Marks the staging buffer drained after a successful upload — the sink's
    /// <c>_stagingCount = 0</c> after <c>BufferData</c>/<c>SourceQueueBuffers</c>, and on
    /// the deactivate path where residual post-EOF staging is dropped.
    /// </summary>
    public BufferQueueState ClearStaging() => this with { StagingCount = 0 };

    /// <summary>
    /// The once-per-flush underrun check (first pass only). Reproduces
    /// <c>if (firstPass &amp;&amp; _sourceStarted &amp;&amp; state == Stopped) { underrun++; _sourceStarted = false; }</c>:
    /// an underrun is detected only when this is the first attempt of the flush, playback
    /// was started, and the device has since stopped (starved). On detection
    /// <see cref="SourceStarted"/> clears so the pre-buffer gate re-arms, and
    /// <see cref="UnderrunOutcome.Underran"/> tells the shell to bump its lock-free
    /// underrun tally and log.
    /// </summary>
    /// <param name="firstPass">Whether this is the first upload attempt of the current flush.</param>
    /// <param name="sourceState">The live source state the shell just read.</param>
    public UnderrunOutcome ObserveUnderrun(bool firstPass, AlSourceState sourceState)
    {
        if (firstPass && SourceStarted && sourceState == AlSourceState.Stopped)
            return new UnderrunOutcome(this with { SourceStarted = false }, Underran: true);

        return new UnderrunOutcome(this, Underran: false);
    }

    /// <summary>
    /// Decides what to do with the pending staging buffer this attempt — the value-level
    /// <c>FlushStep</c>. Reproduces <c>TryFlushStagingBufferOnce</c>'s branch structure:
    /// empty staging (or inert sink) → <see cref="UploadDecision.Nothing"/>; a free buffer
    /// exists → <see cref="UploadDecision.Upload"/>; no free buffer with the source
    /// paused/stopped → <see cref="UploadDecision.Abort"/>; no free buffer with the source
    /// draining → <see cref="UploadDecision.NeedBuffer"/>.
    /// </summary>
    /// <param name="sinkActive">
    /// Whether the sink is activated and not disposed (the shell's <c>_al is not null &amp;&amp; !_disposed</c>).
    /// When false the upload is a no-op regardless of staging.
    /// </param>
    /// <param name="freeBufferCount">Number of free OpenAL buffer handles available in the shell's pool.</param>
    /// <param name="sourceState">The live source state the shell read when the pool is empty.</param>
    public UploadDecision PlanUpload(bool sinkActive, int freeBufferCount, AlSourceState sourceState)
    {
        if (!sinkActive || StagingCount == 0)
            return UploadDecision.Nothing;

        if (freeBufferCount == 0)
        {
            return sourceState is AlSourceState.Paused or AlSourceState.Stopped
                ? UploadDecision.Abort
                : UploadDecision.NeedBuffer;
        }

        return UploadDecision.Upload;
    }

    // Note: the backpressure tally (the sink's _backpressureCount) is NOT carried here.
    // It is incremented lock-free in the shell's FlushStagingBufferAsync because the
    // public BackpressureCount property reads it off-thread without _stateLock; folding it
    // into this lock-protected value would change that threading contract.

    /// <summary>
    /// The pre-buffer playback-start gate, evaluated after a buffer was queued. Reproduces
    /// <c>if (!_sourceStarted &amp;&amp; queued &gt;= PreBufferCount) { SourcePlay(); _sourceStarted = true; }</c>:
    /// when playback has not started and the queue depth has reached the pre-buffer count,
    /// latches <see cref="SourceStarted"/> and reports that the shell should call
    /// <c>SourcePlay</c>.
    /// </summary>
    /// <param name="queuedDepth">The live <c>AL_BUFFERS_QUEUED</c> count after the upload.</param>
    public StartOutcome ObserveQueueDepth(int queuedDepth)
    {
        if (!SourceStarted && queuedDepth >= _preBufferCount)
            return new StartOutcome(this with { SourceStarted = true }, ShouldStartPlayback: true);

        return new StartOutcome(this, ShouldStartPlayback: false);
    }

    /// <summary>
    /// Clears the source-started latch without touching the staging level — the sink's
    /// <c>_sourceStarted = false</c> on deactivate and in the resume-after-underrun path,
    /// where the source drained while paused and the pre-buffer gate must re-arm.
    /// </summary>
    public BufferQueueState MarkSourceStopped() => this with { SourceStarted = false };

    /// <summary>
    /// Resets the per-activation decision state for a fresh activation / loop restart —
    /// staging drained and source not started — matching the sink's
    /// <c>_sourceStarted = false; _stagingCount = 0;</c> reset on every <c>ActivateAsync</c>
    /// path. The configured coalesce target and pre-buffer depth are preserved. (The
    /// lock-free underrun/backpressure tallies are reset separately in the shell, since
    /// they are not carried here.)
    /// </summary>
    public BufferQueueState ResetForActivation() =>
        new(_coalesceTargetSamples, _preBufferCount, stagingCount: 0, sourceStarted: false);
}
