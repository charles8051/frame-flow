using System.Diagnostics.Metrics;
using FrameFlow.Avalonia.Windows;
using FrameFlow.Avalonia.Windows.Diagnostics;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// Unit tests for the pure converter-action decision (ADR-0064): given the cached
/// zero-copy converter's bound decode device + dimensions and an incoming frame's decode-device
/// identity + dimensions, decide whether to reuse it, rebuild it after device-loss, rebuild it on a
/// resolution change, or <b>rebind its decode bridge in place</b> after a same-size warm-sink player
/// swap. Since ADR-0064 Decision 2 the converter owns its own device, so a same-size swap rebinds
/// (ring + compositor imports stay warm) instead of rebuilding — the change that makes a video→video
/// transition over a warm presenter gapless. The GPU mechanics of the rebind require a real D3D11
/// device and are validated on hardware; this fixture pins the decision + telemetry that guarantee a
/// same-size swap records <c>device_change_rebinds</c>, never a <c>device_change_rebuilds</c>.
/// </summary>
public sealed class ConverterActionTests
{
    private const nint DevA = 0x1000;
    private const nint DevB = 0x2000;
    private const nint DevC = 0x3000;

    // The default test resolution; equal cached/frame dims keep the device-focused cases off the
    // resolution-rebuild path (which would otherwise dominate the decision).
    private const int W = 1920;
    private const int H = 1080;

    [Fact]
    public void NoCachedConverter_BuildsFresh_Reuse()
    {
        var r = CompositionInteropVideoView.EvaluateConverterAction(
            hasCached: false, cachedDevice: 0, cachedDeviceLost: false, frameDevice: DevA,
            cachedWidth: 0, cachedHeight: 0, frameWidth: W, frameHeight: H);
        Assert.Equal(CompositionInteropVideoView.ConverterAction.Reuse, r);
    }

    [Fact]
    public void SameDeviceSameSize_Reuses()
    {
        var r = CompositionInteropVideoView.EvaluateConverterAction(
            hasCached: true, cachedDevice: DevA, cachedDeviceLost: false, frameDevice: DevA,
            cachedWidth: W, cachedHeight: H, frameWidth: W, frameHeight: H);
        Assert.Equal(CompositionInteropVideoView.ConverterAction.Reuse, r);
    }

    [Fact]
    public void DifferentDeviceSameSize_WarmSinkSwap_RebindsNotRebuilds()
    {
        // The crux of ADR-0064: a new player feeds the same warm sink on a new decode device, same
        // resolution. The converter rebinds its decode bridge in place — it does NOT rebuild (and
        // does NOT issue cross-device copies against the disposed device, the original freeze).
        var r = CompositionInteropVideoView.EvaluateConverterAction(
            hasCached: true, cachedDevice: DevA, cachedDeviceLost: false, frameDevice: DevB,
            cachedWidth: W, cachedHeight: H, frameWidth: W, frameHeight: H);
        Assert.Equal(CompositionInteropVideoView.ConverterAction.RebindDecodeDevice, r);
    }

    [Fact]
    public void DeviceLost_RebuildsForDeviceLoss_EvenIfSameDeviceAndSize()
    {
        var r = CompositionInteropVideoView.EvaluateConverterAction(
            hasCached: true, cachedDevice: DevA, cachedDeviceLost: true, frameDevice: DevA,
            cachedWidth: W, cachedHeight: H, frameWidth: W, frameHeight: H);
        Assert.Equal(CompositionInteropVideoView.ConverterAction.RebuildForDeviceLoss, r);
    }

    [Fact]
    public void DeviceLost_TakesPriorityOverEverything()
    {
        var r = CompositionInteropVideoView.EvaluateConverterAction(
            hasCached: true, cachedDevice: DevA, cachedDeviceLost: true, frameDevice: DevB,
            cachedWidth: W, cachedHeight: H, frameWidth: 1280, frameHeight: 720);
        Assert.Equal(CompositionInteropVideoView.ConverterAction.RebuildForDeviceLoss, r);
    }

    [Fact]
    public void UnknownFrameDevice_SameSize_NeverActs_DoesNotThrash()
    {
        // device == 0 means "identity unavailable" (chain not walkable) — must not be read as a
        // mismatch, or every frame with missing telemetry would rebind the converter.
        var r = CompositionInteropVideoView.EvaluateConverterAction(
            hasCached: true, cachedDevice: DevA, cachedDeviceLost: false, frameDevice: 0,
            cachedWidth: W, cachedHeight: H, frameWidth: W, frameHeight: H);
        Assert.Equal(CompositionInteropVideoView.ConverterAction.Reuse, r);
    }

    [Fact]
    public void SameDevice_ResolutionChange_Rebuilds()
    {
        // The fixed-size ring + staging textures and the per-frame copy Box cannot be resized by an
        // in-place rebind, so a resolution change must rebuild — even on the same decode device.
        // (Pre-fix this same-size assumption was silently violated; the rebind path made it worse for
        // a different-device swap. The decision now catches both.)
        var r = CompositionInteropVideoView.EvaluateConverterAction(
            hasCached: true, cachedDevice: DevA, cachedDeviceLost: false, frameDevice: DevA,
            cachedWidth: W, cachedHeight: H, frameWidth: 1280, frameHeight: 720);
        Assert.Equal(CompositionInteropVideoView.ConverterAction.RebuildForResolutionChange, r);
    }

    [Fact]
    public void ResolutionChange_TakesPriorityOverDeviceChange()
    {
        // A mixed-resolution playlist swap changes both the device AND the size at once. The size
        // change must win (rebuild), not the device change (rebind) — a rebind would leave a
        // wrong-sized ring presenting the new frames as garbage.
        var r = CompositionInteropVideoView.EvaluateConverterAction(
            hasCached: true, cachedDevice: DevA, cachedDeviceLost: false, frameDevice: DevB,
            cachedWidth: W, cachedHeight: H, frameWidth: 1280, frameHeight: 720);
        Assert.Equal(CompositionInteropVideoView.ConverterAction.RebuildForResolutionChange, r);
    }

    [Fact]
    public void WarmSinkPlaylistSwaps_SameResolution_RebindEachBoundary_NeverRebuild()
    {
        // Simulate a warm presenter playing a 3-item, same-resolution playlist (the
        // signage shape): the sink + view stay warm while the per-item decode device changes at each
        // boundary. Drive the pure decision over the frame device sequence, modelling the converter
        // rebinding in place (its bound device follows each swap). The acceptance: ZERO rebuilds.
        var frameDevices = new nint[] { DevA, DevA, DevA, DevB, DevB, DevC, DevC };

        nint boundDevice = 0;
        var hasConverter = false;
        var rebinds = 0;
        var rebuilds = 0;

        foreach (var frameDevice in frameDevices)
        {
            switch (CompositionInteropVideoView.EvaluateConverterAction(
                hasCached: hasConverter, cachedDevice: boundDevice, cachedDeviceLost: false,
                frameDevice: frameDevice, cachedWidth: W, cachedHeight: H, frameWidth: W, frameHeight: H))
            {
                case CompositionInteropVideoView.ConverterAction.Reuse:
                    break;
                case CompositionInteropVideoView.ConverterAction.RebindDecodeDevice:
                    rebinds++;
                    boundDevice = frameDevice; // rebind in place: the bound device follows the swap
                    break;
                default: // any rebuild (device-loss / resolution) — should not occur here
                    rebuilds++;
                    boundDevice = frameDevice;
                    break;
            }

            if (!hasConverter)
            {
                // First GPU frame lazily builds the converter on the first decode device.
                hasConverter = true;
                boundDevice = frameDevice;
            }
        }

        Assert.Equal(2, rebinds); // DevA->DevB and DevB->DevC
        Assert.Equal(0, rebuilds); // ADR-0064 acceptance: a same-size warm swap never rebuilds
    }

    [Fact]
    public void WarmSinkSwap_RecordsRebindCounter_NeverRebuildCounter()
    {
        // The telemetry half of the acceptance: a warm-sink same-size swap that rebinds in place
        // bumps device_change_rebinds and leaves device_change_rebuilds (the fallback/regression
        // alarm) at zero. Captured off the FrameFlow.Presenter meter with a MeterListener.
        var captured = CaptureMeter(
            () =>
            {
                // Two warm-sink swaps, each rebinding in place (the healthy path the view takes when
                // the converter's TryRebindDecodeDevice succeeds).
                PresenterTeardownMetrics.RecordDeviceChangeRebind();
                PresenterTeardownMetrics.RecordDeviceChangeRebind();
            },
            "frameflow.presenter.device_change_rebinds",
            "frameflow.presenter.device_change_rebuilds");

        Assert.Equal(2, captured["frameflow.presenter.device_change_rebinds"]);
        Assert.Equal(0, captured["frameflow.presenter.device_change_rebuilds"]);
    }

    [Fact]
    public void RebindFallback_RecordsRebuildCounter()
    {
        // The fallback path: when TryRebindDecodeDevice fails on a driver, the view drops + rebuilds
        // and DropGpuConverter(DeviceChangeRebindFailed) records device_change_rebuilds — the
        // regression alarm. This exercises the metric the fallback depends on (it is otherwise the
        // one swap path with no test, since the GPU rebind failure itself needs hardware).
        var captured = CaptureMeter(
            PresenterTeardownMetrics.RecordDeviceChangeRebuild,
            "frameflow.presenter.device_change_rebuilds",
            "frameflow.presenter.device_change_rebinds");

        Assert.Equal(1, captured["frameflow.presenter.device_change_rebuilds"]);
        Assert.Equal(0, captured["frameflow.presenter.device_change_rebinds"]);
    }

    [Fact]
    public void ResolutionChangeRebuild_RecordsResolutionCounter()
    {
        var captured = CaptureMeter(
            PresenterTeardownMetrics.RecordResolutionChangeRebuild,
            "frameflow.presenter.device_resolution_rebuilds");

        Assert.Equal(1, captured["frameflow.presenter.device_resolution_rebuilds"]);
    }

    /// <summary>
    /// Runs <paramref name="action"/> while listening to the named <c>FrameFlow.Presenter</c>
    /// counters, returning the summed delta per instrument (0 for a named instrument never recorded).
    /// Nothing else in the suite records these instruments, so the captured counts are exactly the
    /// action's.
    /// </summary>
    private static Dictionary<string, long> CaptureMeter(Action action, params string[] instrumentNames)
    {
        var wanted = new HashSet<string>(instrumentNames);
        var captured = new Dictionary<string, long>();
        foreach (var name in instrumentNames)
            captured[name] = 0;

        using (var listener = new MeterListener())
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "FrameFlow.Presenter" && wanted.Contains(instrument.Name))
                    l.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>(
                (instrument, measurement, _, _) =>
                {
                    lock (captured)
                        captured[instrument.Name] = captured.GetValueOrDefault(instrument.Name) + measurement;
                });
            listener.Start();
            action();
        }

        return captured;
    }
}
