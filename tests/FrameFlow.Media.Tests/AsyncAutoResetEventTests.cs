using FrameFlow.Media;
using Microsoft.Extensions.Time.Testing;

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
/// <para>
/// Where a test is about the timeout — that a wait gives up, and not before it should — the
/// event runs on a <see cref="FakeTimeProvider"/> and the test advances it. They used to time a
/// real 50 ms wait with a <c>Stopwatch</c> and allow 20 ms of slack, which is an assertion
/// about the scheduler (ADR-0072). <see cref="AsyncAutoResetEvent.WaitAsync"/> registers its
/// waiter and parks before it returns, so a pending task observed straight after the call is
/// a parked waiter, and only a <see cref="AsyncAutoResetEvent.Set"/>, the timeout or
/// cancellation can complete it.
/// </para>
/// <para>
/// "Not before its timeout" is asserted by result, never by <c>IsCompleted</c> after an
/// advance. The timer fires inside <see cref="FakeTimeProvider.Advance"/>, but the wait's own
/// completion runs on a continuation afterwards, so a wait that timed out early can still read
/// as pending for a moment. What is settled synchronously is which of the two won: a
/// <see cref="AsyncAutoResetEvent.Set"/> one tick short of the timeout releases a wait that is
/// still inside it, and the same call against one that already gave up returns false.
/// Measured: with the timeout halved, only the test built that way failed.
/// </para>
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
        // the async conversion removes). With all but the last tick of its timeout
        // gone, a Set() must still release it: a wait that had already given up
        // would return false here instead. This is also the test that pins "not
        // before its timeout" — see the class remarks for why it is done this way.
        var time = new FakeTimeProvider();
        var signal = new AsyncAutoResetEvent(time);
        var waitTask = signal.WaitAsync(GenerousTimeout, CancellationToken.None);

        time.Advance(GenerousTimeout - TimeSpan.FromTicks(1));
        signal.Set();

        Assert.True(
            await waitTask.WaitAsync(GenerousTimeout),
            "Awaiter must remain blocked until a buffer recycles, however much of its timeout has passed."
        );
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
        var time = new FakeTimeProvider();
        var signal = new AsyncAutoResetEvent(time);
        signal.Set();

        // First wait consumes the permit immediately.
        Assert.True(await signal.WaitAsync(GenerousTimeout, CancellationToken.None));

        // Second wait must park — a latched permit returns synchronously, so pending here
        // means there was none — and then time out: returns false, no throw.
        var second = signal.WaitAsync(ShortTimeout, CancellationToken.None);
        Assert.False(second.IsCompleted, "A consumed permit must not satisfy a subsequent wait.");

        time.Advance(ShortTimeout);
        Assert.False(
            await second.WaitAsync(GenerousTimeout),
            "A consumed permit must not satisfy a subsequent wait."
        );
    }

    // ── Timeout self-heal: a missed signal can never deadlock the sink ──────────

    [Fact]
    public async Task Wait_TimesOut_WhenNoSignalArrives()
    {
        // The device-never-drains case: if no buffer ever recycles, WaitAsync must
        // still return (false) after the slice so FlushStagingBufferAsync re-polls
        // source state (catching a Pause/Stop) instead of hanging forever.
        var time = new FakeTimeProvider();
        var signal = new AsyncAutoResetEvent(time);

        var wait = signal.WaitAsync(ShortTimeout, CancellationToken.None);

        time.Advance(ShortTimeout);
        Assert.False(
            await wait.WaitAsync(GenerousTimeout),
            "With no Set(), the wait must report a timeout, not a signal."
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
        //
        // The wait is started on a pool thread and handed back once WaitAsync has returned, which
        // is once it has parked. So the Set() below goes through the release path rather than the
        // latch — the thing this test is for — where it used to sleep 50 ms and hope. The fake
        // provider is never advanced, so nothing but that Set() can complete the wait.
        var time = new FakeTimeProvider();
        var signal = new AsyncAutoResetEvent(time);
        var started = new TaskCompletionSource<Task<bool>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _ = Task.Run(() => started.SetResult(signal.WaitAsync(GenerousTimeout, CancellationToken.None)));
        var waitTask = await started.Task.WaitAsync(GenerousTimeout);

        Assert.False(waitTask.IsCompleted, "The waiter should be parked, not holding a latched permit.");
        signal.Set();

        Assert.True(await waitTask.WaitAsync(GenerousTimeout));
    }
}
