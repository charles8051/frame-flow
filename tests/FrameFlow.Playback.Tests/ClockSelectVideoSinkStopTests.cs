// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Tests.Shared;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// The pacer's stop does not wait for the thread that cancels it to finish its cancellation
/// callbacks (#785). Its dispose cancels on the disposing thread, which runs the link callbacks
/// from the pacer's shutdown down to the clock's sleep, one inside the other. The sleep wakes on a
/// pool thread while that chain is still running, and each linked source the woken loop disposes on
/// its way out used to wait for the canceller to leave that source's link. The runtime's wait falls
/// back to a one-millisecond sleep, which on Windows lasts a system timer tick. Here the canceller
/// is held in the chain by the clock's timer dispose until the test lets it go, so the loop either
/// ends while it is held or not at all.
/// </summary>
public sealed partial class ClockSelectVideoSinkTests
{
    private static readonly TimeSpan StopBound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AWallClockWait_EndsOnItsCancel_WhileTheCancelIsStillRunningItsCallbacks()
    {
        var time = new HeldTimerDisposeTimeProvider();
        await using var clock = new WallClockSource(time);
        using var caller = new CancellationTokenSource();

        // The clock stands at zero, so the wait sleeps in slices until it is cancelled.
        var armed = time.NextTimerArmed;
        var wait = clock.WaitUntilAsync(TimeSpan.FromSeconds(10), caller.Token).AsTask();
        await armed.WaitAsync(StopBound);

        time.HoldNextDispose();
        var cancelling = Task.Run(caller.Cancel);
        try
        {
            await time.DisposeHeld.Task.WaitAsync(StopBound);

            // The bound only turns a wait held by the canceller into a failure.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(StopBound));
        }
        finally
        {
            time.Release();
        }

        await cancelling.WaitAsync(StopBound);
    }

    [Fact]
    public async Task ThePacersStop_EndsTheDeliveryLoop_WhileTheCancelIsStillRunningItsCallbacks()
    {
        var time = new HeldTimerDisposeTimeProvider();
        await using var clock = new WallClockSource(time);
        var sink = new ScriptedSink();
        var pacer = new ClockSelectVideoSink(sink, clock, capacity: 4, timeProvider: new FakeTimeProvider());

        // Due long after the clock, which stands at zero, so the loop sleeps in the clock's wait
        // for it, as a paused pacer does, and the frame is still in the ring when the loop ends.
        var frame = new DisposeSignallingFrame(TimeSpan.FromSeconds(10));
        var armed = time.NextTimerArmed;
        await pacer.PresentAsync(frame, default);
        await armed.WaitAsync(StopBound);

        time.HoldNextDispose();
        var disposing = Task.Run(() => pacer.DisposeAsync().AsTask());
        try
        {
            await time.DisposeHeld.Task.WaitAsync(StopBound);

            // The loop's teardown disposes the frame once the loop has left its wait and every
            // source linked around it. The bound only turns a loop held by the canceller into a
            // failure.
            await frame.Disposed.WaitAsync(StopBound);
        }
        finally
        {
            time.Release();
        }

        await disposing.WaitAsync(StopBound);
        Assert.Equal(0, sink.Calls);
    }

    /// <summary>A frame that signals its dispose.</summary>
    private sealed class DisposeSignallingFrame(TimeSpan pts) : IVideoFrame
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Disposed => _disposed.Task;

        public int Width => 320;
        public int Height => 240;
        public TimeSpan Pts => pts;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(33);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() => this;

        public void Dispose() => _disposed.TrySetResult();

        public CpuFrameData? AsCpu() => null;

        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }
}
