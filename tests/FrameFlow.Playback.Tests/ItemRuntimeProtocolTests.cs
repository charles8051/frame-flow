// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Playback.Core;
using Action = FrameFlow.Playback.Core.ItemRuntimeAction;
using Input = FrameFlow.Playback.Core.ItemRuntimeInput;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// <see cref="ItemRuntimeProtocol"/>, the item runtime's core (ADR-0086, decision 2), through named
/// transcripts: the sequences the ADR and #707 and #714 describe, played as the shell would feed
/// them. <c>ItemRuntimeProtocolWalkTests</c> checks the same rules over every reachable state.
/// </summary>
public sealed class ItemRuntimeProtocolTests
{
    private static readonly TimeSpan At1 = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan At3 = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan At6 = TimeSpan.FromSeconds(6);

    private static readonly Exception Boom = new InvalidOperationException("boom");

    private const ItemWork PumpAndGraph = ItemWork.Pump | ItemWork.Graph;

    // ── A seek ──────────────────────────────────────────────────────────────

    [Fact]
    public void ASeek_StopsTheRun_WaitsForItsWork_ThenSeeksTheDemuxer_AndPlaysOnFromTheTarget()
    {
        var t = ItemRuntimeTranscript.Playing();

        var seek = t.Seek(At3);
        Assert.Empty(t.Actions); // the audio first
        Assert.Equal(new Action.PauseAudio(), t.Last.Awaited);

        t.Ok();
        Assert.Equal([new Action.CloseGates(), new Action.CancelRun(0)], t.Actions);
        Assert.Null(t.Last.Awaited); // waits for the run's work
        Assert.Equal(ItemRunStatus.Stopping, t.State.Status);
        Assert.Equal(0, t.State.Run);

        t.Ends(ItemWork.Pump);
        Assert.Empty(t.Actions);
        Assert.Null(t.Last.Awaited);
        Assert.Equal(0, t.State.Run); // the graph has not ended

        t.Ends(ItemWork.Graph);
        Assert.Equal(1, t.State.Run);
        Assert.Equal([new Action.FlushPacer()], t.Actions);
        Assert.Equal(new Action.DeactivateAudio(), t.Last.Awaited);

        Assert.Equal(new Action.SeekDemux(At3), t.Ok().Awaited);

        t.Ok();
        Assert.Equal([new Action.ResetDecoders(), new Action.SeatClocks(At3, Shared: true)], t.Actions);
        Assert.Equal(new Action.ActivateAudio(), t.Last.Awaited);

        Assert.Equal(new Action.StartRun(1, PumpAndGraph, ItemGraphPolicy.Rebuild, At3, null, HoldForSettle: true), t.Ok().Awaited);

        t.Ok();
        Assert.Equal([new Action.OpenGates()], t.Actions); // over a sink the activation left playing
        Assert.Equal(new Action.ResumeAudio(), t.Last.Awaited);

        Assert.Equal(new Action.Settle(), t.Ok().Awaited);

        t.Ok();
        Assert.Equal([new Action.CompleteCommand(seek, ItemCommandResult.Ok)], t.Actions);
        Assert.Equal(ItemRunStatus.Running, t.State.Status);
        Assert.Equal(PumpAndGraph, t.State.Pending);
    }

    [Fact]
    public void ASeekWhilePaused_Holds_AndThePlayAfterItStartsTheRunFromTheTarget()
    {
        var t = ItemRuntimeTranscript.Playing();
        t.Pause();
        t.OkUntilIdle();

        var seek = t.Seek(At3, clockPaused: true);
        t.OkUntilIdle(); // the audio sink pauses, and the run is stopped
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        Assert.Equal(new Action.DeactivateAudio(), t.Last.Awaited);
        var mark = t.Mark();
        t.OkUntilIdle();

        // Positioned and held: the audio sink is ready, paused, and no run starts.
        Assert.Equal(
            [
                new Action.SeekDemux(At3),
                new Action.ResetDecoders(),
                new Action.SeatClocks(At3, Shared: true),
                new Action.ActivateAudio(),
                new Action.PauseAudio(),
                new Action.CompleteCommand(seek, ItemCommandResult.Ok),
            ],
            t.Since(mark)
        );
        Assert.Equal(ItemRunStatus.Idle, t.State.Status);

        mark = t.Mark();
        var play = t.Play(At3, clockPaused: true);
        t.OkUntilIdle();
        Assert.Equal(
            [
                new Action.StartRun(1, PumpAndGraph, ItemGraphPolicy.Rebuild, At3, null, HoldForSettle: false),
                new Action.ResumeAudio(),
                new Action.ResumeClocks(),
                new Action.OpenGates(),
                new Action.CompleteCommand(play, ItemCommandResult.Ok),
            ],
            t.Since(mark)
        );
    }

    // ── Superseding seeks coalesce ──────────────────────────────────────────

    [Fact]
    public void ASeekSupersedingOneDuringTheUnwind_WaitsForTheSameStop_AndTheFirstCompletesCancelled()
    {
        var t = ItemRuntimeTranscript.Playing();
        var first = t.Seek(At3);
        t.Ok(); // the audio sink paused; the run is stopping

        var second = t.Seek(At6);
        Assert.Equal([new Action.CompleteCommand(first, ItemCommandResult.Cancelled)], t.Actions);
        t.Ok();
        Assert.Equal([new Action.CloseGates()], t.Actions); // the run is stopping already: nothing more to cancel
        Assert.Null(t.Last.Awaited);

        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        t.OkUntilIdle();

        Assert.Equal(1, t.Count<Action.CancelRun>());
        Assert.Equal([new Action.SeekDemux(At6)], t.All.OfType<Action.SeekDemux>());
        Assert.Equal([ItemCommandResult.Cancelled], t.ResultsOf(first));
        Assert.Equal([ItemCommandResult.Ok], t.ResultsOf(second));
        Assert.Equal(1, t.State.Run);
    }

    [Fact]
    public void ASeekAfterItsPredecessorsCallerCancelledIt_WaitsForTheSameStop()
    {
        // The controller cancels a superseded seek before it sends the next one, so the runtime
        // sees the cancellation first.
        var t = ItemRuntimeTranscript.Playing();
        var first = t.Seek(At3);
        t.Ok();

        t.CallerCancels(first);
        Assert.Equal([new Action.CompleteCommand(first, ItemCommandResult.Cancelled)], t.Actions);
        Assert.Null(t.State.Command);
        Assert.Equal(ItemRunStatus.Stopping, t.State.Status); // the stop goes on

        var second = t.Seek(At6);
        t.Ok();
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        t.OkUntilIdle();

        Assert.Equal(1, t.Count<Action.CancelRun>());
        Assert.Equal([new Action.SeekDemux(At6)], t.All.OfType<Action.SeekDemux>());
        Assert.Equal([ItemCommandResult.Ok], t.ResultsOf(second));
    }

    [Fact]
    public void AScrubOfManySeeks_EndsAtTheLastTarget_WithOneUnwindAndOneSeek()
    {
        var t = ItemRuntimeTranscript.Playing();
        var startsBefore = t.Count<Action.StartRun>();
        var superseded = new List<int>();

        for (var i = 1; i <= 12; i++)
        {
            var seek = t.Seek(TimeSpan.FromMilliseconds(500 * i));
            superseded.Add(seek);
            if (i % 3 == 0)
            {
                // Cancelled while its audio pause is under way: it ends when the pause does.
                t.CallerCancels(seek);
                Assert.Empty(t.Actions);
                t.Ok();
            }
            else if (i % 3 == 1)
            {
                // Cancelled while it waits for the stop.
                t.Ok();
                t.CallerCancels(seek);
            }
            else
            {
                // Superseded by the next seek while it waits for the stop.
                t.Ok();
                continue;
            }
        }

        var last = t.Seek(TimeSpan.FromSeconds(9));
        t.Ok();
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        t.OkUntilIdle();

        Assert.Equal(1, t.Count<Action.CancelRun>());
        Assert.Equal([new Action.SeekDemux(TimeSpan.FromSeconds(9))], t.All.OfType<Action.SeekDemux>());
        Assert.Equal(1, t.Count<Action.ResetDecoders>());
        Assert.Equal(startsBefore + 1, t.Count<Action.StartRun>());
        Assert.Equal(1, t.State.Run);
        Assert.All(superseded, s => Assert.Equal([ItemCommandResult.Cancelled], t.ResultsOf(s)));
        Assert.Equal([ItemCommandResult.Ok], t.ResultsOf(last));
        Assert.Equal(ItemRunStatus.Running, t.State.Status);
    }

    // ── A rewind ────────────────────────────────────────────────────────────

    [Fact]
    public void ARewindAtTheEnd_FindsTheRunEnded_SeeksWithoutWaiting_AndRunsTheGraphAgain()
    {
        // The runtime's AwaitClean path before ADR-0086: the run ended by itself, so nothing is
        // waited for.
        var t = ItemRuntimeTranscript.Playing();
        t.Ends(ItemWork.Pump, ItemWorkEnding.Completed);
        t.Ends(ItemWork.Graph, ItemWorkEnding.Completed);
        Assert.Equal([new Action.RaiseEndOfStream(0)], t.Actions);

        var rewind = t.Rewind();
        t.Ok(); // the audio sink paused
        Assert.Equal([new Action.CloseGates(), new Action.CancelRun(0), new Action.FlushPacer()], t.Actions); // its token retires
        Assert.Equal(new Action.DeactivateAudio(), t.Last.Awaited);
        Assert.Equal(1, t.State.Run);

        var mark = t.Mark();
        t.OkUntilIdle();
        Assert.Equal(
            [
                new Action.SeekDemux(TimeSpan.Zero),
                new Action.ResetDecoders(),
                new Action.SeatClocks(TimeSpan.Zero, Shared: true),
                new Action.ActivateAudio(),
                new Action.StartRun(1, PumpAndGraph, ItemGraphPolicy.Reuse, TimeSpan.Zero, null, HoldForSettle: true),
                new Action.OpenGates(),
                new Action.ResumeAudio(),
                new Action.Settle(),
                new Action.CompleteCommand(rewind, ItemCommandResult.Ok),
            ],
            t.Since(mark)
        );
    }

    [Fact]
    public void ARewindAtTheEnd_WithALatenessRecoveryWalk_StopsTheWalk_AndWaitsForItBeforeItSeeks()
    {
        // The walk ends only when it is cancelled, so a run that played to its end is not over.
        var t = ItemRuntimeTranscript.Playing(latenessRecovery: true);
        Assert.Equal(PumpAndGraph | ItemWork.Recovery, t.State.Pending);
        t.Ends(ItemWork.Pump, ItemWorkEnding.Completed);
        t.Ends(ItemWork.Graph, ItemWorkEnding.Completed);

        t.Rewind();
        t.Ok();
        Assert.Equal([new Action.CloseGates(), new Action.CancelRun(0)], t.Actions);
        Assert.Null(t.Last.Awaited);
        Assert.Equal(ItemRunStatus.Stopping, t.State.Status);

        t.Ends(ItemWork.Recovery);
        Assert.Equal([new Action.FlushPacer()], t.Actions);
        Assert.Equal(1, t.State.Run);
    }

    [Fact]
    public void ARewindBeforeAnyGraphWasBuilt_BuildsOne()
    {
        var t = new ItemRuntimeTranscript();
        t.Initialize();
        t.Opened();

        t.Rewind();
        t.OkUntilIdle();
        var start = t.Play();
        t.OkUntilIdle();

        Assert.Equal(ItemGraphPolicy.Rebuild, Assert.Single(t.All.OfType<Action.StartRun>()).Graph);
        Assert.Equal([ItemCommandResult.Ok], t.ResultsOf(start));
    }

    [Fact]
    public void ARewindPastTheEnd_IsRefused_AndTheRunsStillStopAtTheNewEnd()
    {
        var t = ItemRuntimeTranscript.Playing();
        var end = TimeSpan.FromSeconds(12);

        var rewind = t.Rewind(TimeSpan.FromSeconds(11), end);

        Assert.Equal([ItemCommandResult.Refused(ItemRefusal.PastTheEnd)], t.ResultsOf(rewind));
        Assert.Equal([new Action.CompleteCommand(rewind, ItemCommandResult.Refused(ItemRefusal.PastTheEnd))], t.Actions);
        Assert.Equal(end, t.State.ItemEnd);
        Assert.Equal(ItemRunStatus.Running, t.State.Status);
    }

    // ── A seek cut short, then a dispose or a play ──────────────────────────

    [Fact]
    public void ADisposeAfterACutShortSeek_FreesNothingUntilTheRunHasEnded()
    {
        var t = ItemRuntimeTranscript.Playing();
        var seek = t.Seek(At3);
        t.Ok();
        t.CallerCancels(seek);

        t.Dispose();
        Assert.Equal([new Action.OpenGates()], t.Actions); // the run is cancelled already
        Assert.Equal(ItemRuntimePhase.Disposing, t.State.Phase);

        t.Ends(ItemWork.Pump);
        Assert.Empty(t.Actions);

        t.Ends(ItemWork.Graph);
        Assert.Equal(
            [
                new Action.Free(ItemResources.Pacer),
                new Action.DeactivateAudio(),
                new Action.StopClock(),
                new Action.Free(ItemResources.Pipeline),
                new Action.Free(ItemResources.AudioDecoder),
                new Action.Free(ItemResources.VideoDecoder),
                new Action.Free(ItemResources.Demux),
                new Action.Free(ItemResources.Clock),
                new Action.CompleteDispose(),
            ],
            t.Actions
        );
        Assert.Equal(ItemRuntimePhase.Disposed, t.State.Phase);
        Assert.Empty(t.All.OfType<Action.SeekDemux>());
    }

    [Fact]
    public void AFirstPlayAfterACutShortSeek_RepositionsAtTheClock_AndStartsARun()
    {
        // #714: the warmed run was stopped by a seek that was then cancelled, and the first play
        // used to open the gates on nothing.
        var t = new ItemRuntimeTranscript();
        t.Initialize();
        t.Opened();
        t.WarmUp();
        t.OkUntilIdle();
        t.FirstFrame();

        var seek = t.Seek(At3);
        Assert.Equal([new Action.CloseGates(), new Action.CancelRun(0)], t.Actions); // nothing played: no audio
        t.CallerCancels(seek);
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        Assert.Empty(t.Actions);
        Assert.Equal(ItemRunStatus.Stopped, t.State.Status);
        Assert.Equal(0, t.State.Run);

        var mark = t.Mark();
        var play = t.Play(At1);
        t.OkUntilIdle();

        Assert.Equal(
            [
                new Action.CloseGates(),
                new Action.FlushPacer(),
                new Action.SeekDemux(At1),
                new Action.ResetDecoders(),
                new Action.SeatClocks(At1, Shared: true),
                new Action.SeatBaseline(At1),
                new Action.ActivateAudio(),
                new Action.StartRun(1, PumpAndGraph, ItemGraphPolicy.Rebuild, At1, null, HoldForSettle: false),
                new Action.StartClocks(At1),
                new Action.OpenGates(),
                new Action.CompleteCommand(play, ItemCommandResult.Ok),
            ],
            t.Since(mark)
        );
        Assert.Equal(ItemRunStatus.Running, t.State.Status);
        Assert.True(t.State.Played);
    }

    [Fact]
    public void AResumeAfterASeekCutShortByAPause_RepositionsWhereItPaused_AndStartsARun()
    {
        // #714: the controller cancels a seek when a pause arrives, then pauses.
        var t = ItemRuntimeTranscript.Playing();
        var seek = t.Seek(At3);
        t.Ok();
        t.CallerCancels(seek);
        t.Pause();
        t.OkUntilIdle();
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        Assert.Equal(ItemRunStatus.Stopped, t.State.Status);

        var mark = t.Mark();
        var play = t.Play(At1, clockPaused: true);
        t.OkUntilIdle();

        Assert.Equal(
            [
                new Action.PauseAudio(),
                new Action.CloseGates(),
                new Action.FlushPacer(),
                new Action.DeactivateAudio(),
                new Action.SeekDemux(At1),
                new Action.ResetDecoders(),
                new Action.SeatClocks(At1, Shared: true),
                new Action.ActivateAudio(),
                new Action.PauseAudio(),
                new Action.StartRun(1, PumpAndGraph, ItemGraphPolicy.Rebuild, At1, null, HoldForSettle: false),
                new Action.ResumeAudio(),
                new Action.ResumeClocks(),
                new Action.OpenGates(),
                new Action.CompleteCommand(play, ItemCommandResult.Ok),
            ],
            t.Since(mark)
        );
        Assert.Equal(ItemRunStatus.Running, t.State.Status);
    }

    [Fact]
    public void APlayAfterASeekCutShortWhilePlaying_RepositionsAndPlaysOn_ThenResumes()
    {
        // As the runtime did after #715: the reposition relaunches, and the resume finds the run
        // going.
        var t = ItemRuntimeTranscript.Playing();
        var seek = t.Seek(At3);
        t.Ok();
        t.CallerCancels(seek);
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);

        var mark = t.Mark();
        var play = t.Play(At1);
        t.OkUntilIdle();

        var actions = t.Since(mark);
        Assert.Equal(
            new Action.StartRun(1, PumpAndGraph, ItemGraphPolicy.Rebuild, At1, null, HoldForSettle: true),
            Assert.Single(actions.OfType<Action.StartRun>())
        );
        Assert.Equal(
            [
                new Action.OpenGates(),
                new Action.ResumeAudio(),
                new Action.Settle(),
                new Action.ResumeAudio(),
                new Action.ResumeClocks(),
                new Action.OpenGates(),
                new Action.CompleteCommand(play, ItemCommandResult.Ok),
            ],
            actions.SkipWhile(a => a is not Action.StartRun).Skip(1)
        );
    }

    [Fact]
    public void AWarmUpAfterACutShortSeek_RepositionsBeforeItStartsARun()
    {
        var t = new ItemRuntimeTranscript();
        t.Initialize();
        t.Opened();
        t.WarmUp();
        t.OkUntilIdle();
        var seek = t.Seek(At3);
        t.CallerCancels(seek);
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);

        var mark = t.Mark();
        var warm = t.WarmUp(At1);
        t.OkUntilIdle();
        t.FirstFrame();

        var actions = t.Since(mark);
        Assert.True(
            actions.ToList().FindIndex(a => a is Action.ResetDecoders) < actions.ToList().FindIndex(a => a is Action.StartRun),
            "The warm-up started a run before the decoders were reset."
        );
        Assert.Equal([ItemCommandResult.Ok], t.ResultsOf(warm));
        Assert.Equal(ItemRunStatus.Running, t.State.Status);
    }

    [Fact]
    public void ASeekCancelledWhileTheDemuxerSeeks_EndsWithTheSeek_AndLeavesTheRunToReposition()
    {
        var t = ItemRuntimeTranscript.Playing();
        var seek = t.Seek(At3);
        t.Ok();
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        t.Ok(); // deactivated
        Assert.Equal(new Action.SeekDemux(At3), t.Last.Awaited);

        t.CallerCancels(seek);
        Assert.Empty(t.Actions); // the caller has its answer when the seek returns
        Assert.Empty(t.ResultsOf(seek));

        t.Cancelled();
        Assert.Equal([new Action.CompleteCommand(seek, ItemCommandResult.Cancelled)], t.Actions);
        Assert.Equal(ItemRunStatus.Stopped, t.State.Status);

        // Even had the demuxer's seek gone through, the command goes no further once cancelled.
        var other = ItemRuntimeTranscript.Playing();
        var again = other.Seek(At3);
        other.Ok();
        other.Ends(ItemWork.Pump);
        other.Ends(ItemWork.Graph);
        other.Ok();
        other.CallerCancels(again);
        other.Ok();
        Assert.Equal([new Action.CompleteCommand(again, ItemCommandResult.Cancelled)], other.Actions);
        Assert.Equal(ItemRunStatus.Stopped, other.State.Status);
        Assert.Empty(other.All.OfType<Action.ResetDecoders>());
    }

    // ── End of stream and faults during a seek ──────────────────────────────

    [Fact]
    public void AnEndOfStreamDuringASeek_IsNotRaised_AndTheSeekGoesOn()
    {
        var t = ItemRuntimeTranscript.Playing();
        var seek = t.Seek(At3);
        t.Ok();

        t.Ends(ItemWork.Pump, ItemWorkEnding.Completed);
        t.Ends(ItemWork.Graph, ItemWorkEnding.Completed);
        Assert.Equal([new Action.FlushPacer()], t.Actions); // the stop is replacing that run
        t.OkUntilIdle();

        Assert.Empty(t.All.OfType<Action.RaiseEndOfStream>());
        Assert.Equal([ItemCommandResult.Ok], t.ResultsOf(seek));
    }

    [Fact]
    public void AnEndOfStreamJustBeforeASeek_IsRaisedWithItsOwnRun()
    {
        var t = ItemRuntimeTranscript.Playing();
        t.Ends(ItemWork.Graph, ItemWorkEnding.Completed);
        Assert.Equal([new Action.RaiseEndOfStream(0)], t.Actions);
        Assert.Equal(0, t.State.Run); // what the end of stream is read against

        t.Seek(At3);
        t.Ok();
        t.Ends(ItemWork.Pump);
        Assert.Equal(1, t.State.Run);
    }

    [Fact]
    public void AFaultDuringASeek_IsReportedOnce_AndTheSeekGoesOn()
    {
        var t = ItemRuntimeTranscript.Playing();
        var seek = t.Seek(At3);
        t.Ok();

        t.Ends(ItemWork.Pump, ItemWorkEnding.Faulted, Boom);
        Assert.Equal([new Action.ReportFault(0, ItemWork.Pump, Boom)], t.Actions);

        t.Ends(ItemWork.Graph);
        t.OkUntilIdle();

        Assert.Single(t.All.OfType<Action.ReportFault>());
        Assert.Equal([ItemCommandResult.Ok], t.ResultsOf(seek));
    }

    [Fact]
    public void AFaultOfTheRecoveryWalk_IsReported_AndThePlaybackGoesOn()
    {
        var t = ItemRuntimeTranscript.Playing(latenessRecovery: true);

        t.Ends(ItemWork.Recovery, ItemWorkEnding.Faulted, Boom);

        Assert.Equal([new Action.ReportFault(0, ItemWork.Recovery, Boom)], t.Actions);
        Assert.Equal(ItemRunStatus.Running, t.State.Status);
        Assert.Equal(PumpAndGraph, t.State.Pending);
    }

    // ── Work that ends before its start's outcome ───────────────────────────

    [Fact]
    public void APumpThatEndsBeforeItsStartsOutcome_IsTakenOffTheRun_SoTheDisposeEnds()
    {
        // The audit's sequence: a pump over a tiny source ends before the warm-up's start returns.
        var t = new ItemRuntimeTranscript();
        t.Initialize();
        t.Opened();
        t.WarmUp();

        t.Send(new Input.WorkEnded(0, ItemWork.Pump, ItemWorkEnding.Completed));
        Assert.Empty(t.Actions);
        Assert.Equal(ItemWork.Pump, t.State.EndedBeforeStart);

        t.Ok();
        Assert.Equal(ItemWork.Graph, t.State.Pending);
        Assert.Equal(ItemWork.None, t.State.EndedBeforeStart);
        t.FirstFrame();

        t.Dispose();
        t.Send(new Input.WorkEnded(0, ItemWork.Graph, ItemWorkEnding.Cancelled));

        Assert.Equal(ItemRuntimePhase.Disposed, t.State.Phase);
        Assert.IsType<Action.CompleteDispose>(t.Actions[^1]);
    }

    [Fact]
    public void AnEmptyItemsEndThatCompletesBeforeItsStartsOutcome_IsRaisedThen_AndTheRunHasNothingPending()
    {
        var t = new ItemRuntimeTranscript();
        t.Initialize();
        t.Opened(ItemResources.Clock | ItemResources.Demux | ItemResources.Pipeline);
        var play = t.Play();
        Assert.Equal(new Action.StartRun(0, ItemWork.EmptyEnd, ItemGraphPolicy.Rebuild, TimeSpan.Zero, null, false), t.Last.Awaited);

        t.Ends(ItemWork.EmptyEnd, ItemWorkEnding.Completed);
        Assert.Equal([new Action.RaiseEndOfStream(0)], t.Actions);

        t.Ok();
        Assert.Equal(
            [new Action.StartClocks(TimeSpan.Zero), new Action.OpenGates(), new Action.CompleteCommand(play, ItemCommandResult.Ok)],
            t.Actions
        );
        Assert.Equal(ItemRunStatus.Running, t.State.Status);
        Assert.Equal(ItemWork.None, t.State.Pending);

        // A seek finds nothing to wait for.
        t.Seek(At3);
        Assert.Equal([new Action.CloseGates(), new Action.CancelRun(0)], t.Actions); // no pacer to flush
        Assert.Equal(new Action.SeekDemux(At3), t.Last.Awaited);
        Assert.Single(t.All.OfType<Action.RaiseEndOfStream>());
    }

    [Fact]
    public void AFaultBeforeItsStartsOutcome_IsReportedOnce_AndThePieceIsNotPending()
    {
        var t = ItemRuntimeTranscript.Playing();
        t.Seek(At3);
        t.Ok();
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        t.Ok(); // deactivated
        t.Ok(); // sought
        t.Ok(); // activated
        Assert.IsType<Action.StartRun>(t.Last.Awaited);

        t.Send(new Input.WorkEnded(1, ItemWork.Pump, ItemWorkEnding.Faulted, Boom));
        Assert.Equal([new Action.ReportFault(1, ItemWork.Pump, Boom)], t.Actions);
        t.Send(new Input.WorkEnded(1, ItemWork.Pump, ItemWorkEnding.Faulted, Boom)); // once only
        Assert.Empty(t.Actions);

        t.Ok();
        Assert.Equal(ItemWork.Graph, t.State.Pending);
        Assert.Single(t.All.OfType<Action.ReportFault>());
    }

    // ── A relaunch that ends before its settle ──────────────────────────────

    [Fact]
    public void ARelaunchThatEndsBeforeItsSettle_ReleasesTheHoldItsRunTook()
    {
        var t = ItemRuntimeTranscript.Playing();
        var seek = t.Seek(At3);
        t.Ok();
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        t.Ok(); // deactivated
        t.Ok(); // sought
        t.Ok(); // activated
        Assert.Equal(new Action.StartRun(1, PumpAndGraph, ItemGraphPolicy.Rebuild, At3, null, HoldForSettle: true), t.Last.Awaited);
        t.Ok();
        Assert.True(t.State.Command!.SettleHeld);
        Assert.Equal(new Action.ResumeAudio(), t.Last.Awaited);

        t.Cancelled();

        Assert.Equal(
            [new Action.ReleaseSettle(), new Action.CompleteCommand(seek, ItemCommandResult.Cancelled)],
            t.Actions
        );
    }

    [Fact]
    public void ARelaunchCancelledByItsCallerAsItsRunStarts_ReleasesTheHold()
    {
        var t = ItemRuntimeTranscript.Playing();
        var seek = t.Seek(At3);
        t.Ok();
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        t.Ok();
        t.Ok();
        t.Ok();
        t.CallerCancels(seek);

        t.Ok(); // the run started

        // The run started to play on, over the sink the relaunch activated: the gates open over it.
        Assert.Equal(
            [new Action.OpenGates(), new Action.ReleaseSettle(), new Action.CompleteCommand(seek, ItemCommandResult.Cancelled)],
            t.Actions
        );
        Assert.Equal(ItemRunStatus.Running, t.State.Status);
    }

    [Fact]
    public void ARelaunchThatSettles_ReleasesNothingMore()
    {
        var t = ItemRuntimeTranscript.Playing();
        t.Seek(At3);
        t.Ok();
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        t.OkUntilIdle();

        Assert.Single(t.All.OfType<Action.Settle>());
        Assert.Empty(t.All.OfType<Action.ReleaseSettle>());

        // Cancelled during the settle: the settle releases the hold itself.
        var u = ItemRuntimeTranscript.Playing();
        u.Seek(At3);
        u.Ok();
        u.Ends(ItemWork.Pump);
        u.Ends(ItemWork.Graph);
        while (u.Last.Awaited is not Action.Settle)
            u.Ok();
        u.Cancelled();
        Assert.Empty(u.All.OfType<Action.ReleaseSettle>());
    }

    // ── A seek at or past the item's end ────────────────────────────────────

    [Theory]
    [InlineData(5)] // at the end
    [InlineData(7)] // past it
    public void ASeekAtOrPastTheItemsEnd_StartsARunThatPlaysToTheEndOfTheSource(int seconds)
    {
        var end = TimeSpan.FromSeconds(5);
        var target = TimeSpan.FromSeconds(seconds);
        var t = new ItemRuntimeTranscript();
        t.Initialize(TimeSpan.Zero, end);
        t.Opened();
        t.WarmUp();
        Assert.Equal(new Action.StartRun(0, PumpAndGraph, ItemGraphPolicy.Rebuild, TimeSpan.Zero, end, false), t.Last.Awaited);
        t.Ok();
        t.FirstFrame();
        t.Play();
        t.OkUntilIdle();

        t.Seek(target);
        t.Ok();
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        while (t.Last.Awaited is not Action.StartRun)
            t.Ok();

        Assert.Equal(new Action.StartRun(1, PumpAndGraph, ItemGraphPolicy.Rebuild, target, null, HoldForSettle: true), t.Last.Awaited);
    }

    [Fact]
    public void ASeekBeforeTheItemsEnd_StopsTheRunThere()
    {
        var end = TimeSpan.FromSeconds(5);
        var t = new ItemRuntimeTranscript();
        t.Initialize(TimeSpan.Zero, end);
        t.Opened();

        t.Seek(At3, clockPaused: true);
        t.OkUntilIdle();
        t.WarmUp(At3);

        Assert.Equal(new Action.StartRun(1, PumpAndGraph, ItemGraphPolicy.Rebuild, At3, end, false), t.Last.Awaited);
    }

    // ── Warm-up ─────────────────────────────────────────────────────────────

    [Fact]
    public void AWarmUpThenAPlay_StartsOneRun_AndThePlayOpensTheGatesOnIt()
    {
        var t = new ItemRuntimeTranscript();
        t.Initialize();
        t.Opened();

        var warm = t.WarmUp();
        Assert.Equal(new Action.StartRun(0, PumpAndGraph, ItemGraphPolicy.Rebuild, TimeSpan.Zero, null, false), t.Last.Awaited);
        t.Ok();
        Assert.Empty(t.Actions); // waits for the first frame
        Assert.Empty(t.ResultsOf(warm));
        t.FirstFrame();
        Assert.Equal([ItemCommandResult.Ok], t.ResultsOf(warm));

        // A warm-up again starts nothing beside the run.
        var again = t.WarmUp();
        Assert.Equal([new Action.CompleteCommand(again, ItemCommandResult.Ok)], t.Actions);

        var mark = t.Mark();
        var play = t.Play();
        t.OkUntilIdle();
        Assert.Equal(
            [
                new Action.SeatBaseline(TimeSpan.Zero),
                new Action.ActivateAudio(),
                new Action.StartClocks(TimeSpan.Zero),
                new Action.OpenGates(),
                new Action.CompleteCommand(play, ItemCommandResult.Ok),
            ],
            t.Since(mark)
        );
        Assert.Equal(1, t.Count<Action.StartRun>());
    }

    [Fact]
    public void AWarmUpWhoseRunFaultsBeforeAFrame_EndsWithThePump()
    {
        var t = new ItemRuntimeTranscript();
        t.Initialize();
        t.Opened();
        var warm = t.WarmUp();
        t.Ok();

        t.Ends(ItemWork.Pump, ItemWorkEnding.Faulted, Boom);

        Assert.Equal(
            [new Action.ReportFault(0, ItemWork.Pump, Boom), new Action.CompleteCommand(warm, ItemCommandResult.Ok)],
            t.Actions
        );
    }

    [Fact]
    public void AnOpenAtAStart_PositionsThereAndHolds()
    {
        var t = new ItemRuntimeTranscript();
        var start = TimeSpan.FromSeconds(2);
        var open = t.Initialize(start, TimeSpan.FromSeconds(5));
        t.Opened();

        Assert.Equal([new Action.CloseGates(), new Action.FlushPacer()], t.Actions);
        Assert.Equal(new Action.SeekDemux(start), t.Last.Awaited);
        t.Ok();
        Assert.Equal(
            [
                new Action.ResetDecoders(),
                new Action.SeatClocks(start, Shared: true),
                new Action.CompleteCommand(open, ItemCommandResult.Ok),
            ],
            t.Actions
        );
        Assert.Equal(1, t.State.Run);

        t.WarmUp(start);
        Assert.Equal(
            new Action.StartRun(1, PumpAndGraph, ItemGraphPolicy.Rebuild, start, TimeSpan.FromSeconds(5), false),
            t.Last.Awaited
        );
    }

    [Fact]
    public void AnOpenAtOrPastTheEnd_IsRefused()
    {
        var t = new ItemRuntimeTranscript();
        var open = t.Initialize(ItemRuntimeTranscript.Duration);
        t.Opened();

        Assert.Equal([ItemCommandResult.Refused(ItemRefusal.PastTheEnd)], t.ResultsOf(open));
        Assert.Empty(t.All.OfType<Action.SeekDemux>());
        Assert.Equal(ItemRuntimePhase.Open, t.State.Phase); // the playlist disposes it
    }

    [Fact]
    public void AnOpenThatFails_LeavesTheRuntimeNew()
    {
        var t = new ItemRuntimeTranscript();
        var open = t.Initialize();
        t.Fails(Boom);

        Assert.Equal([ItemCommandResult.Failed(Boom)], t.ResultsOf(open));
        Assert.Equal(ItemRuntimePhase.New, t.State.Phase);
        Assert.Equal(ItemResources.Clock, t.State.Resources);

        t.Dispose();
        Assert.Equal(
            [new Action.OpenGates(), new Action.StopClock(), new Action.Free(ItemResources.Clock), new Action.CompleteDispose()],
            t.Actions
        );
    }

    // ── A runtime prepared ahead ────────────────────────────────────────────

    [Fact]
    public void APreparedRuntime_SeatsOnlyItsOwnClock_UntilItIsAttached()
    {
        var t = new ItemRuntimeTranscript(ItemRuntimeState.Initial(attached: false));
        var start = TimeSpan.FromSeconds(2);
        t.Initialize(start);
        t.Opened();
        t.OkUntilIdle();
        t.WarmUp(start);
        t.OkUntilIdle();

        Assert.Contains(new Action.SeatClocks(start, Shared: false), t.All);

        t.Attach();
        Assert.Equal([new Action.AttachClocks()], t.Actions);
        t.Attach();
        Assert.Empty(t.Actions);
    }

    [Fact]
    public void APreparedRuntimeDisposedDetached_LeavesTheGatesTheAudioSinkAndThePositionClockAlone()
    {
        var t = new ItemRuntimeTranscript(ItemRuntimeState.Initial(attached: false));
        t.Initialize();
        t.Opened();
        t.WarmUp();
        t.Ok();
        t.FirstFrame();

        t.Dispose();
        Assert.Equal([new Action.CancelRun(0)], t.Actions);
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);

        Assert.DoesNotContain(t.Actions, a => a is Action.OpenGates or Action.DeactivateAudio or Action.StopClock);
        Assert.IsType<Action.CompleteDispose>(t.Actions[^1]);
    }

    [Fact]
    public void APreparedRuntime_RefusesAPlayAndAPause_UntilItIsAttached()
    {
        // Either would act on the position clock and the audio sink of the item that is playing.
        var t = new ItemRuntimeTranscript(ItemRuntimeState.Initial(attached: false));
        t.Initialize();
        t.Opened();
        t.WarmUp();
        t.Ok();
        t.FirstFrame();
        var prepared = t.State;

        var pause = t.Pause();
        Assert.Equal([new Action.CompleteCommand(pause, ItemCommandResult.Refused(ItemRefusal.Detached))], t.Actions);
        var play = t.Play();
        Assert.Equal([new Action.CompleteCommand(play, ItemCommandResult.Refused(ItemRefusal.Detached))], t.Actions);
        Assert.Equal(prepared, t.State);

        t.Attach();
        t.Play();
        t.OkUntilIdle();
        Assert.True(t.State.Played);
    }

    // ── The audio comes first ───────────────────────────────────────────────

    [Fact]
    public void AResumeWhoseAudioFails_LeavesTheClocksStillAndTheGatesShut()
    {
        var t = ItemRuntimeTranscript.Playing();
        t.Pause();
        t.OkUntilIdle();

        var play = t.Play(clockPaused: true);
        Assert.Empty(t.Actions);
        Assert.Equal(new Action.ResumeAudio(), t.Last.Awaited);

        t.Fails(Boom);

        Assert.Equal(
            [new Action.PauseClocks(), new Action.CompleteCommand(play, ItemCommandResult.Failed(Boom))],
            t.Actions
        );
        Assert.False(t.State.GatesOpen);
    }

    [Fact]
    public void ARelaunchWhoseAudioResumeFails_HasItsSinkPlayingFromTheActivation()
    {
        // The relaunch activates the sink before its run starts, and an activated sink plays, so the
        // gates it opens before the resume open over sound.
        var t = ItemRuntimeTranscript.Playing();
        var seek = t.Seek(At3);
        t.Ok();
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        while (t.Last.Awaited is not Action.ResumeAudio)
            t.Ok();
        var relaunch = t.All.SkipWhile(a => a is not Action.SeekDemux).ToList();
        Assert.True(
            relaunch.FindIndex(a => a is Action.ActivateAudio) < relaunch.FindIndex(a => a is Action.OpenGates),
            "The gates opened before the sink was activated."
        );

        t.Fails(Boom);

        Assert.Equal(
            [new Action.ReleaseSettle(), new Action.CompleteCommand(seek, ItemCommandResult.Failed(Boom))],
            t.Actions
        );
        Assert.True(t.State.AudioActive);
    }

    [Fact]
    public void AResumeCutShortOnceItsAudioHasResumed_StillRunsTheClocksAndOpensTheGates()
    {
        // The sink plays what it held at the pause, so the picture has to go with it.
        var t = ItemRuntimeTranscript.Playing();
        t.Pause();
        t.OkUntilIdle();
        var play = t.Play(clockPaused: true);
        t.CallerCancels(play);

        t.Ok(); // the audio resumed anyway

        Assert.Equal(
            [
                new Action.ResumeClocks(),
                new Action.OpenGates(),
                new Action.CompleteCommand(play, ItemCommandResult.Cancelled),
            ],
            t.Actions
        );
    }

    [Fact]
    public void ASeekWhoseAudioPauseFails_LeavesTheItemPlayingAsItWas()
    {
        var t = ItemRuntimeTranscript.Playing();
        var seek = t.Seek(At3);
        Assert.Empty(t.Actions);

        t.Fails(Boom);

        Assert.Equal([new Action.CompleteCommand(seek, ItemCommandResult.Failed(Boom))], t.Actions);
        Assert.Equal(ItemRunStatus.Running, t.State.Status);
    }

    [Fact]
    public void ASeekCutShortOnceItsAudioHasPaused_StopsItsRun_AndThePlayAfterItRepositionsAtTheClock()
    {
        // Left going, the run would sit behind shut gates while the clock ran on.
        var t = ItemRuntimeTranscript.Playing();
        var seek = t.Seek(At3);
        t.CallerCancels(seek);

        t.Ok(); // the audio paused anyway

        Assert.Equal(
            [
                new Action.CloseGates(),
                new Action.CancelRun(0),
                new Action.CompleteCommand(seek, ItemCommandResult.Cancelled),
            ],
            t.Actions
        );
        Assert.Equal(ItemRunStatus.Stopping, t.State.Status);

        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        var mark = t.Mark();
        t.Play(At1);
        t.OkUntilIdle();
        Assert.Contains(new Action.SeekDemux(At1), t.Since(mark));
        Assert.Equal(ItemRunStatus.Running, t.State.Status);
    }

    [Fact]
    public void APlayCutShortOnceItsRunHasStarted_HoldsTheRunWithTheClocks()
    {
        // A seek while paused held the item; the play starts the run, and its caller cancels it.
        var t = ItemRuntimeTranscript.Playing();
        t.Pause();
        t.OkUntilIdle();
        t.Seek(At3, clockPaused: true);
        t.Ok();
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        t.OkUntilIdle();
        var play = t.Play(At3, clockPaused: true);
        Assert.IsType<Action.StartRun>(t.Last.Awaited);
        t.CallerCancels(play);

        t.Ok(); // the run started anyway

        Assert.Equal(
            [new Action.PauseClocks(), new Action.CompleteCommand(play, ItemCommandResult.Cancelled)],
            t.Actions
        );
        Assert.Equal(ItemRunStatus.Running, t.State.Status);
        Assert.False(t.State.GatesOpen);
    }

    [Fact]
    public void APauseCutShortOnceItsAudioHasPaused_StillShutsTheGatesAndStopsTheClocks()
    {
        var t = ItemRuntimeTranscript.Playing();
        var pause = t.Pause();
        t.CallerCancels(pause);

        t.Ok(); // the audio paused anyway

        Assert.Equal(
            [
                new Action.CloseGates(),
                new Action.PauseClocks(),
                new Action.CompleteCommand(pause, ItemCommandResult.Cancelled),
            ],
            t.Actions
        );
    }

    // ── Commands the runtime refuses, and inputs it ignores ─────────────────

    public enum Command
    {
        Initialize,
        WarmUp,
        Play,
        Pause,
        Seek,
        Rewind,
    }

    public static TheoryData<Command> EveryCommand
    {
        get
        {
            var commands = new TheoryData<Command>();
            foreach (var command in Enum.GetValues<Command>())
                commands.Add(command);
            return commands;
        }
    }

    private static Input Numbered99(Command command) =>
        command switch
        {
            Command.Initialize => new Input.Initialize(99, TimeSpan.Zero, null),
            Command.WarmUp => new Input.WarmUp(99, TimeSpan.Zero),
            Command.Play => new Input.Play(99, TimeSpan.Zero, false),
            Command.Pause => new Input.Pause(99),
            Command.Seek => new Input.Seek(99, At3, false),
            _ => new Input.Rewind(99, TimeSpan.Zero, null, false),
        };

    [Theory]
    [MemberData(nameof(EveryCommand))]
    public void ADisposedRuntime_RefusesEveryCommand(Command command)
    {
        var t = ItemRuntimeTranscript.Playing();
        t.Dispose();
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        var disposed = t.State;

        t.Send(Numbered99(command));

        Assert.Equal([new Action.CompleteCommand(99, ItemCommandResult.Refused(ItemRefusal.Disposed))], t.Actions);
        Assert.Null(t.Last.Awaited);
        Assert.Equal(disposed, t.State);
    }

    [Theory]
    [MemberData(nameof(EveryCommand))]
    public void ADisposingRuntime_RefusesEveryCommand(Command command)
    {
        var t = ItemRuntimeTranscript.Playing();
        t.Dispose();
        var disposing = t.State;

        t.Send(Numbered99(command));

        Assert.Equal([new Action.CompleteCommand(99, ItemCommandResult.Refused(ItemRefusal.Disposed))], t.Actions);
        Assert.Equal(disposing, t.State);
    }

    [Fact]
    public void ACommandWhileAnActionIsUnderWay_IsRefusedAsBusy()
    {
        var t = ItemRuntimeTranscript.Playing();
        t.Seek(At3); // the audio sink is pausing
        var before = t.State;

        var pause = t.Pause();

        Assert.Equal([ItemCommandResult.Refused(ItemRefusal.Busy)], t.ResultsOf(pause));
        Assert.Equal(before, t.State);
    }

    [Fact]
    public void ADisposeWhileAnActionIsUnderWay_WaitsForIt_ThenEndsTheCommandAndDisposes()
    {
        var t = ItemRuntimeTranscript.Playing();
        var seek = t.Seek(At3);

        t.Dispose();
        Assert.Empty(t.Actions);
        Assert.True(t.State.DisposeRequested);

        t.Ok(); // the audio pause ends, and the gates and the stop follow it
        Assert.Equal(
            [
                new Action.CloseGates(),
                new Action.CancelRun(0),
                new Action.CompleteCommand(seek, ItemCommandResult.Cancelled),
                new Action.OpenGates(),
            ],
            t.Actions
        );
        Assert.Equal(ItemRuntimePhase.Disposing, t.State.Phase);
    }

    [Fact]
    public void AStrayCompletionOrOutcome_ChangesNothing()
    {
        var t = ItemRuntimeTranscript.Playing();
        var before = t.State;

        foreach (var stray in new Input[]
                 {
                     new Input.WorkEnded(1, ItemWork.Pump, ItemWorkEnding.Faulted, Boom), // another run
                     new Input.WorkEnded(0, ItemWork.Recovery, ItemWorkEnding.Completed), // not pending
                     new Input.WorkEnded(0, PumpAndGraph, ItemWorkEnding.Completed), // not one piece
                     new Input.Performed(ItemActionOutcome.Ok), // nothing awaited
                     new Input.Opened(ItemResources.None, TimeSpan.Zero),
                     new Input.CallerCancelled(42),
                 })
        {
            t.Send(stray);
            Assert.Empty(t.Actions);
            Assert.Null(t.Last.Awaited);
            Assert.Equal(before, t.State);
        }
    }

    [Fact]
    public void AnOkThatIsNotOpened_IsNotAnOpensSuccess()
    {
        var t = new ItemRuntimeTranscript();
        t.Initialize();
        var opening = t.State;

        t.Ok();

        Assert.Empty(t.Actions);
        Assert.Null(t.Last.Awaited);
        Assert.Equal(opening, t.State);
        Assert.Equal(ItemRuntimePhase.Opening, t.State.Phase);

        // An Opened while something else is awaited is not one either.
        var u = ItemRuntimeTranscript.Playing();
        u.Seek(At3);
        var pausing = u.State;
        u.Opened();
        Assert.Empty(u.Actions);
        Assert.Equal(pausing, u.State);
    }

    // ── Behaviour that differs from the runtime before ADR-0086 ─────────────

    [Fact]
    public void AResumeWhoseAudioSinkWasLeftInactive_ActivatesIt()
    {
        // A seek while paused that committed, and then failed to activate the sink again.
        var t = ItemRuntimeTranscript.Playing();
        t.Pause();
        t.OkUntilIdle();
        var seek = t.Seek(At3, clockPaused: true);
        t.Ok(); // paused
        t.Ends(ItemWork.Pump);
        t.Ends(ItemWork.Graph);
        t.Ok(); // deactivated
        t.Ok(); // sought
        Assert.Equal(new Action.ActivateAudio(), t.Last.Awaited);
        t.Fails(Boom);
        Assert.Equal([ItemCommandResult.Failed(Boom)], t.ResultsOf(seek));
        Assert.False(t.State.AudioActive);
        Assert.Equal(ItemRunStatus.Idle, t.State.Status);

        var mark = t.Mark();
        t.Play(At3, clockPaused: true);
        t.OkUntilIdle();

        Assert.Contains(new Action.ActivateAudio(), t.Since(mark));
        Assert.DoesNotContain(new Action.ResumeAudio(), t.Since(mark));
        Assert.True(t.State.AudioActive);
    }
}
