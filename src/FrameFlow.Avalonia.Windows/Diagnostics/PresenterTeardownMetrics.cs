// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FrameFlow.Avalonia.Windows.Diagnostics;

/// <summary>
/// Process-wide telemetry for the zero-copy composition-interop presenter's
/// <b>teardown</b> and <b>device-loss</b> lifecycle (investigation 2026-06-12,
/// the cross-device keyed-mutex teardown-ordering fix).
/// </summary>
/// <remarks>
/// <para>
/// These counters exist so the fix can be <i>observed in the field</i>: the live
/// deadlock needs real signage hardware plus a wedged display transition (a
/// remote-desktop connect — Splashtop or similar — during a view detach) and
/// cannot be reproduced in a unit test. Scrape them with <c>dotnet-counters</c>
/// on the device via the <c>FrameFlow.Presenter</c> meter and watch a transition:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>teardowns_completed</c> ticking with a low <c>teardown_duration_ms</c>
///     is the healthy path — the new ordering drained the compositor and disposed
///     the producer rings without blocking the UI thread.
///   </description></item>
///   <item><description>
///     <c>teardowns_deferred</c> ticking is the <b>money signal</b>: the compositor
///     did <i>not</i> drain within the bounded timeout (it was wedged), so the fix
///     handed the producer rings to the background reaper instead of blocking the
///     UI thread on a keyed-mutex <c>Release</c> — i.e. it just caught a teardown
///     that would previously have <i>deadlocked</i> the whole app.
///   </description></item>
///   <item><description>
///     <c>reaper_completed</c> should track <c>teardowns_deferred</c>: when they are
///     equal, every deferral was reclaimed once the compositor recovered (no leak).
///     A persistent gap means a compositor that never came back — the only scenario
///     the fix accepts a held COM reference for, and one a reboot resolves anyway.
///   </description></item>
///   <item><description>
///     <c>device_lost_rebuilds</c> counts genuine D3D11 device-loss (TDR /
///     <c>DEVICE_REMOVED</c>) the presenter rode out by dropping + rebuilding the
///     converter on the next frame, rather than blocking.
///   </description></item>
/// </list>
/// <para>
/// All instruments are free-threaded; the record methods may be called from the UI
/// thread (teardown begin), a thread-pool teardown continuation (completed/deferred),
/// or the reaper's thread-pool continuation (reaper) without external synchronization.
/// </para>
/// </remarks>
public static class PresenterTeardownMetrics
{
    private static readonly Meter Meter = new("FrameFlow.Presenter", "1.0.0");

    private static readonly Counter<long> TeardownsCompleted = Meter.CreateCounter<long>(
        "frameflow.presenter.teardowns_completed",
        unit: "{teardown}",
        description: "Presenter teardowns that drained in-flight presents within the bounded timeout and "
            + "disposed the producer rings off the UI thread (the healthy path)."
    );

    private static readonly Counter<long> TeardownsDeferred = Meter.CreateCounter<long>(
        "frameflow.presenter.teardowns_deferred",
        unit: "{teardown}",
        description: "Presenter teardowns where the compositor did not drain within the timeout (e.g. a wedged "
            + "display transition); producer disposal was deferred to the background reaper rather than blocking "
            + "the UI thread. A nonzero value is the teardown-deadlock fix catching a would-have-hung teardown."
    );

    private static readonly Counter<long> ReaperCompleted = Meter.CreateCounter<long>(
        "frameflow.presenter.reaper_completed",
        unit: "{teardown}",
        description: "Deferred presenter teardowns the background reaper finished reclaiming after the compositor "
            + "recovered. Tracks teardowns_deferred: equal counts mean every deferral was reclaimed (no leak)."
    );

    private static readonly Counter<long> DeviceLostRebuilds = Meter.CreateCounter<long>(
        "frameflow.presenter.device_lost_rebuilds",
        unit: "{rebuild}",
        description: "Times the presenter observed D3D11 device-loss (TDR / DEVICE_REMOVED) on the converter or "
            + "uploader and dropped it for rebuild on the next frame, instead of blocking (step 6 guard)."
    );

    private static readonly Counter<long> DeviceChangeRebinds = Meter.CreateCounter<long>(
        "frameflow.presenter.device_change_rebinds",
        unit: "{rebind}",
        description: "Times the presenter rebound the GPU converter's decode bridge in place because an incoming "
            + "frame's decode device differed from the one it was bound to — a warm-sink player swap "
            + "(ADR-0064 Decision 2). The converter owns its own device, so the ring + compositor imports "
            + "stay warm and no rebuild is paid. A steady climb matched to playlist item boundaries is the "
            + "expected gapless-playlist signature."
    );

    private static readonly Counter<long> DeviceChangeRebuilds = Meter.CreateCounter<long>(
        "frameflow.presenter.device_change_rebuilds",
        unit: "{rebuild}",
        description: "Times the presenter fell back to a full GPU-converter rebuild on a decode-device change "
            + "because the in-place decode-bridge rebind failed (a driver that would not open the shared NV12 on "
            + "the new device). Since the converter owns its device (ADR-0064 Decision 2) it normally rebinds — "
            + "see device_change_rebinds — so this is a 0-valued regression/fallback alarm: a steady climb means "
            + "the durable fix is not engaging on this GPU and every swap is paying a rebuild (ADR-0064)."
    );

    private static readonly Counter<long> ResolutionChangeRebuilds = Meter.CreateCounter<long>(
        "frameflow.presenter.device_resolution_rebuilds",
        unit: "{rebuild}",
        description: "Times the presenter rebuilt the GPU converter because an incoming frame's dimensions "
            + "differed from the cached converter's (a mixed-resolution playlist item boundary). The converter's "
            + "ring + staging textures are sized at construction, so a resolution change requires a rebuild, not an "
            + "in-place decode-bridge rebind — distinct from device_change_rebinds (same-size warm swap)."
    );

    private static readonly Counter<long> Stalls = Meter.CreateCounter<long>(
        "frameflow.presenter.stalls",
        unit: "{stall}",
        description: "Times the present-stall watchdog found the presenter frozen (the UI-thread "
            + "VideoProcessorBlt wedged in the GPU driver) while the sink kept accepting frames. The host must "
            + "rebuild the decode pipeline; the presenter cannot self-recover from a device-level wedge (§9)."
    );

    private static readonly Histogram<double> TeardownDurationMs = Meter.CreateHistogram<double>(
        "frameflow.presenter.teardown_duration_ms",
        unit: "ms",
        description: "Wall-clock from teardown start to the compositor draining in-flight presents (healthy path "
            + "only). A distribution creeping toward the bounded timeout warns the compositor is getting sluggish."
    );

    /// <summary>
    /// Stamps the start of a presenter teardown. Returns an opaque <see cref="Stopwatch"/>
    /// timestamp to hand to <see cref="RecordCompleted"/> so the healthy-path duration is
    /// measured from the same origin. Cheap and allocation-free.
    /// </summary>
    public static long BeginTeardown() => Stopwatch.GetTimestamp();

    /// <summary>
    /// Records a teardown that drained cleanly: increments <c>teardowns_completed</c> and
    /// records the elapsed time (since <paramref name="startTimestamp"/>) on
    /// <c>teardown_duration_ms</c>. Returns the elapsed milliseconds so the caller can log it.
    /// </summary>
    public static double RecordCompleted(long startTimestamp)
    {
        double elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        TeardownDurationMs.Record(elapsedMs);
        TeardownsCompleted.Add(1);
        return elapsedMs;
    }

    /// <summary>
    /// Records a teardown that timed out draining and was deferred to the reaper
    /// (increments <c>teardowns_deferred</c>) — the fix catching a would-have-deadlocked path.
    /// </summary>
    public static void RecordDeferred() => TeardownsDeferred.Add(1);

    /// <summary>
    /// Records the reaper finishing a deferred teardown after compositor recovery
    /// (increments <c>reaper_completed</c>).
    /// </summary>
    public static void RecordReaperCompleted() => ReaperCompleted.Add(1);

    /// <summary>
    /// Records a D3D11 device-loss drop-and-rebuild of the converter/uploader
    /// (increments <c>device_lost_rebuilds</c>).
    /// </summary>
    public static void RecordDeviceLostRebuild() => DeviceLostRebuilds.Add(1);

    /// <summary>
    /// Records the GPU converter rebinding its decode bridge in place on a decode-device change
    /// (warm-sink player swap, ADR-0064; increments <c>device_change_rebinds</c>). The healthy
    /// gapless-playlist signature: the ring + compositor imports stayed warm, no rebuild was paid.
    /// </summary>
    public static void RecordDeviceChangeRebind() => DeviceChangeRebinds.Add(1);

    /// <summary>
    /// Records a <b>fallback</b> full converter rebuild on a decode-device change because the
    /// in-place rebind failed (increments <c>device_change_rebuilds</c>). Normally 0 — the
    /// converter owns its device and rebinds (<see cref="RecordDeviceChangeRebind"/>); a nonzero
    /// value is a regression alarm that the durable fix is not engaging on this GPU (ADR-0064).
    /// </summary>
    public static void RecordDeviceChangeRebuild() => DeviceChangeRebuilds.Add(1);

    /// <summary>
    /// Records a converter rebuild forced by an incoming frame whose dimensions differ from the
    /// cached converter's — a mixed-resolution playlist swap (increments
    /// <c>device_resolution_rebuilds</c>). The ring + staging textures are fixed-size, so a
    /// resolution change cannot be a rebind; it must rebuild. Distinct from a warm same-size swap.
    /// </summary>
    public static void RecordResolutionChangeRebuild() => ResolutionChangeRebuilds.Add(1);

    /// <summary>
    /// Records a detected present-stall (increments <c>stalls</c>) — the presenter frozen in a hung
    /// GPU Blt while frames kept arriving.
    /// </summary>
    public static void RecordStall() => Stalls.Add(1);
}
