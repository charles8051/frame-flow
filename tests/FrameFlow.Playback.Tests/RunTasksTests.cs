// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Playback.Core;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Pins what a playback session holds of its run's tasks (#707), its pump, its graph and its
/// lateness-recovery walk: a task is forgotten only once it has ended, so a wait cut short leaves
/// every task still running for the next stop to wait for, and a run may launch only when nothing
/// is held. Pure: a handle stands in for each task.
/// </summary>
public sealed class RunTasksTests
{
    private static readonly Handle Pump = new("pump");
    private static readonly Handle Graph = new("graph");
    private static readonly Handle Walk = new("recovery walk");

    [Fact]
    public void NothingLaunched_HoldsNothing()
    {
        var tasks = RunTasks<Handle>.None;

        Assert.True(tasks.IsEmpty);
        Assert.Empty(tasks.Held);
    }

    [Fact]
    public void ALaunchedRun_HoldsItsPumpAndItsGraph()
    {
        var tasks = RunTasks<Handle>.Launched(Pump, Graph);

        Assert.False(tasks.IsEmpty);
        Assert.Equal([Pump, Graph], tasks.Held);
    }

    [Fact]
    public void AWaitCutShort_WithNeitherTaskEnded_ForgetsNothing()
    {
        var tasks = RunTasks<Handle>.Launched(Pump, Graph).ForgetEnded(Ended());

        Assert.Equal(RunTasks<Handle>.Launched(Pump, Graph), tasks);
        Assert.False(tasks.IsEmpty);
    }

    [Fact]
    public void BothTasksEnded_AreForgotten_SoARunMayLaunch()
    {
        var tasks = RunTasks<Handle>.Launched(Pump, Graph).ForgetEnded(Ended(Pump, Graph));

        Assert.True(tasks.IsEmpty);
        Assert.Empty(tasks.Held);
    }

    [Fact]
    public void APumpThatEndedBeforeTheGraph_IsForgotten_AndTheGraphIsStillHeld()
    {
        // A pump reaches the end of the file long before the graph has delivered what it read.
        var tasks = RunTasks<Handle>.Launched(Pump, Graph).ForgetEnded(Ended(Pump));

        Assert.Equal([Graph], tasks.Held);
        Assert.False(tasks.IsEmpty);
    }

    [Fact]
    public void AGraphThatEndedBeforeThePump_IsForgotten_AndThePumpIsStillHeld()
    {
        var tasks = RunTasks<Handle>.Launched(Pump, Graph).ForgetEnded(Ended(Graph));

        Assert.Equal([Pump], tasks.Held);
        Assert.False(tasks.IsEmpty);
    }

    [Fact]
    public void ForgettingWhatEnded_OnNothingHeld_HoldsNothing()
    {
        var asked = 0;

        var tasks = RunTasks<Handle>.None.ForgetEnded(_ =>
        {
            asked++;
            return false;
        });

        Assert.True(tasks.IsEmpty);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void AScrubOfWaitsCutShort_HoldsTheSameRun_UntilItsTasksEnd()
    {
        // Each superseded seek's wait is cut short while the run unwinds. None of them loses a
        // task or adds one, and the wait that outlasts the unwinding forgets both.
        var tasks = RunTasks<Handle>.Launched(Pump, Graph);
        for (var seek = 0; seek < 5; seek++)
            tasks = tasks.ForgetEnded(Ended());

        Assert.Equal([Pump, Graph], tasks.Held);

        tasks = tasks.ForgetEnded(Ended(Pump));
        Assert.Equal([Graph], tasks.Held);

        tasks = tasks.ForgetEnded(Ended(Pump, Graph));
        Assert.True(tasks.IsEmpty);
    }

    [Fact]
    public void ARunWithLatenessRecovery_HoldsItsWalkToo()
    {
        var tasks = RunTasks<Handle>.Launched(Pump, Graph, Walk);

        Assert.Equal([Pump, Graph, Walk], tasks.Held);
    }

    [Fact]
    public void AWalkStillGoing_AfterThePumpAndTheGraphEnded_IsStillHeld_SoNoRunMayLaunch()
    {
        // The walk ends only when the run is cancelled. A run that reached its end by itself is not
        // over until it has been stopped.
        var tasks = RunTasks<Handle>.Launched(Pump, Graph, Walk).ForgetEnded(Ended(Pump, Graph));

        Assert.Equal([Walk], tasks.Held);
        Assert.False(tasks.IsEmpty);
    }

    [Fact]
    public void AllThreeEnded_AreForgotten()
    {
        var tasks = RunTasks<Handle>.Launched(Pump, Graph, Walk).ForgetEnded(Ended(Pump, Graph, Walk));

        Assert.True(tasks.IsEmpty);
    }

    [Fact]
    public void ALaunch_NeedsBothTasks()
    {
        Assert.Throws<ArgumentNullException>(() => RunTasks<Handle>.Launched(null!, Graph));
        Assert.Throws<ArgumentNullException>(() => RunTasks<Handle>.Launched(Pump, null!));
    }

    private static Func<Handle, bool> Ended(params Handle[] ended) => task => ended.Contains(task);

    private sealed record Handle(string Name);
}
