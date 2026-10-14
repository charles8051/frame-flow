// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;
using FrameFlow.Media.Core;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Pins ADR-0086 decision 1 on the scope an owner starts its work through: work is held until it
/// ends and only its end lets it go; a stop cancels, then waits for every piece of work; a caller
/// that abandons its wait leaves the work held for the next stop; the faults come back, but not a
/// cancellation the work was asked for; and a stopped scope starts nothing. <see cref="WorkLedger"/>
/// holds the rules and is tested as a value; the scope tests pin the shell around it, each waiting
/// on the work's own signals or on <see cref="WorkScope.WorkEnded"/>.
/// </summary>
public sealed class WorkScopeTests
{
    // ─── The core ───────────────────────────────────────────────────

    [Fact]
    public void Ledger_HoldsEachStart_UnderTheNextNumber_InTheOrderItStarted()
    {
        var (first, pump) = WorkLedger.Empty.Start("pump");
        var (second, graph) = first.Start("graph");

        Assert.Equal(1, pump);
        Assert.Equal(2, graph);
        Assert.Equal(["pump", "graph"], second.Pending);
        Assert.False(second.IsIdle);
    }

    [Fact]
    public void Ledger_OnlyTheEndLetsWorkGo()
    {
        var (held, pump) = WorkLedger.Empty.Start("pump");
        var (stopped, _) = held.Stop();

        Assert.Equal(["pump"], stopped.Pending);

        var ended = stopped.End(pump!.Value, error: null, cancellationRequested: true);
        Assert.True(ended.IsIdle);
        Assert.Empty(ended.Faults);
    }

    [Fact]
    public void Ledger_OnlyTheFirstStopCancels()
    {
        var (once, cancel) = WorkLedger.Empty.Start("pump").Next.Stop();
        var (twice, cancelAgain) = once.Stop();

        Assert.True(cancel);
        Assert.False(cancelAgain);
        Assert.True(twice.Stopped);
        Assert.Equal(["pump"], twice.Pending);
    }

    [Fact]
    public void Ledger_AStoppedLedger_RefusesAStart_AndHoldsNothingMore()
    {
        var stopped = WorkLedger.Empty.Stop().Next;

        var (next, work) = stopped.Start("late");

        Assert.Null(work);
        Assert.Same(stopped, next);
        Assert.True(next.IsIdle);
    }

    [Fact]
    public void Ledger_TheEndOfWorkItDoesNotHold_ChangesNothing()
    {
        var held = WorkLedger.Empty.Start("pump").Next;

        Assert.Same(held, held.End(7, new InvalidOperationException(), cancellationRequested: false));
    }

    [Fact]
    public void Ledger_AFault_IsKeptUnderTheWorksName_InTheOrderTheWorkEnded()
    {
        var (a, pump) = WorkLedger.Empty.Start("pump");
        var (b, graph) = a.Start("graph");
        var pumpFault = new InvalidOperationException("pump");
        var graphFault = new InvalidOperationException("graph");

        var ended = b.End(graph!.Value, graphFault, false).End(pump!.Value, pumpFault, false);

        Assert.Equal([new WorkFault("graph", graphFault), new WorkFault("pump", pumpFault)], ended.Faults);
    }

    public static TheoryData<Exception?, bool, bool> Outcomes =>
        new()
        {
            // error, cancellation requested, is a fault
            { null, false, false },
            { null, true, false },
            { new OperationCanceledException(), true, false },
            { new TaskCanceledException(), true, false },
            { new OperationCanceledException(), false, true },
            { new InvalidOperationException(), true, true },
            { new InvalidOperationException(), false, true },
        };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void Ledger_ACancellationTheWorkWasAskedFor_IsTheOnlyEndThatIsNotAFault(
        Exception? error,
        bool cancellationRequested,
        bool isFault
    )
    {
        Assert.Equal(isFault ? error : null, WorkLedger.FaultOf(error, cancellationRequested));
    }

    // ─── The scope ──────────────────────────────────────────────────

    [Fact]
    public async Task StartedWork_IsHeldUntilItEnds()
    {
        var scope = new WorkScope();
        var ended = new WorkEnds(scope);
        var release = Signal();

        Assert.True(scope.TryStart("pump", _ => release.Task));
        Assert.Equal(["pump"], scope.Pending);

        release.SetResult();
        await ended.For("pump");

        Assert.Empty(scope.Pending);
    }

    [Fact]
    public async Task Stop_CancelsTheWork_AndWaitsForItToEnd()
    {
        var scope = new WorkScope();
        var cancelled = Signal();
        var release = Signal();
        scope.TryStart(
            "pump",
            async ct =>
            {
                using var _ = ct.Register(() => cancelled.TrySetResult());
                await release.Task;
            }
        );

        var stopping = scope.StopAsync();
        await cancelled.Task.WaitAsync(Bound);

        // The work cannot end before it is released, so neither can the stop.
        Assert.Equal(["pump"], scope.Pending);
        Assert.False(stopping.IsCompleted);

        release.SetResult();
        Assert.Empty(await stopping.WaitAsync(Bound));
        Assert.Empty(scope.Pending);
    }

    [Fact]
    public async Task Stop_WaitsForWorkThatIgnoresItsToken()
    {
        var scope = new WorkScope();
        var release = Signal();
        scope.TryStart("node", _ => release.Task);

        var stopping = scope.StopAsync();

        Assert.False(stopping.IsCompleted);
        Assert.Equal(["node"], scope.Pending);

        release.SetResult();
        await stopping.WaitAsync(Bound);
        Assert.Empty(scope.Pending);
    }

    [Fact]
    public async Task ACallerThatAbandonsItsWait_LeavesTheWorkHeld_AndTheNextStopWaitsForIt()
    {
        var scope = new WorkScope();
        var release = Signal();
        scope.TryStart(
            "pump",
            async _ =>
            {
                await release.Task;
                throw new InvalidOperationException("ended after the first stop gave up");
            }
        );
        using var impatient = new CancellationTokenSource();

        var first = scope.StopAsync(impatient.Token);
        impatient.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.True(scope.IsStopped);
        Assert.Equal(["pump"], scope.Pending);

        var second = scope.StopAsync();
        Assert.False(second.IsCompleted);

        release.SetResult();
        var fault = Assert.Single(await second.WaitAsync(Bound));
        Assert.Equal("pump", fault.Work);
        Assert.Equal("ended after the first stop gave up", fault.Error.Message);
        Assert.Empty(scope.Pending);
    }

    [Fact]
    public async Task Stop_ReturnsTheFaults_ButNotACancellationTheWorkWasAskedFor()
    {
        var scope = new WorkScope();
        var ended = new WorkEnds(scope);
        using var linked = new CancellationTokenSource();

        // Two that end before the stop, in this order: a fault, and a cancellation nobody asked for.
        scope.TryStart("faulted", _ => throw new InvalidOperationException("faulted"));
        await ended.For("faulted");
        scope.TryStart("cancelled unasked", _ => throw new OperationCanceledException("unasked"));
        await ended.For("cancelled unasked");

        // One cancelled by the token it was started with, and one by the stop.
        scope.TryStart("cancelled by its token", UntilCancelled, linked.Token);
        await linked.CancelAsync();
        await ended.For("cancelled by its token");
        scope.TryStart("cancelled by the stop", UntilCancelled);

        var faults = await scope.StopAsync().WaitAsync(Bound);

        Assert.Equal(["faulted", "cancelled unasked"], faults.Select(f => f.Work));
        Assert.IsType<InvalidOperationException>(faults[0].Error);
        Assert.IsType<OperationCanceledException>(faults[1].Error);
    }

    [Fact]
    public async Task EveryStop_ReturnsEveryFault()
    {
        var scope = new WorkScope();
        scope.TryStart("faulted", _ => throw new InvalidOperationException("faulted"));

        var first = await scope.StopAsync().WaitAsync(Bound);
        var second = await scope.StopAsync().WaitAsync(Bound);

        Assert.Equal("faulted", Assert.Single(first).Work);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task AStoppedScope_StartsNothing()
    {
        var scope = new WorkScope();
        await scope.StopAsync().WaitAsync(Bound);
        var called = false;

        var started = scope.TryStart(
            "late",
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            }
        );

        Assert.False(started);
        Assert.False(called);
        Assert.Empty(scope.Pending);
    }

    [Fact]
    public async Task WorkWhoseTokenIsCancelledBeforeItStarts_StillEnds_AndIsLetGo()
    {
        var scope = new WorkScope();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        scope.TryStart("pump", ct => Task.FromCanceled(ct), cancelled.Token);

        // The bound only turns a piece of work that is never let go into a failure.
        Assert.Empty(await scope.StopAsync().WaitAsync(Bound));
        Assert.Empty(scope.Pending);
    }

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// A signal for each piece of work's end, by name, completed once the scope has let it go.
    /// </summary>
    private sealed class WorkEnds
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _ends = new();

        public WorkEnds(WorkScope scope) => scope.WorkEnded = name => Of(name).TrySetResult();

        /// <summary>Completes once the work named <paramref name="name"/> has been let go.</summary>
        public Task For(string name) => Of(name).Task.WaitAsync(Bound);

        private TaskCompletionSource Of(string name) => _ends.GetOrAdd(name, _ => Signal());
    }

    /// <summary>Work that ends only when its token is cancelled, with that cancellation.</summary>
    private static async Task UntilCancelled(CancellationToken ct)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var _ = ct.Register(() => cancelled.TrySetCanceled(ct));
        await cancelled.Task;
    }
}
