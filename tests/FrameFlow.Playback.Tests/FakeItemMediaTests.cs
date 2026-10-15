// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Playback.Core;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// The rig's own contract (#750): <see cref="FakeItemMedia"/> sets a piece of work up from its
/// hooks before the work can be seen to start, so a rig test that waits for a start and then
/// changes a hook leaves the work it saw as it was started.
/// </summary>
/// <remarks>
/// Each test changes a hook in <see cref="FakeItemMedia.OnWorkSeen"/>, on the work's own thread as
/// soon as the work can be seen: the earliest any test can act on a start, and where a test that
/// waits for it on another thread acts if the work's thread is preempted there. The work is started
/// on the test's thread, so the order is fixed.
/// </remarks>
public sealed class FakeItemMediaTests
{
    private static readonly TimeSpan Bound = ItemRuntimeRig.Bound;

    [Fact]
    public async Task ACancellationCallback_IsRegisteredBeforeItsWorkCanBeSeenToStart()
    {
        // As SubstrateSessionRigTests' cancellation callback test does once it has seen the pump start.
        var media = new FakeItemMedia(MediaShape.AudioAndVideo, new RigLog()) { ThrowOnCancel = ItemWork.Pump };
        media.OnWorkSeen = _ => media.ThrowOnCancel = ItemWork.None;
        using var run = new CancellationTokenSource();
        var pump = media.RunPumpAsync(run.Token);

        var thrown = Assert.Throws<AggregateException>(run.Cancel);

        Assert.Same(media.CancelFault, Assert.Single(thrown.InnerExceptions));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.WaitAsync(Bound));
    }

    [Fact]
    public async Task AFaultOnStart_IsSetBeforeItsWorkCanBeSeenToStart()
    {
        var media = new FakeItemMedia(MediaShape.AudioAndVideo, new RigLog()) { FaultOnStart = ItemWork.Pump };
        media.OnWorkSeen = _ => media.FaultOnStart = ItemWork.None;
        using var run = new CancellationTokenSource();
        var pump = media.RunPumpAsync(run.Token);

        // Cancelled, the pump ends at once either way: with the start's fault if it was set up to
        // fault, as cancelled if not.
        run.Cancel();

        Assert.Same(media.StartFault, await Assert.ThrowsAsync<InvalidOperationException>(() => pump.WaitAsync(Bound)));
    }

    [Fact]
    public async Task AGraphsFirstFrame_IsDecidedBeforeTheGraphCanBeSeenToStart()
    {
        var media = new FakeItemMedia(MediaShape.AudioAndVideo, new RigLog()) { FirstFrameOnGraphStart = false };
        media.OnWorkSeen = _ => media.FirstFrameOnGraphStart = true;
        var graph = media.RunGraphAsync(CancellationToken.None);

        // The graph's start decodes the first frame before it returns, if it does at all.
        var decoded = media.FirstFrameDecoded!.IsCompleted;
        media.ReleaseAll();
        await graph.WaitAsync(Bound);

        Assert.False(decoded, "The graph decoded its first frame at once, though it started with that turned off.");
    }
}
