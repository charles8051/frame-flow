// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// The claims one claimant holds on several sinks, taken all or none in the one claim order
/// (ADR-0085, decisions 6 and 8). Disposing it releases them all.
/// </summary>
/// <remarks>
/// The controller factories and the pass builder claim a build's video and audio sinks through it.
/// It replaces each <see cref="IClaimForwardingSink"/> with its targets, flattened through nested
/// ones, and takes what remains in <see cref="IClaimableSink.ClaimOrder"/>. So the order is global
/// over the sinks that hold state, two builds racing for overlapping sets cannot both fail, and a
/// sink reached twice is claimed once.
/// </remarks>
internal sealed class SinkClaimSet : IDisposable
{
    private readonly IDisposable[] _leases;

    private SinkClaimSet(IDisposable[] leases) => _leases = leases;

    /// <summary>
    /// Tries to claim every sink in <paramref name="sinks"/> for <paramref name="claimant"/>,
    /// without waiting, in their claim order.
    /// </summary>
    /// <param name="sinks">
    /// The sinks to claim. A forwarding sink stands for its targets. A sink that appears twice, or
    /// two that share a claim order, are claimed once.
    /// </param>
    /// <param name="claimant">Who claims them.</param>
    /// <param name="held">
    /// When the claim fails, the first sink in the claim order that another claimant held, which is
    /// never a forwarding sink; otherwise <see langword="null"/>.
    /// </param>
    /// <returns>
    /// The claims on all of them, or <see langword="null"/> when one was held, in which case none
    /// of them is claimed.
    /// </returns>
    /// <exception cref="ObjectDisposedException">
    /// One of them is disposed. None of them is claimed.
    /// </exception>
    public static SinkClaimSet? TryClaim(
        IEnumerable<IClaimableSink> sinks,
        string claimant,
        out IClaimableSink? held
    )
    {
        ArgumentNullException.ThrowIfNull(sinks);

        var leases = new List<IDisposable>();
        try
        {
            foreach (var sink in InClaimOrder(sinks))
            {
                if (sink.TryClaim(claimant) is not { } lease)
                {
                    held = sink;
                    ReleaseInReverse(leases);
                    return null;
                }
                leases.Add(lease);
            }
        }
        catch
        {
            ReleaseInReverse(leases);
            throw;
        }

        held = null;
        return new SinkClaimSet([.. leases]);
    }

    /// <summary>
    /// The sinks <see cref="TryClaim"/> takes for <paramref name="sinks"/>, in the order it takes
    /// them: each forwarding sink replaced by its targets, flattened, then by claim order, each
    /// claim order once. Pure.
    /// </summary>
    internal static IReadOnlyList<IClaimableSink> InClaimOrder(IEnumerable<IClaimableSink> sinks) =>
        [.. Flattened(sinks).DistinctBy(sink => sink.ClaimOrder).OrderBy(sink => sink.ClaimOrder)];

    // A forwarding sink stands for the sinks it claims, through any number of nested ones.
    private static IEnumerable<IClaimableSink> Flattened(IEnumerable<IClaimableSink> sinks) =>
        sinks.SelectMany(sink =>
            sink is IClaimForwardingSink forwarding ? Flattened(forwarding.ClaimTargets) : [sink]
        );

    /// <summary>
    /// Releases every claim, last taken first. Safe from any thread; a second call does nothing,
    /// because each lease releases only once.
    /// </summary>
    public void Dispose() => ReleaseInReverse(_leases);

    // Releases each lease even if one before it throws, then rethrows the first failure.
    private static void ReleaseInReverse(IReadOnlyList<IDisposable> leases)
    {
        Exception? first = null;
        for (var i = leases.Count - 1; i >= 0; i--)
        {
            try
            {
                leases[i].Dispose();
            }
            catch (Exception ex)
            {
                first ??= ex;
            }
        }

        if (first is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
    }
}
