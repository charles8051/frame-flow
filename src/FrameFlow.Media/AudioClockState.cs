// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// The <b>pure core</b> of an audio master clock (§5.2): an immutable value carrying the
/// clock's threaded state — the source-time origin, whether that origin has been seated,
/// and the cumulative processed-sample count — plus total transforms over it.
/// No IO, no device handle, no clock, no lock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Backend-neutral.</b> The equation below needs a per-channel processed-sample count and
/// a device playback cursor, and nothing else. Any sink that can report those two quantities
/// drives this value: <c>OpenAlAudioSink</c> does today, and the SDL audio sink proposed in
/// ADR-0018 §2 would. It lived inside <c>FrameFlow.Audio.OpenAL</c> only because OpenAL was
/// the first backend to need it, which put it out of reach of the second.
/// </para>
/// <para>
/// <b>Why a pure core.</b> The master clock is what <c>AudioMasterSyncStrategy</c>
/// (and the presenter-side <c>ClockSelectVideoSink</c> pacer) reads to time every
/// video frame, so its arithmetic is A/V-sync-load-bearing. Before this split the
/// math was only reachable through the live OpenAL source handle under the sink's
/// <c>_stateLock</c>, so every clock test had to be
/// <c>[RequiresAudioDeviceFact]</c> and "passed trivially" with no device. Lifting
/// the decision into a value makes it the same kind of exhaustively unit-testable
/// transform the in-tree exemplars use — <c>ClockSelectBuffer.Select(now, dropped)</c>
/// and <c>LoopStallEvaluator.Observe(sample)</c> — wrapped by a thin shell that owns
/// the AL handle, the lock, and the timing.
/// </para>
/// <para>
/// <b>The clock equation.</b> Published position is
/// <c>BaseSourceTime + (ProcessedSamplesPerChannel + deviceSampleOffset) / sampleRate</c>.
/// Three quantities, three owners:
/// </para>
/// <list type="bullet">
/// <item><see cref="BaseSourceTime"/> — the source-stream origin, seated at
/// activation (seek target or first-buffer PTS). Owned by this value.</item>
/// <item><see cref="ProcessedSamplesPerChannel"/> — samples in buffers the device has
/// finished and the sink has recycled, accumulated across the session. Owned by
/// this value; the shell advances it via <see cref="WithProcessed"/> as it
/// recycles buffers.</item>
/// <item><c>deviceSampleOffset</c> — the live per-buffer playback cursor read
/// fresh from the device on every clock read (<c>AL_SAMPLE_OFFSET</c> under
/// OpenAL). Owned by the device;
/// the shell passes it into <see cref="Position"/> and it is never stored here
/// (storing a device cursor in an immutable value would make it instantly
/// stale).</item>
/// </list>
/// <para>
/// <b>Origin policy.</b> The origin is seated one of two ways, mirroring the sink's
/// <c>SeatBaseSourceTimeOnActivate</c> / <c>SeekBaseline</c> / first-buffer-capture
/// logic exactly:
/// </para>
/// <list type="bullet">
/// <item><see cref="SeatOnActivate"/> — at each activation. With a pending seek seed
/// the origin becomes the seek target and is marked seated (so the first post-seek
/// buffer's possibly-stale PTS is ignored). Absent a seed the origin resets to zero
/// and is marked <i>un</i>seated, to be discovered from the first buffer.</item>
/// <item><see cref="CaptureFirstBufferPts"/> — when the first buffer after an
/// activation arrives and the origin is not yet seated, the origin is captured from
/// that buffer's PTS. A no-op once seated.</item>
/// <item><see cref="SeekBaseline"/> — an out-of-band reseat (the
/// <c>ISeekableClock.SeekBaseline</c> seed) that sets the origin, marks it seated,
/// and zeroes the processed count, while also retaining the seed so the next
/// activation re-seats to the same target.</item>
/// </list>
/// <para>
/// <b>Deactivation</b> (<see cref="OnDeactivate"/>) returns the clock to its initial
/// state — origin zero, unseated, processed zero, no pending seed — matching the
/// sink clearing <c>_baseSourceTime</c> / <c>_baseSourceTimeCaptured</c> /
/// <c>_pendingSeekBaseline</c> on <c>DeactivateAsync</c>.
/// </para>
/// </remarks>
public readonly record struct AudioClockState
{
    /// <summary>The clock's initial state: origin zero, unseated, no samples, no pending seek seed.</summary>
    public static AudioClockState Initial =>
        new(baseSourceTime: TimeSpan.Zero, originSeated: false, processedSamplesPerChannel: 0, pendingSeekBaseline: null);

    private AudioClockState(
        TimeSpan baseSourceTime,
        bool originSeated,
        long processedSamplesPerChannel,
        TimeSpan? pendingSeekBaseline
    )
    {
        BaseSourceTime = baseSourceTime;
        OriginSeated = originSeated;
        ProcessedSamplesPerChannel = processedSamplesPerChannel;
        PendingSeekBaseline = pendingSeekBaseline;
    }

    /// <summary>
    /// The source-stream origin (PTS coordinate) the published clock is measured
    /// from — the sink's <c>_baseSourceTime</c>. Combined with the sample counts in
    /// <see cref="Position"/>.
    /// </summary>
    public TimeSpan BaseSourceTime { get; init; }

    /// <summary>
    /// Whether <see cref="BaseSourceTime"/> has been seated (by a seek seed or a
    /// captured first-buffer PTS) — the sink's <c>_baseSourceTimeCaptured</c>. While
    /// false, <see cref="CaptureFirstBufferPts"/> will seat the origin from the next
    /// buffer.
    /// </summary>
    public bool OriginSeated { get; init; }

    /// <summary>
    /// Cumulative per-channel samples in buffers the device has finished playing and the
    /// sink has recycled — the sink's <c>_processedSamplesPerChannel</c>. The shell
    /// advances this via <see cref="WithProcessed"/> as <c>RecycleProcessedBuffers</c>
    /// returns buffers; the live in-flight cursor is added separately in
    /// <see cref="Position"/>.
    /// </summary>
    public long ProcessedSamplesPerChannel { get; init; }

    /// <summary>
    /// A pending seek seed retained for the next activation (the sink's
    /// <c>_pendingSeekBaseline</c>). Non-null after <see cref="SeekBaseline"/> until
    /// the next <see cref="SeatOnActivate"/> consumes it or <see cref="OnDeactivate"/>
    /// drops it.
    /// </summary>
    public TimeSpan? PendingSeekBaseline { get; init; }

    /// <summary>
    /// Computes the published playback position from the live device cursor.
    /// Reproduces <c>GetPlaybackTimeUnderLock</c> bit-for-bit: when the clock is not
    /// yet measuring (no positive sample rate) it returns the bare origin; otherwise
    /// it returns <c>BaseSourceTime + (ProcessedSamplesPerChannel + deviceSampleOffset) / sampleRate</c>.
    /// </summary>
    /// <param name="deviceSampleOffset">
    /// The live per-buffer playback cursor, per channel, that the shell read fresh from
    /// the device for this call (<c>AL_SAMPLE_OFFSET</c> under OpenAL). Never stored on
    /// the value.
    /// </param>
    /// <param name="sampleRate">
    /// The source sample rate in Hz (the sink's <c>_sampleRate</c>). A value &lt;= 0
    /// means no buffer has been presented yet (or the sink is inactive), so the
    /// position is just the origin — exactly the sink's early-return guard.
    /// </param>
    public TimeSpan Position(long deviceSampleOffset, int sampleRate)
    {
        // Mirror GetPlaybackTimeUnderLock's guard: _al is null / disposed / rate<=0
        // all collapse to "return _baseSourceTime" in the shell. The pure value can't
        // see _al/_disposed, so the shell decides those; here the rate<=0 arm is the
        // value-level expression of "not measuring yet".
        if (sampleRate <= 0)
            return BaseSourceTime;

        long totalSamples = ProcessedSamplesPerChannel + deviceSampleOffset;
        return BaseSourceTime + TimeSpan.FromSeconds((double)totalSamples / sampleRate);
    }

    /// <summary>
    /// Advances the cumulative processed-sample count by
    /// <paramref name="additionalSamplesPerChannel"/> (one recycled buffer's worth, or
    /// a batch) — the sink's <c>_processedSamplesPerChannel += totalSamples</c> inside
    /// <c>RecycleProcessedBuffers</c>.
    /// </summary>
    public AudioClockState WithProcessed(long additionalSamplesPerChannel) =>
        this with { ProcessedSamplesPerChannel = ProcessedSamplesPerChannel + additionalSamplesPerChannel };

    /// <summary>
    /// Seats the origin at activation (the sink's <c>SeatBaseSourceTimeOnActivate</c>),
    /// called after the processed count is reset to zero. Honours a
    /// <see cref="PendingSeekBaseline"/> seed (origin = seek target, marked seated,
    /// seed consumed) over the default first-buffer discovery (origin = zero,
    /// unseated). Always zeroes the processed count, matching the sink resetting
    /// <c>_processedSamplesPerChannel = 0</c> immediately before seating on every
    /// activation path.
    /// </summary>
    public AudioClockState SeatOnActivate()
    {
        if (PendingSeekBaseline is { } seed)
            return new AudioClockState(seed, originSeated: true, processedSamplesPerChannel: 0, pendingSeekBaseline: null);

        return new AudioClockState(TimeSpan.Zero, originSeated: false, processedSamplesPerChannel: 0, pendingSeekBaseline: null);
    }

    /// <summary>
    /// Captures the origin from the first post-activation buffer's PTS when not yet
    /// seated — the sink's <c>if (!_baseSourceTimeCaptured) { _baseSourceTime = pts; … }</c>
    /// in <c>PresentAsync</c>. A no-op once the origin is seated (so only the very
    /// first buffer can establish it), and it does not touch the seek seed or the
    /// processed count.
    /// </summary>
    public AudioClockState CaptureFirstBufferPts(TimeSpan presentationTime)
    {
        if (OriginSeated)
            return this;

        return this with { BaseSourceTime = presentationTime, OriginSeated = true };
    }

    /// <summary>
    /// Applies an out-of-band seek reseat (the sink's <c>SeekBaseline</c> body): sets
    /// the origin to <paramref name="position"/>, marks it seated, retains the seed for
    /// the next activation, and zeroes the processed count. The immediate seat covers a
    /// seek that does not recycle the sink; the retained <see cref="PendingSeekBaseline"/>
    /// covers the deactivate/reactivate seek path, which re-seats via
    /// <see cref="SeatOnActivate"/>.
    /// </summary>
    public AudioClockState SeekBaseline(TimeSpan position) =>
        new(position, originSeated: true, processedSamplesPerChannel: 0, pendingSeekBaseline: position);

    /// <summary>
    /// Returns the clock to its <see cref="Initial"/> state on deactivation — origin
    /// zero, unseated, processed zero, seed dropped — matching the sink clearing
    /// <c>_baseSourceTime</c> / <c>_baseSourceTimeCaptured</c> /
    /// <c>_processedSamplesPerChannel</c> / <c>_pendingSeekBaseline</c> in
    /// <c>DeactivateAsync</c> (so a never-consumed seed can't leak into a later,
    /// unrelated activation).
    /// </summary>
    public AudioClockState OnDeactivate() => Initial;
}
