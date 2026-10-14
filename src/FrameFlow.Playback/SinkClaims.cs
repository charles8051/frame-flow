// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// Claims the sinks a player, controller or pass is given, before it builds anything else
/// (ADR-0085, decision 6).
/// </summary>
/// <remarks>
/// The controller factories and the pass builder call <see cref="Take"/> first, and hand what it
/// returns to the controller or pass they build, which releases it once its dispose has
/// completed. A build that fails after <see cref="Take"/> releases it before it throws.
/// </remarks>
internal static class SinkClaims
{
    /// <summary>
    /// Claims whichever of <paramref name="videoSink"/> and <paramref name="audioSink"/> implement
    /// <see cref="IClaimableSink"/>, all or none.
    /// </summary>
    /// <param name="claimant">Who claims them, as the error a later build gets names them.</param>
    /// <param name="videoSink">The video sink, or <see langword="null"/>.</param>
    /// <param name="audioSink">The audio sink, or <see langword="null"/>.</param>
    /// <returns>The claims. Disposing them releases them, once.</returns>
    /// <exception cref="InvalidOperationException">
    /// Another player or pass holds one of them. The message names the sink and its holder.
    /// </exception>
    /// <exception cref="ObjectDisposedException">One of them is disposed.</exception>
    public static IDisposable Take(string claimant, IVideoSink? videoSink, IAudioSink? audioSink)
    {
        var sinks = new List<IClaimableSink>(2);
        if (videoSink is IClaimableSink video)
            sinks.Add(video);
        if (audioSink is IClaimableSink audio)
            sinks.Add(audio);

        return SinkClaimSet.TryClaim(sinks, claimant, out var held)
            ?? throw new InvalidOperationException(HeldMessage(held!, held!.Claimant));
    }

    /// <summary>The error a build gets when <paramref name="sink"/> is held. Pure.</summary>
    /// <param name="sink">The sink another player or pass holds.</param>
    /// <param name="holder">
    /// Who holds it, or <see langword="null"/> when the claim was released before it was read.
    /// </param>
    internal static string HeldMessage(IClaimableSink sink, string? holder)
    {
        var role = sink switch
        {
            IVideoSink => "video sink",
            IAudioSink => "audio sink",
            _ => "sink",
        };
        return $"The {role} {sink.GetType().Name} is held by {holder ?? "another player or pass"}. "
            + "One sink serves one player or pass at a time: await DisposeAsync on the player or "
            + "pass already built against it before building another. Stopping it is not enough.";
    }
}
