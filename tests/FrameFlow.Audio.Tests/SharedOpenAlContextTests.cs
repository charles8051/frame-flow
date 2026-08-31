using FrameFlow.Audio.OpenAL;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// Structural tests for the shared OpenAL device/context (ADR-0058). Unlike the
/// behavioural clock tests in <see cref="OpenAlAudioSinkMultiInstanceTests"/>,
/// these assert on the refcount and device-open accounting directly, so given a
/// device they are fully deterministic — no playback timing involved. They prove
/// the mechanism the fix relies on: many sinks, one device, one context, made
/// current once.
/// </summary>
/// <remarks>
/// Requires a real OpenAL device (the counters only advance once a device opens),
/// so gated behind <see cref="RequiresAudioDeviceFactAttribute"/>. Serialised via
/// the "OpenAL device" collection so the process-global counters aren't perturbed
/// by other device-activating test classes running in parallel.
/// </remarks>
[Collection("OpenAL device")]
public sealed class SharedOpenAlContextTests : IClassFixture<FfmpegBootstrapFixture>
{
    [RequiresAudioDeviceFact]
    public async Task TwoSinks_ShareOneDevice_WithRefcountedTeardown()
    {
        var opensAtStart = SharedOpenAlContext.DeviceOpensTotal;
        var leasesAtStart = SharedOpenAlContext.CurrentLeaseCount;

        var sinkA = new OpenAlAudioSink();
        var sinkB = new OpenAlAudioSink();

        await sinkA.ActivateAsync();

        // No audio device on this box: the shared context never came up. Nothing
        // structural to assert — degrade to a pass (matches the behavioural tests).
        if (!SharedOpenAlContext.IsContextLive)
        {
            await sinkA.DisposeAsync();
            await sinkB.DisposeAsync();
            return;
        }

        var opensAfterA = SharedOpenAlContext.DeviceOpensTotal;
        Assert.Equal(leasesAtStart + 1, SharedOpenAlContext.CurrentLeaseCount);

        await sinkB.ActivateAsync();

        // The load-bearing assertion: a SECOND sink opens NO new device/context.
        // Before ADR-0058 it opened its own device + context and clobbered the
        // process-global current context.
        Assert.Equal(opensAfterA, SharedOpenAlContext.DeviceOpensTotal);
        Assert.Equal(leasesAtStart + 2, SharedOpenAlContext.CurrentLeaseCount);

        // Disposing one sink keeps the shared context alive for the other.
        await sinkB.DisposeAsync();
        Assert.True(SharedOpenAlContext.IsContextLive);
        Assert.Equal(leasesAtStart + 1, SharedOpenAlContext.CurrentLeaseCount);

        // Disposing the last holder tears the shared device/context down.
        await sinkA.DisposeAsync();
        Assert.Equal(leasesAtStart, SharedOpenAlContext.CurrentLeaseCount);
        if (leasesAtStart == 0)
            Assert.False(SharedOpenAlContext.IsContextLive);

        // Total device opens advanced by at most one across both sinks' lives.
        Assert.True(
            SharedOpenAlContext.DeviceOpensTotal - opensAtStart <= 1,
            $"Two concurrent sinks opened {SharedOpenAlContext.DeviceOpensTotal - opensAtStart} "
                + "devices; expected at most 1 (the shared device)."
        );
    }

    [RequiresAudioDeviceFact]
    public async Task Reactivation_DoesNotAcquireASecondLease()
    {
        // The lease is acquired once at first activation and released once at
        // disposal — NOT per Activate/Deactivate cycle. Loop restart goes
        // Deactivate -> Activate many times; each must reuse the one lease.
        await using var sink = new OpenAlAudioSink();

        var leasesBefore = SharedOpenAlContext.CurrentLeaseCount;
        await sink.ActivateAsync();

        if (!SharedOpenAlContext.IsContextLive)
            return; // no device

        var leasesActive = SharedOpenAlContext.CurrentLeaseCount;
        Assert.Equal(leasesBefore + 1, leasesActive);

        await sink.DeactivateAsync();
        Assert.Equal(
            leasesActive,
            SharedOpenAlContext.CurrentLeaseCount
        ); // deactivate keeps the device/context lease (it's a state op, not ownership)

        await sink.ActivateAsync();
        Assert.Equal(
            leasesActive,
            SharedOpenAlContext.CurrentLeaseCount
        ); // reactivate reuses the lease, does not double-acquire
    }

    [RequiresAudioDeviceFact]
    public async Task DisposeWithoutActivate_HoldsNoLease()
    {
        // A sink that never activated never acquired a lease; disposing it must
        // not touch the shared context refcount.
        var leasesBefore = SharedOpenAlContext.CurrentLeaseCount;

        var sink = new OpenAlAudioSink();
        await sink.DisposeAsync();

        Assert.Equal(leasesBefore, SharedOpenAlContext.CurrentLeaseCount);
    }
}
