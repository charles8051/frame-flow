// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Avalonia.Windows.Diagnostics;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// Disposes the composition-interop presenter's producers <i>off</i> the UI thread, once the
/// compositor has finished the in-flight work that uses their rings.
/// </summary>
/// <remarks>
/// <para>
/// Step 4 of the 2026-06-12 teardown-deadlock fix. When
/// <see cref="CompositionInteropVideoView"/> tears down while the compositor is wedged in a
/// display/composition transition (e.g. a remote-desktop connect), the in-flight presents
/// and imported-image disposals do not drain within the bounded timeout. Rather than
/// blocking the UI thread on the producer's keyed-mutex <c>Release</c> (the original
/// deadlock) or abandoning the resources (a GPU/device leak over a long uptime),
/// the view hands the producer ring(s) to this reaper, gated on the same tasks the bounded
/// drain timed out on (<see cref="Enqueue"/>). (The Avalonia drawing surface/visual are disposed up front on the UI
/// thread — they are not part of the keyed-mutex rendezvous and do not block.)
/// </para>
/// <para>
/// A producer the view drops while it presents, to rebuild a converter for a resize, a device
/// change or a new output, or an uploader after device loss, goes the same way
/// (<see cref="Retire"/>), gated on the presents that used its ring (ADR-0086, decision 3): it is
/// not freed under the compositor's work, what its dispose throws is logged, and a wait that does
/// not end is named in the log.
/// </para>
/// <para>
/// The reaper <c>await</c>s the gating tasks — it does <b>not</b> spin or block a thread while
/// waiting, so a never-recovering compositor costs no thread, only the outstanding COM
/// references (which a reboot reclaims, as it must for that scenario anyway). When the
/// compositor recovers and the gating tasks complete, the producer ring's keyed mutex is
/// released and disposing it can no longer block, so the reaper frees everything on a
/// thread-pool thread, never on the thread that completed the gating. Each producer is disposed
/// on its own, so one that throws does not keep the others.
/// </para>
/// </remarks>
internal static class PresenterTeardownReaper
{
    /// <summary>
    /// Schedules disposal of <paramref name="producers"/> (the GPU converters and the CPU
    /// uploader a teardown took; null entries are skipped) to run once <paramref name="gating"/>
    /// (the in-flight present hand-offs and imported-image disposals) completes. Returns
    /// immediately; disposal happens on a thread-pool thread, never the caller's (UI) thread.
    /// Counted as a deferral reclaimed (<c>reaper_completed</c>).
    /// </summary>
    /// <returns>
    /// A task that completes once the producers have been disposed, which may be never if the
    /// compositor never recovers. It never faults: what a dispose throws is logged.
    /// </returns>
    public static Task Enqueue(Task gating, ILogger logger, params IDisposable?[] producers)
    {
        if (producers.All(p => p is null))
            return Task.CompletedTask;

        return ReapAsync(gating, logger, producers);
    }

    /// <summary>
    /// Schedules disposal of <paramref name="producers"/>, which the view dropped while it
    /// presented (null entries are skipped), to run once <paramref name="gating"/> completes: the
    /// compositor has let go of the ring they filled, its imported images disposed and the presents
    /// that used it drained. Returns immediately; disposal happens on a thread-pool thread.
    /// </summary>
    /// <param name="gating">The release of the ring the producers filled.</param>
    /// <param name="logger">Where the disposal, a fault, and a wait that goes on are logged.</param>
    /// <param name="timeProvider">
    /// Times the wait. Once it has gone on for <see cref="SlowWorkWarning.DefaultInterval"/>, a
    /// warning says so, once (ADR-0086, decision 3.3); the producers stay held, not freed.
    /// </param>
    /// <param name="producers">The producers to dispose.</param>
    /// <returns>
    /// A task that completes once the producers have been disposed, which may be never if the
    /// compositor never lets go. It never faults: what a dispose throws is logged.
    /// </returns>
    public static Task Retire(
        Task gating,
        ILogger logger,
        TimeProvider timeProvider,
        params IDisposable?[] producers
    )
    {
        if (producers.All(p => p is null))
            return Task.CompletedTask;

        return RetireAsync(gating, logger, timeProvider, producers);
    }

    private static async Task ReapAsync(Task gating, ILogger logger, IDisposable?[] producers)
    {
        // Wait for the compositor to finish releasing the shared keyed mutex. ConfigureAwait
        // (false) keeps the continuation off any captured (UI) context. Faults are fine — a
        // faulted present has still released its mutex, which is all we need before disposing.
        try
        {
            await gating.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Reaper gating tasks faulted; proceeding to dispose deferred producers.");
        }

        if (await DisposeOffThreadAsync(producers).ConfigureAwait(false) is { } fault)
            logger.LogWarning(fault, "Presenter teardown reaper hit an error disposing deferred producers.");
        else
            logger.LogInformation("Presenter teardown reaper disposed deferred producers after compositor recovery.");

        // Count the reclaim whether or not Dispose threw: the reaper got past the gating await
        // (the compositor recovered), so the deferral is resolved either way. reaper_completed
        // tracking teardowns_deferred is how the field confirms no deferral was ever stranded.
        PresenterTeardownMetrics.RecordReaperCompleted();
    }

    private static async Task RetireAsync(
        Task gating,
        ILogger logger,
        TimeProvider timeProvider,
        IDisposable?[] producers
    )
    {
        var dropped = timeProvider.GetTimestamp();
        try
        {
            await SlowWorkWarning
                .AwaitAsync(
                    gating,
                    SlowWorkWarning.DefaultInterval,
                    timeProvider,
                    () =>
                        logger.LogWarning(
                            "Presenter waits for the compositor to release the ring of a producer it dropped while "
                                + "presenting; the producer is held, not freed, until it does (ADR-0086)."
                        )
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Presenter gating for a dropped producer faulted; disposing it anyway.");
        }

        if (await DisposeOffThreadAsync(producers).ConfigureAwait(false) is { } fault)
        {
            logger.LogWarning(fault, "Presenter hit an error disposing a producer it dropped while presenting.");
            return;
        }

        logger.LogDebug(
            "Presenter disposed a producer it dropped while presenting, {ElapsedMs:F0}ms after the drop, once the "
                + "compositor had released its ring.",
            timeProvider.GetElapsedTime(dropped).TotalMilliseconds
        );
    }

    /// <summary>
    /// Disposes every producer on a thread-pool thread, each on its own, and returns what they
    /// threw: the one exception, or an <see cref="AggregateException"/> of several, or
    /// <see langword="null"/>. The gating may complete on the compositor's thread, and a
    /// producer's dispose can wait on the GPU.
    /// </summary>
    private static Task<Exception?> DisposeOffThreadAsync(IDisposable?[] producers) =>
        Task.Run(() => DisposeEach(producers));

    /// <summary>
    /// Disposes every producer in <paramref name="producers"/>, skipping null entries, each even
    /// when one before it threw. Returns what they threw: the one exception, an
    /// <see cref="AggregateException"/> of several, or <see langword="null"/>.
    /// </summary>
    internal static Exception? DisposeEach(IReadOnlyList<IDisposable?> producers)
    {
        List<Exception>? faults = null;
        foreach (var producer in producers)
        {
            try
            {
                producer?.Dispose();
            }
            catch (Exception ex)
            {
                (faults ??= []).Add(ex);
            }
        }

        return faults switch
        {
            null => null,
            [var one] => one,
            _ => new AggregateException(faults),
        };
    }
}
