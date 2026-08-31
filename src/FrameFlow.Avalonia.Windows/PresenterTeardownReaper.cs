// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Avalonia.Windows.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// Completes a deferred composition-interop presenter teardown <i>off</i> the UI thread,
/// once the compositor has finished its in-flight work.
/// </summary>
/// <remarks>
/// <para>
/// Step 4 of the 2026-06-12 teardown-deadlock fix. When
/// <see cref="CompositionInteropVideoView"/> tears down while the compositor is wedged in a
/// display/composition transition (e.g. a remote-desktop connect), the in-flight presents
/// and imported-image disposals do not drain within the bounded timeout. Rather than
/// blocking the UI thread on the producer's keyed-mutex <c>Release</c> (the original
/// deadlock) or abandoning the resources (a GPU/device leak over a long-uptime deployment),
/// the view hands the producer ring(s) to this reaper, gated on the same tasks the bounded
/// drain timed out on. (The Avalonia drawing surface/visual are disposed up front on the UI
/// thread — they are not part of the keyed-mutex rendezvous and do not block.)
/// </para>
/// <para>
/// The reaper <c>await</c>s those tasks — it does <b>not</b> spin or block a thread while
/// waiting, so a never-recovering compositor costs no thread, only the outstanding COM
/// references (which a reboot reclaims, as it must for that scenario anyway). When the
/// compositor recovers and the gating tasks complete, the producer ring's keyed mutex is
/// released and disposing it can no longer block, so the reaper frees everything on a
/// thread-pool thread. Disposal is best-effort and exception-isolated.
/// </para>
/// </remarks>
internal static class PresenterTeardownReaper
{
    /// <summary>
    /// Schedules disposal of <paramref name="converter"/> + <paramref name="uploader"/> to
    /// run once <paramref name="gating"/> (the in-flight present hand-offs and imported-image
    /// disposals) completes. Returns immediately; disposal happens on a thread-pool thread,
    /// never the caller's (UI) thread.
    /// </summary>
    public static void Enqueue(
        Task gating,
        D3D11Nv12SharedConverter? converter,
        D3D11BgraUploader? uploader,
        ILogger logger)
    {
        if (converter is null && uploader is null)
            return;

        _ = ReapAsync(gating, converter, uploader, logger);
    }

    private static async Task ReapAsync(
        Task gating,
        D3D11Nv12SharedConverter? converter,
        D3D11BgraUploader? uploader,
        ILogger logger)
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

        try
        {
            converter?.Dispose();
            uploader?.Dispose();
            logger.LogInformation("Presenter teardown reaper disposed deferred producers after compositor recovery.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Presenter teardown reaper hit an error disposing deferred producers.");
        }

        // Count the reclaim whether or not Dispose threw: the reaper got past the gating await
        // (the compositor recovered), so the deferral is resolved either way. reaper_completed
        // tracking teardowns_deferred is how the field confirms no deferral was ever stranded.
        PresenterTeardownMetrics.RecordReaperCompleted();
    }
}
