// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// A sink one player or pass at a time can claim (ADR-0085, decision 6). Implemented beside
/// <see cref="IVideoSink"/> or <see cref="IAudioSink"/> by the in-box sinks with state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who claims.</b> <c>PlaybackController.Create</c>, the player builder and the pass builder
/// claim every sink they are given that implements this, video and audio, before they build
/// anything else. A build against a sink another player or pass holds fails with
/// <see cref="InvalidOperationException"/>, naming the sink and its holder. The claim is released
/// once the player's or pass's <c>DisposeAsync</c> has completed, which is after the last frame
/// it sends has reached the sink, and when a build that claimed fails or is cancelled. A pass holds
/// its claim until it is disposed, not until its run ends. So one sink serves several players in
/// sequence, each built after the one before it has been disposed, and never two at once.
/// </para>
/// <para>
/// <b>What it does not see.</b> A graph built by hand, such as one ended with
/// <c>SinkAdapters.AsSinkNode</c> or a <c>Branch</c> edge inside a configurator, takes no claim, and
/// neither does a sink without this interface. Those keep ADR-0044's rule, which nothing enforces.
/// Whether a graph should claim its sinks for its run is #694.
/// </para>
/// <para>
/// <b>A side interface (ADR-0066 §4).</b> Not every sink has state worth guarding:
/// <see cref="NullVideoSink"/> does not implement it, and nothing is added to
/// <see cref="IVideoSink"/> or <see cref="IAudioSink"/>.
/// </para>
/// <para>
/// <b>Implementing it.</b> Compose a <see cref="SinkClaimGate"/>, forward the three members to it,
/// and call <see cref="SinkClaimGate.Close"/> from the sink's <c>DisposeAsync</c>. A sink that
/// presents to exactly one other sink can forward all three to that sink instead, so a claim on the
/// one is a claim on both.
/// </para>
/// </remarks>
public interface IClaimableSink
{
    /// <summary>Tries to claim the sink for <paramref name="claimant"/>, without waiting.</summary>
    /// <param name="claimant">
    /// Who claims it, as an error message names them, such as <c>a player built for 'clip.mp4'</c>.
    /// </param>
    /// <returns>
    /// The lease, or <see langword="null"/> when another claimant holds the sink and
    /// <see cref="Claimant"/> names it. Disposing the lease releases the claim. It may be disposed
    /// from any thread and more than once, and it releases only the claim it took: once released,
    /// it does nothing to a later claim.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="claimant"/> is null, empty or white space.</exception>
    /// <exception cref="ObjectDisposedException">The sink is disposed.</exception>
    IDisposable? TryClaim(string claimant);

    /// <summary>
    /// Who holds the sink, as their claim described them, or <see langword="null"/> when nobody
    /// does. For error messages: the claim may have been released by the time it is read.
    /// </summary>
    string? Claimant { get; }

    /// <summary>
    /// The sink's place in the one order every claim on several sinks takes them in, unique in the
    /// process. Two sinks report the same value only when one forwards its claim to the other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A claim on several sinks tries each without waiting, in this order, and releases the ones
    /// it took when one is held. Two claimants racing for overlapping sets then cannot both fail:
    /// each would hold the sink the other failed on, taken before the one it failed on itself, so
    /// each of those two sinks would come before the other. <see cref="SinkClaimGate.ClaimOrder"/>
    /// supplies it.
    /// </para>
    /// <para>
    /// <b>A sink that presents to several.</b> The order is over the sinks that hold state, not
    /// over the sinks that forward to them. <see cref="FanOutVideoSink"/>, which presents to several
    /// targets, is replaced by its targets, flattened through nested ones, before a build's sinks
    /// are put in order; otherwise a build of a fan-out over two views and an audio sink, and
    /// another over an overlapping pair and the same audio sink, could take the shared sinks in
    /// opposite orders and both fail. It reports an order of its own, which a build never uses. A
    /// sink of your own that presents to several claimable sinks is not flattened: forward a claim
    /// to exactly one sink, or present to several through <see cref="FanOutVideoSink"/>.
    /// </para>
    /// </remarks>
    long ClaimOrder { get; }
}
