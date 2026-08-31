// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding.Internal;

/// <summary>
/// The threaded state of audio PTS synthesis: the cumulative count of output samples
/// (per channel) emitted so far. A value, not a mutable field — threaded by the caller
/// through <see cref="AudioPtsSynthesis.Advance"/>.
/// </summary>
internal readonly record struct PtsSynthesisState(long AccumulatedSamples)
{
    /// <summary>The starting state — no samples emitted yet.</summary>
    public static PtsSynthesisState Initial => new(0);
}

/// <summary>
/// One step of PTS synthesis: the timestamp for this frame, the advanced state, and
/// whether the timestamp was synthesised (vs taken from a real frame PTS).
/// </summary>
internal readonly record struct PtsSynthesisResult(
    TimeSpan Pts,
    PtsSynthesisState State,
    bool UsedSynthetic
);

/// <summary>
/// Pure audio-PTS synthesis (ADR-0055 follow-up). The presentation timestamp of each
/// decoded audio frame is either its real PTS scaled by the time base, or — when the
/// source carries no PTS — synthesised from the cumulative output-sample count at the
/// target sample rate. Modelled as a fold <c>(state, frame) → (pts, state')</c> so the
/// accumulator is an immutable value threaded by the caller rather than a mutated field,
/// and the drift behaviour over a long PTS-less stream is unit-testable with no FFmpeg.
/// </summary>
/// <remarks>
/// The synthesised timestamp is computed from the <em>integer</em> sample count
/// (<c>accumulated / sampleRate</c>) each step, not by accumulating <see cref="TimeSpan"/>
/// deltas, so it stays monotonic and free of accumulating floating-point drift by
/// construction. The accumulator advances by this frame's output samples regardless of
/// whether the real or synthetic timestamp was used, so a stream that starts with valid
/// PTS and later drops to NOPTS continues monotonically from where it was.
/// </remarks>
internal static class AudioPtsSynthesis
{
    /// <summary>
    /// Computes the presentation timestamp for one decoded audio frame and advances the
    /// synthesis state.
    /// </summary>
    /// <param name="state">
    /// The current accumulator; thread it from the previous call, starting at
    /// <see cref="PtsSynthesisState.Initial"/>.
    /// </param>
    /// <param name="hasValidPts">
    /// Whether the frame carries a usable PTS — the caller checks the NOPTS sentinel and a
    /// non-zero time-base denominator before calling.
    /// </param>
    /// <param name="framePts">Raw frame PTS in time-base units (ignored when synthesising).</param>
    /// <param name="timeBaseNum">Time-base numerator (ignored when synthesising).</param>
    /// <param name="timeBaseDen">Time-base denominator (ignored when synthesising).</param>
    /// <param name="outputSamplesPerChannel">Samples produced for this frame (per channel); advances the accumulator.</param>
    /// <param name="targetSampleRate">Output sample rate in Hz, used to convert sample counts to time.</param>
    public static PtsSynthesisResult Advance(
        PtsSynthesisState state,
        bool hasValidPts,
        long framePts,
        int timeBaseNum,
        int timeBaseDen,
        int outputSamplesPerChannel,
        int targetSampleRate
    )
    {
        TimeSpan pts;
        bool usedSynthetic;

        if (hasValidPts)
        {
            pts = TimeSpan.FromSeconds((double)framePts * timeBaseNum / timeBaseDen);
            usedSynthetic = false;
        }
        else
        {
            // Synthesise from the count accumulated BEFORE this frame's samples are added,
            // so the first PTS-less frame lands at t=0.
            pts = TimeSpan.FromSeconds((double)state.AccumulatedSamples / targetSampleRate);
            usedSynthetic = true;
        }

        var next = new PtsSynthesisState(state.AccumulatedSamples + outputSamplesPerChannel);
        return new PtsSynthesisResult(pts, next, usedSynthetic);
    }
}
