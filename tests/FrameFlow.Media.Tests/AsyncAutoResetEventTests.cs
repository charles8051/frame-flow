using System.Diagnostics;
using FrameFlow.Media;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Tests for <see cref="AsyncAutoResetEvent"/>, the latched-permit handoff that replaced the
/// OpenAL sink's <c>Thread.Sleep(1)</c> backpressure spin (perf survey A3).
/// </summary>
/// <remarks>
/// Deterministic and device-independent: they model the stalled-then-drained sequence
/// <c>OpenAlAudioSink.FlushStagingBufferAsync</c> depends on without needing a real OpenAL
/// device, which is what the end-to-end backpressure test gates behind
/// <c>RequiresAudioDeviceFact</c>. They moved here with the primitive; the sequence they pin
/// is the one any buffer-return backpressure loop needs, not an OpenAL-specific one.
/// </remarks>
public sealed class AsyncAutoResetEventTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan GenerousTimeout = TimeSpan.FromSeconds(5);

    // ── The load-bearing case: a stalled awaiter is released by a buffer recycle ──

    [Fact]
    public async Task StalledWaiter_IsReleased_WhenBufferRecycles()
    {
        // Models: all OpenAL buffers in flight → FlushStagingBufferAsync parks on
        // WaitAsync → later RecycleProcessedBuffers returns a buffer and calls
        // Set() → the awaiter must wake and report a real signal (true), not a
        // timeout. This is the survey's acceptance criterion: "a stalled-then-
        // drained buffer recycle releases the awaiter."
        var signal = new AsyncAutoResetEvent();

        // Park the awaiter first (no latched permit yet), with a generous timeout
        // so a pass can only come from Set(), never from the timeout elapsing.
        var waitTask = signal.WaitAsync(GenerousTimeout, CancellationToken.None);
        Assert.False(waitTask.IsCompleted, "Awaiter should block while no buffer is available.");

        // The "buffer recycled" event.
        signal.Set();

        // Released promptly, and explicitly because of the signal.
        var releasedBySignal = await waitTask.WaitAsync(GenerousTimeout);
        Assert.True(releasedBySignal, "WaitAsync must report it was released by a signal, not a timeout.");
    }

    [Fact]
    public async Task StalledWaiter_StaysBlocked_UntilSignal()
    {
        // Before the recycle, the awaiter must not complete on its own — otherwise
        // the sink would busy-loop re-polling an empty queue (the exact CPU waste
        // the async conversion removes). Verify it is still pending after a real
        // delay, then released by Set().
        var signal = new AsyncAutoResetEvent();
        var waitTask = signal.WaitAsync(GenerousTimeout, CancellationToken.None);

        await Task.Delay(100);
        Assert.False(waitTask.IsCompleted, "Awaiter must remain blocked until a buffer recycles.");

        signal.Set();
        Assert.True(await waitTask.WaitAsync(GenerousTimeout));
    }

    // ── Missed-wakeup safety: Set before Wait must not be lost ──────────────────

    [Fact]
    public async Task SetBeforeWait_IsLatched_NextWaitReturnsImmediately()
    {
        // A buffer can recycle in the tiny window between the sink dequeuing the
        // last free buffer and parking on WaitAsync. The latched permit ensures
        // that Set() is not lost — the next WaitAsync consumes it and returns
        // synchronously, so the sink re-polls the (now non-empty) queue instead of
        // sleeping out the full timeout. A lost signal here would add up-to-one-
        // slice of latency per occurrence; never a deadlock, but worth pinning.
        var signal = new AsyncAutoResetEvent();

        signal.Set(); // recycle raced ahead of the wait

        var releasedBySignal = await signal.WaitAsync(GenerousTimeout, CancellationToken.None);
        Assert.True(releasedBySignal, "A Set() with no waiter parked must latch a permit for the next wait.");
    }

    [Fact]
    public async Task LatchedPermit_IsConsumed_OnlyOnce()
    {
        // One recycle == one permit. After a latched Set() is consumed by a wait,
        // the following wait must block again (until the next recycle) rather than
        // spuriously returning. Auto-reset semantics: the permit does not persist.
        var signal = new AsyncAutoResetEvent();
        signal.Set();

        // First wait consumes the permit immediately.
        Assert.True(await signal.WaitAsync(GenerousTimeout, CancellationToken.None));

        // Second wait must time out (no further Set()) — returns false, no throw.
        var sw = Stopwatch.StartNew();
        var releasedBySignal = await signal.WaitAsync(ShortTimeout, CancellationToken.None);
        sw.Stop();

        Assert.False(releasedBySignal, "A consumed permit must not satisfy a subsequent wait.");
        Assert.True(
            sw.Elapsed >= ShortTimeout - TimeSpan.FromMilliseconds(20),
            $"Second wait returned after {sw.ElapsedMilliseconds}ms; expected to block ~{ShortTimeout.TotalMilliseconds}ms."
        );
    }

    // ── Timeout self-heal: a missed signal can never deadlock the sink ──────────

    [Fact]
    public async Task Wait_TimesOut_WhenNoSignalArrives()
    {
        // The device-never-drains case: if no buffer ever recycles, WaitAsync must
        // still return (false) after the slice so FlushStagingBufferAsync re-polls
        // source state (catching a Pause/Stop) instead of hanging forever.
        var signal = new AsyncAutoResetEvent();

        var sw = Stopwatch.StartNew();
        var releasedBySignal = await signal.WaitAsync(ShortTimeout, CancellationToken.None);
        sw.Stop();

        Assert.False(releasedBySignal, "With no Set(), the wait must report a timeout, not a signal.");
        Assert.True(
            sw.Elapsed >= ShortTimeout - TimeSpan.FromMilliseconds(20),
            $"Wait returned after {sw.ElapsedMilliseconds}ms; expected to block ~{ShortTimeout.TotalMilliseconds}ms before timing out."
        );
    }

    // ── Cancellation: dispose / shutdown breaks the wait promptly ───────────────

    [Fact]
    public async Task Wait_Throws_WhenCancelledWhileParked()
    {
        // FlushStagingBufferAsync links the wait to the sink's shutdown token, so a
        // DisposeAsync mid-backpressure must surface as cancellation (which the loop
        // catches to abandon the flush). A timed-out-vs-cancelled distinction matters:
        // cancellation throws, timeout returns false.
        var signal = new AsyncAutoResetEvent();
        using var cts = new CancellationTokenSource();

        var waitTask = signal.WaitAsync(GenerousTimeout, cts.Token);
        Assert.False(waitTask.IsCompleted);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitTask);
    }

    [Fact]
    public async Task Wait_Throws_WhenTokenAlreadyCancelled()
    {
        var signal = new AsyncAutoResetEvent();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await signal.WaitAsync(GenerousTimeout, cts.Token)
        );
    }

    // ── Repeatability: the signal services many recycle/wait cycles in sequence ──

    [Fact]
    public async Task SignalAndWait_RepeatedCycles_EachReleasesItsAwaiter()
    {
        // The backpressure loop can wait → wake → wait → wake many times within one
        // sustained stall. Each Set() must release exactly the awaiter that followed
        // it, with the gate re-arming cleanly between cycles.
        var signal = new AsyncAutoResetEvent();

        for (int i = 0; i < 50; i++)
        {
            var waitTask = signal.WaitAsync(GenerousTimeout, CancellationToken.None);
            signal.Set();
            Assert.True(
                await waitTask.WaitAsync(GenerousTimeout),
                $"Cycle {i}: awaiter was not released by its Set()."
            );
        }
    }

    [Fact]
    public async Task DrainSignal_ReleasesAwaiter_AcrossThreadBoundary()
    {
        // Mirrors the production topology: the awaiter is the audio worker parked in
        // FlushStagingBufferAsync; Set() is fired from a *different* path
        // (RecycleProcessedBuffers, invoked under _stateLock by another call). Prove
        // the cross-thread set→release works and the result is observed.
        var signal = new AsyncAutoResetEvent();
        var waitTask = Task.Run(() => signal.WaitAsync(GenerousTimeout, CancellationToken.None));

        // Give the waiter time to park, then signal from this thread.
        await Task.Delay(50);
        signal.Set();

        Assert.True(await waitTask.WaitAsync(GenerousTimeout));
    }
}
