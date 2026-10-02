// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback.Core;

/// <summary>
/// Whether a video pacer with nothing to present is starved, and the edges that report it as an
/// underrun and as ready again (#547). Pure: the pacer's state in, a decision out.
/// </summary>
/// <param name="Starved">True once an underrun has been reported and not yet resolved.</param>
/// <remarks>
/// <para>
/// An empty ring is starved when it has waited <see cref="Threshold"/> while playing, after this
/// run presented a frame, with more input to come. The startup and a seek wait for their first
/// frame without counting, and so does a paused pacer, whose clock the user stopped.
/// </para>
/// <para>
/// A pause forgets the starvation without reporting it resolved. The controller leaves
/// Rebuffering for Paused on its own, and after the resume the wait is timed again, so a
/// source still stalled reports a fresh underrun rather than playing on in silence.
/// </para>
/// </remarks>
internal readonly record struct PacerStarvation(bool Starved)
{
    /// <summary>The shortest wait that counts as starved, whatever the frame rate.</summary>
    public static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(1);

    /// <summary>How many of the last frame's display intervals an empty ring waits before it is starved.</summary>
    public const int FrameIntervals = 3;

    /// <summary>
    /// How long an empty ring waits before it is starved: <see cref="FrameIntervals"/> times the
    /// last presented frame's duration, and at least <see cref="MinimumWait"/>. A source with
    /// sparse frames is given longer, so the gap between two of its frames is not an underrun.
    /// </summary>
    public static TimeSpan Threshold(TimeSpan lastFrameDuration)
    {
        var intervals = lastFrameDuration > TimeSpan.Zero ? lastFrameDuration * FrameIntervals : TimeSpan.Zero;
        return intervals > MinimumWait ? intervals : MinimumWait;
    }

    /// <summary>Whether an empty ring's wait counts toward starvation now.</summary>
    public bool Times(bool paused, bool inputComplete, bool presentedThisRun) =>
        !Starved && !paused && !inputComplete && presentedThisRun;

    /// <summary>The ring waited out <see cref="Threshold"/> still empty: report an underrun.</summary>
    public (PacerStarvation Next, bool RaiseUnderrun) TimedOut() => (new(true), !Starved);

    /// <summary>
    /// A frame arrived, or the input completed: report ready if an underrun was reported, so a
    /// stream that ends while starved returns to playing before its end is reported.
    /// </summary>
    public (PacerStarvation Next, bool RaiseReady) Resolved() => (new(false), Starved);

    /// <summary>Paused: forget the starvation without reporting it resolved.</summary>
    public PacerStarvation Forgotten() => new(false);
}
