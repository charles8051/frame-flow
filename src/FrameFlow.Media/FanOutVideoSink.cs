// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.ExceptionServices;
using FrameFlow.Media.Core;
using FrameFlow.Media.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Media;

/// <summary>
/// A video sink that presents each frame it is given to several sinks, in step (ADR-0085,
/// decision 8). Register it with a player as its one video sink, and the player's pacer presents
/// each frame to every target when that frame is due.
/// </summary>
/// <remarks>
/// <para>
/// <b>A fan-out sink or a <c>Branch</c>.</b> A <c>Branch</c> forks the graph, ahead of the pacer,
/// so a sink at the end of a branch shows each frame as soon as the branch delivers it, ahead of
/// the picture the player paces (#636). Use a <c>Branch</c> for consumers that do not present,
/// such as inference or recording, and this sink for pictures that must stay in step. It is a
/// sink, not a second way to fork a graph (ADR-0054, ADR-0078).
/// </para>
/// <para>
/// <b>Targets.</b> Given at construction, at least one, and fixed from then on, so fan-outs cannot
/// form a cycle. None is null, and no sink is a target twice, including through a nested fan-out.
/// A target can come with a transform that makes its frame from each one the fan-out is given
/// (<see cref="FanOutTarget.Transform"/>), such as a filtered copy.
/// </para>
/// <para>
/// <b>Delivery.</b> <see cref="PresentAsync"/> presents to each target in turn, on the calling
/// thread, so a slow target or transform delays the ones after it. A plain target is given a
/// reference taken just before its call, and a transformed one the transform's output; either way
/// the target owns what it is given, even when its call throws. Every target is called even when
/// one before it throws or is cancelled. Then the fan-out disposes the reference it was given,
/// and throws what the targets threw: one exception as it is; several together as an
/// <see cref="AggregateException"/>, in target order, or the first of them when every one is an
/// <see cref="OperationCanceledException"/>. <see cref="OnFormatChangedAsync"/> reaches every
/// target the same way.
/// </para>
/// <para>
/// <b>What it declares</b> is read from its targets when it is made.
/// <see cref="MaxHeldFrames"/> is the sum of theirs, <see cref="AcceptedDomains"/> the domains
/// every target takes, and <see cref="PreferredBackends"/> the first list a target names. A
/// fan-out whose targets take no domain in common is refused. One that takes fewer domains than a
/// target would alone logs it once, when it is made: a fan-out over a zero-copy view and a CPU
/// view hands both CPU frames.
/// </para>
/// <para>
/// <b>Claims (ADR-0085, decision 6).</b> It is an <see cref="IClaimableSink"/>, and a claim on it
/// is a claim on every claimable sink beneath it, through nested fan-outs, taken in the one
/// claim order, all or none. A player or pass built against it therefore holds the views it
/// presents to, and a build against a target another player holds fails, naming that target and
/// its holder. A target without the interface is fed but not claimed. A fan-out no player or pass
/// uses holds no claims.
/// </para>
/// <para>
/// <b>Ownership (ADR-0044).</b> It owns none of its targets: disposing it disposes none of them,
/// and they are the caller's to dispose after it. Once it is disposed it disposes each frame it is
/// given without presenting it, a format change reaches no target, and a claim on it throws
/// <see cref="ObjectDisposedException"/>.
/// </para>
/// </remarks>
public sealed partial class FanOutVideoSink : IVideoSink, IClaimForwardingSink
{
    private static readonly VideoSinkMeters Meters = new(
        "FrameFlow.FanOut.Sink",
        "frameflow.fanout.sink",
        nameof(FanOutVideoSink)
    );

    private readonly FanOutTarget[] _targets;
    private readonly IReadOnlyList<IClaimableSink> _claimTargets;
    private readonly long _claimOrder = SinkClaimGate.NextClaimOrder();
    private readonly VideoSinkTelemetry _telemetry = new(Meters);
    private int _disposed;

    /// <summary>Creates a fan-out that presents each frame to <paramref name="targets"/>, in order.</summary>
    /// <param name="targets">The sinks to present to. The fan-out owns none of them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="targets"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="targets"/> is empty, holds a null, or names a sink twice, including through
    /// a nested fan-out; or the targets take no memory domain in common.
    /// </exception>
    public FanOutVideoSink(params IVideoSink[] targets)
        : this(Plain(targets), logger: null) { }

    /// <summary>
    /// Creates a fan-out that presents each frame to <paramref name="targets"/>, in order, through
    /// each target's transform where it has one.
    /// </summary>
    /// <param name="targets">The targets. The fan-out owns none of their sinks.</param>
    /// <exception cref="ArgumentNullException"><paramref name="targets"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="targets"/> is empty, holds a null, or names a sink twice, including through
    /// a nested fan-out; or the targets take no memory domain in common.
    /// </exception>
    public FanOutVideoSink(params FanOutTarget[] targets)
        : this((IEnumerable<FanOutTarget>)targets, logger: null) { }

    /// <summary>
    /// Creates a fan-out that presents each frame to <paramref name="targets"/>, in order, and
    /// logs to <paramref name="logger"/> when it takes fewer memory domains than a target would.
    /// </summary>
    /// <param name="targets">The targets. The fan-out owns none of their sinks.</param>
    /// <param name="logger">Where to log, once, a target whose domains the fan-out narrows.</param>
    /// <exception cref="ArgumentNullException"><paramref name="targets"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="targets"/> is empty, holds a null, or names a sink twice, including through
    /// a nested fan-out; or the targets take no memory domain in common.
    /// </exception>
    public FanOutVideoSink(IEnumerable<FanOutTarget> targets, ILogger<FanOutVideoSink>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        _targets = [.. targets];
        if (_targets.Length == 0)
            throw new ArgumentException("A fan-out needs at least one target.", nameof(targets));
        for (var i = 0; i < _targets.Length; i++)
        {
            if (_targets[i] is null)
                throw new ArgumentException($"Target {i} is null.", nameof(targets));
        }
        RefuseRepeatedSinks(_targets);

        var domains = Array.ConvertAll(_targets, static target => target.AcceptedDomains);
        AcceptedDomains = FanOutRules.AcceptedDomains(domains);
        if (AcceptedDomains == FrameMemoryDomains.None)
        {
            throw new ArgumentException(
                $"The targets take no memory domain in common: {DescribeDomains(_targets)}.",
                nameof(targets)
            );
        }
        MaxHeldFrames = FanOutRules.MaxHeldFrames(
            _targets.Select(static target => target.Sink.MaxHeldFrames)
        );
        PreferredBackends = FanOutRules.PreferredBackends(
            _targets.Select(static target => target.Sink.PreferredBackends)
        );

        // A build claims these in the fan-out's place. The fan-out's own entry among them makes a
        // claim on a disposed fan-out throw, as one on a disposed in-box sink does.
        _claimTargets =
        [
            new Disposal(this),
            .. _targets.Select(static target => target.Sink).OfType<IClaimableSink>(),
        ];

        var narrowed = FanOutRules.Narrowed(domains, AcceptedDomains);
        if (narrowed.Count > 0)
        {
            LogNarrowed(
                logger ?? (ILogger)NullLogger.Instance,
                AcceptedDomains,
                DescribeDomains(narrowed.Select(i => _targets[i]))
            );
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Presents <paramref name="frame"/> to every target in order, and disposes the reference it
    /// was given once they have all been called. After <see cref="DisposeAsync"/> it disposes the
    /// frame and calls no target (ADR-0085, decision 4).
    /// </remarks>
    /// <exception cref="AggregateException">More than one target threw, not all of them cancelled.</exception>
    public async ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (Volatile.Read(ref _disposed) != 0)
        {
            frame.Dispose();
            return;
        }

        var pts = frame.Pts;
        List<Exception>? thrown = null;
        try
        {
            foreach (var target in _targets)
            {
                try
                {
                    await target.PresentAsync(frame, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    (thrown ??= []).Add(ex);
                }
            }
        }
        finally
        {
            // The reference the fan-out was given. Each target was given one of its own.
            frame.Dispose();
        }

        if (thrown is null)
            _telemetry.RecordPresented(pts);
        else
            Throw(thrown);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Tells every target, even when one before it throws or is cancelled, then throws what they
    /// threw as <see cref="PresentAsync"/> does. After <see cref="DisposeAsync"/> it tells none.
    /// A transform keeps the format, so every target is told the same one.
    /// </remarks>
    public async ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (Volatile.Read(ref _disposed) != 0)
            return;

        List<Exception>? thrown = null;
        foreach (var target in _targets)
        {
            try
            {
                await target.Sink.OnFormatChangedAsync(format, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (thrown ??= []).Add(ex);
            }
        }

        if (thrown is not null)
            Throw(thrown);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The fan-out's own counts: a frame is presented when every target took it without throwing.
    /// It drops nothing itself. Each target counts its own presents and drops.
    /// </remarks>
    public VideoSinkDiagnosticsSnapshot GetDiagnostics() => _telemetry.Snapshot();

    /// <inheritdoc />
    /// <remarks>
    /// The sum of the targets' counts, or <see langword="null"/> when any target's is. The targets
    /// keep the same frames, so the sum can count one frame more than once, which is the safe
    /// direction for a pool sized from it.
    /// </remarks>
    public int? MaxHeldFrames { get; }

    /// <inheritdoc />
    /// <remarks>
    /// The domains every target takes, a transformed target's being those its transform takes.
    /// </remarks>
    public FrameMemoryDomains AcceptedDomains { get; }

    /// <inheritdoc />
    /// <remarks>The first non-empty list among the targets, in order.</remarks>
    public IReadOnlyList<HardwareDecodeBackendKind> PreferredBackends { get; }

    /// <inheritdoc />
    IDisposable? IClaimableSink.TryClaim(string claimant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimant);
        return SinkClaimSet.TryClaim(_claimTargets, claimant, out _);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Who holds a sink beneath it, the first in claim order that is held, or
    /// <see langword="null"/> when none is.
    /// </remarks>
    string? IClaimableSink.Claimant =>
        SinkClaimSet
            .InClaimOrder(_claimTargets)
            .Select(static sink => sink.Claimant)
            .FirstOrDefault(static claimant => claimant is not null);

    /// <inheritdoc />
    /// <remarks>Its own, which a build never uses: a build claims the sinks beneath it instead.</remarks>
    long IClaimableSink.ClaimOrder => _claimOrder;

    /// <inheritdoc />
    IReadOnlyList<IClaimableSink> IClaimForwardingSink.ClaimTargets => _claimTargets;

    /// <inheritdoc />
    /// <remarks>
    /// Disposes none of the targets. A second call does nothing.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private static IEnumerable<FanOutTarget> Plain(IVideoSink[] targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        for (var i = 0; i < targets.Length; i++)
        {
            if (targets[i] is null)
                throw new ArgumentException($"Target {i} is null.", nameof(targets));
        }
        return Array.ConvertAll(targets, FanOutTarget.Of);
    }

    // A sink reached twice, directly or through nested fan-outs, would be given each frame twice.
    private static void RefuseRepeatedSinks(FanOutTarget[] targets)
    {
        var seen = new HashSet<IVideoSink>(ReferenceEqualityComparer.Instance);
        foreach (var sink in targets.SelectMany(static target => Reached(target.Sink)))
        {
            if (!seen.Add(sink))
            {
                throw new ArgumentException(
                    $"The {sink.GetType().Name} is a target more than once, directly or through a "
                        + "nested fan-out. Each sink is a target once.",
                    nameof(targets)
                );
            }
        }
    }

    // The sink itself and, for a fan-out, every sink beneath it.
    private static IEnumerable<IVideoSink> Reached(IVideoSink sink) =>
        sink is FanOutVideoSink nested
            ? [sink, .. nested._targets.SelectMany(static target => Reached(target.Sink))]
            : [sink];

    private static string DescribeDomains(IEnumerable<FanOutTarget> targets) =>
        string.Join(
            "; ",
            targets.Select(static target =>
                target.HasTransform
                    ? $"the transform for {target.Sink.GetType().Name} takes {target.AcceptedDomains}"
                    : $"{target.Sink.GetType().Name} takes {target.AcceptedDomains}"
            )
        );

    private static void Throw(List<Exception> thrown) =>
        ExceptionDispatchInfo.Throw(FanOutRules.Failure(thrown)!);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "FanOutVideoSink takes {Domains} frames, fewer domains than these targets take alone: {Narrowed}."
    )]
    private static partial void LogNarrowed(ILogger logger, FrameMemoryDomains domains, string narrowed);

    /// <summary>
    /// The fan-out's own entry among the sinks a claim on it takes. Any number of claimants pass
    /// it, since it guards nothing, but a claim on a disposed fan-out throws here.
    /// </summary>
    private sealed class Disposal(FanOutVideoSink owner) : IClaimableSink
    {
        public IDisposable? TryClaim(string claimant)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(claimant);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref owner._disposed) != 0, owner);
            return NoClaim.Instance;
        }

        public string? Claimant => null;

        public long ClaimOrder { get; } = SinkClaimGate.NextClaimOrder();
    }

    /// <summary>A lease on nothing.</summary>
    private sealed class NoClaim : IDisposable
    {
        public static NoClaim Instance { get; } = new();

        public void Dispose() { }
    }
}
