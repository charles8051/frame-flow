using FrameFlow.Media;
using FrameFlow.Playback;
using Xunit;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Exhaustive, FFmpeg-free tests of the pure <see cref="PlaybackProtocol"/> Mealy core —
/// the playback transition table lifted out of the Stateless
/// <c>OnEntry</c>/<c>OnExit</c>/<c>InternalTransition</c> callbacks (ADR-0055's "sibling
/// pattern one layer up"; architecture review §2.1). Every transition cell is asserted
/// here from a scripted <c>(state, trigger, inputs)</c> transcript with nothing plugged in:
/// no session, no clock, no corpus media. This is the isolated test surface the review
/// found missing — before this, a repo-wide grep of <c>tests/</c> for the transition
/// machinery returned zero files and every transition was exercised only end-to-end under
/// <c>[RequiresFfmpegAndCorpusFact]</c>.
///
/// Style mirrors <see cref="LoopStallEvaluatorTests"/>: small pure values folded by hand,
/// asserting the next state and the ordered action list directly.
/// </summary>
public class PlaybackProtocolTests
{
    private static readonly PlaybackInputs Default = new(RepeatOne: false, HasSession: true);
    private static readonly PlaybackInputs RepeatOne = new(RepeatOne: true, HasSession: true);
    private static readonly PlaybackInputs NoSession = new(RepeatOne: false, HasSession: false);

    private static PlaybackDecision Advance(
        InternalPlaybackState state,
        PlaybackTrigger trigger,
        PlaybackInputs? inputs = null
    ) => PlaybackProtocol.Advance(state, trigger, inputs ?? Default);

    private static void AssertActions(
        PlaybackDecision decision,
        params PlaybackActionKind[] expected
    )
    {
        Assert.Equal(expected, decision.Actions.Select(a => a.Kind).ToArray());
    }

    // NOTE on test shape: the state/trigger/action enums are `internal` (visible here via
    // InternalsVisibleTo). xUnit requires public [Theory] methods, but a public method may
    // not take a less-accessible parameter type (CS0051) — so cases that vary over an
    // internal enum are written as a [Fact] iterating an in-body array rather than
    // [Theory]/[InlineData] over the enum. Cases that vary only over public types or ints
    // still use [Theory].

    // ─────────────────────────────────────────────────────────────────────
    // Idle
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Idle_Load_EntersInitializing_CreatesAndInitializesThenChainsHeaders()
    {
        var d = Advance(InternalPlaybackState.Idle, PlaybackTrigger.Load);

        Assert.True(d.Handled);
        Assert.Equal(InternalPlaybackState.Initializing, d.NextState);
        AssertActions(
            d,
            PlaybackActionKind.CreateSession,
            PlaybackActionKind.InitializeSession,
            PlaybackActionKind.FireTrigger
        );
        // The auto-chain follow-up is HeadersReceived.
        Assert.Equal(PlaybackTrigger.HeadersReceived, d.Actions[^1].FollowUp);
    }

    [Fact]
    public void Idle_RejectsEverythingButLoad()
    {
        // (Theory-style cases iterated in-body so the internal PlaybackTrigger enum never
        // appears in a public test signature — see the note above AssertActions.)
        PlaybackTrigger[] rejected =
        [
            PlaybackTrigger.Play,
            PlaybackTrigger.Pause,
            PlaybackTrigger.Seek,
            PlaybackTrigger.Unload,
            PlaybackTrigger.LastFrameRendered,
            PlaybackTrigger.BufferReady,
            PlaybackTrigger.FatalError,
        ];

        foreach (var trigger in rejected)
        {
            var d = Advance(InternalPlaybackState.Idle, trigger);
            Assert.False(d.Handled, $"Idle should reject {trigger}");
            Assert.Equal(InternalPlaybackState.Idle, d.NextState);
            Assert.Empty(d.Actions);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Loading substages — the auto-chain
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Initializing_HeadersReceived_EntersPreparing_ChainsMetadataParsed()
    {
        var d = Advance(InternalPlaybackState.Initializing, PlaybackTrigger.HeadersReceived);

        Assert.Equal(InternalPlaybackState.Preparing, d.NextState);
        AssertActions(d, PlaybackActionKind.FireTrigger);
        Assert.Equal(PlaybackTrigger.MetadataParsed, d.Actions[0].FollowUp);
    }

    [Fact]
    public void Preparing_MetadataParsed_EntersInitialBuffering_WarmsThenChainsBufferReady()
    {
        var d = Advance(InternalPlaybackState.Preparing, PlaybackTrigger.MetadataParsed);

        Assert.Equal(InternalPlaybackState.InitialBuffering, d.NextState);
        AssertActions(d, PlaybackActionKind.WarmUp, PlaybackActionKind.FireTrigger);
        Assert.Equal(PlaybackTrigger.BufferReady, d.Actions[^1].FollowUp);
    }

    [Fact]
    public void InitialBuffering_BufferReady_EntersPaused_NoSessionPauseIssued()
    {
        // From InitialBuffering the session has not started playing, so the
        // OnEntryFrom(BufferReady) handler only logs — no PauseSession action.
        var d = Advance(InternalPlaybackState.InitialBuffering, PlaybackTrigger.BufferReady);

        Assert.Equal(InternalPlaybackState.Paused, d.NextState);
        Assert.Empty(d.Actions);
    }

    [Fact]
    public void InitialBuffering_Play_EntersPlaying_StartsTickerThenPlays()
    {
        var d = Advance(InternalPlaybackState.InitialBuffering, PlaybackTrigger.Play);

        Assert.Equal(InternalPlaybackState.Playing, d.NextState);
        AssertActions(d, PlaybackActionKind.StartTicker, PlaybackActionKind.PlaySession);
    }

    [Fact]
    public void LoadingSubstages_Unload_DisposeSession()
    {
        InternalPlaybackState[] loading =
        [
            InternalPlaybackState.Initializing,
            InternalPlaybackState.Preparing,
            InternalPlaybackState.InitialBuffering,
        ];

        foreach (var from in loading)
        {
            var d = Advance(from, PlaybackTrigger.Unload);
            Assert.Equal(InternalPlaybackState.Unloaded, d.NextState);
            AssertActions(d, PlaybackActionKind.DisposeSession);
        }
    }

    [Fact]
    public void LoadingSubstages_FatalError_RouteToError_DisposeThenRaise()
    {
        InternalPlaybackState[] loading =
        [
            InternalPlaybackState.Initializing,
            InternalPlaybackState.Preparing,
            InternalPlaybackState.InitialBuffering,
        ];

        foreach (var from in loading)
        {
            var d = Advance(from, PlaybackTrigger.FatalError);
            Assert.Equal(InternalPlaybackState.Error, d.NextState);
            AssertActions(d, PlaybackActionKind.DisposeSession, PlaybackActionKind.RaiseError);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Paused
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Paused_Play_EntersPlaying_StartsTickerThenPlays()
    {
        var d = Advance(InternalPlaybackState.Paused, PlaybackTrigger.Play);

        Assert.Equal(InternalPlaybackState.Playing, d.NextState);
        AssertActions(d, PlaybackActionKind.StartTicker, PlaybackActionKind.PlaySession);
    }

    [Fact]
    public void Paused_Unload_DisposeSession()
    {
        var d = Advance(InternalPlaybackState.Paused, PlaybackTrigger.Unload);

        Assert.Equal(InternalPlaybackState.Unloaded, d.NextState);
        AssertActions(d, PlaybackActionKind.DisposeSession);
    }

    [Fact]
    public void Paused_RejectsUnpermitted()
    {
        PlaybackTrigger[] rejected =
        [
            PlaybackTrigger.Pause,
            PlaybackTrigger.Seek,
            PlaybackTrigger.BufferReady,
            PlaybackTrigger.LastFrameRendered,
        ];

        foreach (var trigger in rejected)
        {
            var d = Advance(InternalPlaybackState.Paused, trigger);
            Assert.False(d.Handled, $"Paused should reject {trigger}");
            Assert.Equal(InternalPlaybackState.Paused, d.NextState);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Playing — the marquee loop-vs-end branch
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Playing_LastFrameRendered_RepeatOff_EntersEnded_StopsTickerAndFreezesClock()
    {
        var d = Advance(InternalPlaybackState.Playing, PlaybackTrigger.LastFrameRendered, Default);

        Assert.True(d.Handled);
        Assert.Equal(InternalPlaybackState.Ended, d.NextState);
        AssertActions(d, PlaybackActionKind.StopTicker, PlaybackActionKind.FreezeClock);
    }

    [Fact]
    public void Playing_LastFrameRendered_RepeatOne_InternalLoop_StaysPlaying_RunsRewind()
    {
        var d = Advance(
            InternalPlaybackState.Playing,
            PlaybackTrigger.LastFrameRendered,
            RepeatOne
        );

        Assert.True(d.Handled);
        // Internal transition: the state does not change.
        Assert.Equal(InternalPlaybackState.Playing, d.NextState);
        AssertActions(d, PlaybackActionKind.RunLoopRewind);
        // Critically, the loop must NOT stop the ticker or freeze the clock.
        Assert.DoesNotContain(d.Actions, a => a.Kind == PlaybackActionKind.StopTicker);
        Assert.DoesNotContain(d.Actions, a => a.Kind == PlaybackActionKind.FreezeClock);
    }

    [Fact]
    public void Playing_Pause_EntersPaused_StopsTickerThenPausesSession()
    {
        var d = Advance(InternalPlaybackState.Playing, PlaybackTrigger.Pause);

        Assert.Equal(InternalPlaybackState.Paused, d.NextState);
        // OnExit(Playing) stops the ticker, then OnEntryFrom(Pause) pauses the session.
        AssertActions(d, PlaybackActionKind.StopTicker, PlaybackActionKind.PauseSession);
    }

    [Fact]
    public void Playing_BufferUnderrun_EntersRebuffering_StopsTicker()
    {
        var d = Advance(InternalPlaybackState.Playing, PlaybackTrigger.BufferUnderrun);

        Assert.Equal(InternalPlaybackState.Rebuffering, d.NextState);
        AssertActions(d, PlaybackActionKind.StopTicker);
    }

    [Fact]
    public void Playing_Unload_StopsTickerThenDisposes()
    {
        var d = Advance(InternalPlaybackState.Playing, PlaybackTrigger.Unload);

        Assert.Equal(InternalPlaybackState.Unloaded, d.NextState);
        // Unlike the other Unload cells, Playing stops the ticker (its OnExit) first.
        AssertActions(d, PlaybackActionKind.StopTicker, PlaybackActionKind.DisposeSession);
    }

    [Fact]
    public void Playing_FatalError_RouteToError()
    {
        var d = Advance(InternalPlaybackState.Playing, PlaybackTrigger.FatalError);

        Assert.Equal(InternalPlaybackState.Error, d.NextState);
        AssertActions(d, PlaybackActionKind.DisposeSession, PlaybackActionKind.RaiseError);
    }

    [Fact]
    public void Playing_RejectsUnpermitted()
    {
        PlaybackTrigger[] rejected =
        [
            PlaybackTrigger.Play,
            PlaybackTrigger.Seek,
            PlaybackTrigger.BufferReady,
            PlaybackTrigger.HeadersReceived,
        ];

        foreach (var trigger in rejected)
        {
            var d = Advance(InternalPlaybackState.Playing, trigger);
            Assert.False(d.Handled, $"Playing should reject {trigger}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Rebuffering — note BufferReady→Playing must NOT play the session
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rebuffering_BufferReady_EntersPlaying_StartsTicker_DoesNotPlaySession()
    {
        // BufferReady is not the Play trigger, so EnterPlayingFromPlay (PlaySession)
        // does not run — only the unparameterised Playing OnEntry (StartTicker).
        var d = Advance(InternalPlaybackState.Rebuffering, PlaybackTrigger.BufferReady);

        Assert.Equal(InternalPlaybackState.Playing, d.NextState);
        AssertActions(d, PlaybackActionKind.StartTicker);
        Assert.DoesNotContain(d.Actions, a => a.Kind == PlaybackActionKind.PlaySession);
    }

    [Fact]
    public void Rebuffering_Pause_EntersPaused_PausesSession()
    {
        // Rebuffering has no OnExit (no ticker to stop — it was stopped entering
        // Rebuffering), so Pause only pauses the session via OnEntryFrom(Pause).
        var d = Advance(InternalPlaybackState.Rebuffering, PlaybackTrigger.Pause);

        Assert.Equal(InternalPlaybackState.Paused, d.NextState);
        AssertActions(d, PlaybackActionKind.PauseSession);
        Assert.DoesNotContain(d.Actions, a => a.Kind == PlaybackActionKind.StopTicker);
    }

    [Fact]
    public void Rebuffering_Unload_DisposeSession()
    {
        var d = Advance(InternalPlaybackState.Rebuffering, PlaybackTrigger.Unload);

        Assert.Equal(InternalPlaybackState.Unloaded, d.NextState);
        AssertActions(d, PlaybackActionKind.DisposeSession);
    }

    [Fact]
    public void Rebuffering_FatalError_RouteToError()
    {
        var d = Advance(InternalPlaybackState.Rebuffering, PlaybackTrigger.FatalError);
        Assert.Equal(InternalPlaybackState.Error, d.NextState);
        AssertActions(d, PlaybackActionKind.DisposeSession, PlaybackActionKind.RaiseError);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Ended — seek-out, replay-from-Ended, end terminal-ish behaviour
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ended_Seek_EntersInitialBuffering_WarmsThenChainsBufferReady()
    {
        var d = Advance(InternalPlaybackState.Ended, PlaybackTrigger.Seek);

        Assert.Equal(InternalPlaybackState.InitialBuffering, d.NextState);
        AssertActions(d, PlaybackActionKind.WarmUp, PlaybackActionKind.FireTrigger);
        Assert.Equal(PlaybackTrigger.BufferReady, d.Actions[^1].FollowUp);
    }

    [Fact]
    public void Ended_Play_WithSession_EntersPlaying()
    {
        // The raw Stateless cell is Ended → Playing. (The shell intercepts this and runs
        // the unload+reload+play replay recovery before firing; with a session present the
        // protocol still exposes the underlying permitted transition.)
        var d = Advance(InternalPlaybackState.Ended, PlaybackTrigger.Play, Default);

        Assert.True(d.Handled);
        Assert.Equal(InternalPlaybackState.Playing, d.NextState);
        AssertActions(d, PlaybackActionKind.StartTicker, PlaybackActionKind.PlaySession);
    }

    [Fact]
    public void Ended_Play_NoSession_IsInvalidOperation()
    {
        // replay-from-Ended guard: Play from Ended with no session/source is rejected
        // (the shell fails it with InvalidOperation rather than replaying).
        var d = Advance(InternalPlaybackState.Ended, PlaybackTrigger.Play, NoSession);

        Assert.False(d.Handled);
        Assert.Equal(InternalPlaybackState.Ended, d.NextState);
        Assert.Empty(d.Actions);
    }

    [Fact]
    public void Ended_Unload_DisposeSession()
    {
        var d = Advance(InternalPlaybackState.Ended, PlaybackTrigger.Unload);
        Assert.Equal(InternalPlaybackState.Unloaded, d.NextState);
        AssertActions(d, PlaybackActionKind.DisposeSession);
    }

    [Fact]
    public void Ended_FatalError_RouteToError()
    {
        // Ended DOES permit FatalError (a seek/replay launched from Ended can fault).
        var d = Advance(InternalPlaybackState.Ended, PlaybackTrigger.FatalError);
        Assert.Equal(InternalPlaybackState.Error, d.NextState);
        AssertActions(d, PlaybackActionKind.DisposeSession, PlaybackActionKind.RaiseError);
    }

    [Fact]
    public void Ended_RejectsUnpermitted()
    {
        PlaybackTrigger[] rejected =
        [
            PlaybackTrigger.Pause,
            PlaybackTrigger.BufferReady,
            PlaybackTrigger.LastFrameRendered,
            PlaybackTrigger.BufferUnderrun,
        ];

        foreach (var trigger in rejected)
        {
            var d = Advance(InternalPlaybackState.Ended, trigger);
            Assert.False(d.Handled, $"Ended should reject {trigger}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Unloaded — reload and reset
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Unloaded_Load_EntersInitializing_SameAsIdleLoad()
    {
        var d = Advance(InternalPlaybackState.Unloaded, PlaybackTrigger.Load);

        Assert.Equal(InternalPlaybackState.Initializing, d.NextState);
        AssertActions(
            d,
            PlaybackActionKind.CreateSession,
            PlaybackActionKind.InitializeSession,
            PlaybackActionKind.FireTrigger
        );
        Assert.Equal(PlaybackTrigger.HeadersReceived, d.Actions[^1].FollowUp);
    }

    [Fact]
    public void Unloaded_Reset_EntersIdle_NoActions()
    {
        var d = Advance(InternalPlaybackState.Unloaded, PlaybackTrigger.Reset);
        Assert.Equal(InternalPlaybackState.Idle, d.NextState);
        Assert.Empty(d.Actions);
    }

    [Fact]
    public void Unloaded_RejectsUnpermitted()
    {
        PlaybackTrigger[] rejected =
        [
            PlaybackTrigger.Play,
            PlaybackTrigger.Pause,
            PlaybackTrigger.Seek,
            PlaybackTrigger.FatalError,
        ];

        foreach (var trigger in rejected)
        {
            var d = Advance(InternalPlaybackState.Unloaded, trigger);
            Assert.False(d.Handled, $"Unloaded should reject {trigger}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Error — reset only
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Error_Reset_EntersIdle()
    {
        var d = Advance(InternalPlaybackState.Error, PlaybackTrigger.Reset);
        Assert.Equal(InternalPlaybackState.Idle, d.NextState);
        Assert.Empty(d.Actions);
    }

    [Fact]
    public void Error_RejectsEverythingButReset()
    {
        PlaybackTrigger[] rejected =
        [
            PlaybackTrigger.Load,
            PlaybackTrigger.Play,
            PlaybackTrigger.Unload,
            PlaybackTrigger.FatalError, // already in Error — a second fault is dropped
        ];

        foreach (var trigger in rejected)
        {
            var d = Advance(InternalPlaybackState.Error, trigger);
            Assert.False(d.Handled, $"Error should reject {trigger}");
            Assert.Equal(InternalPlaybackState.Error, d.NextState);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // FatalError routing — exhaustive over every state (error routing branch)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void FatalError_RoutesToError_ExactlyFromPermittingStates()
    {
        // Exhaustive over every internal state: FatalError routes to Error from exactly
        // the seven states whose Stateless config carries Permit(FatalError, Error), and
        // is a dropped stale trigger from Idle / Unloaded / Error.
        (InternalPlaybackState From, bool ShouldRoute)[] cases =
        [
            (InternalPlaybackState.Initializing, true),
            (InternalPlaybackState.Preparing, true),
            (InternalPlaybackState.InitialBuffering, true),
            (InternalPlaybackState.Paused, true),
            (InternalPlaybackState.Playing, true),
            (InternalPlaybackState.Rebuffering, true),
            (InternalPlaybackState.Ended, true),
            (InternalPlaybackState.Idle, false),
            (InternalPlaybackState.Unloaded, false),
            (InternalPlaybackState.Error, false),
        ];

        foreach (var (from, shouldRoute) in cases)
        {
            var d = Advance(from, PlaybackTrigger.FatalError);

            Assert.Equal(shouldRoute, d.Handled);
            if (shouldRoute)
            {
                Assert.Equal(InternalPlaybackState.Error, d.NextState);
                AssertActions(
                    d,
                    PlaybackActionKind.DisposeSession,
                    PlaybackActionKind.RaiseError
                );
            }
            else
            {
                Assert.Equal(from, d.NextState);
                Assert.Empty(d.Actions);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // End-to-end transcripts — drive the machine through whole flows by
    // following the FireTrigger auto-chain, the way the shell does.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives a single externally-fired trigger to a settled state by following every
    /// <see cref="PlaybackActionKind.FireTrigger"/> auto-chain action, collecting the
    /// non-chain actions in order. This is exactly the shell's loop: perform the actions,
    /// and when one says "fire trigger X", re-enter Advance with X.
    /// </summary>
    private static (InternalPlaybackState Final, List<PlaybackActionKind> Effects) Drive(
        InternalPlaybackState start,
        PlaybackTrigger trigger,
        PlaybackInputs inputs
    )
    {
        var effects = new List<PlaybackActionKind>();
        var state = start;
        var pending = new Queue<PlaybackTrigger>();
        pending.Enqueue(trigger);

        var guard = 0;
        while (pending.Count > 0)
        {
            Assert.True(++guard < 100, "Auto-chain did not settle — possible cycle.");
            var t = pending.Dequeue();
            var d = PlaybackProtocol.Advance(state, t, inputs);
            if (!d.Handled)
                continue;
            state = d.NextState;
            foreach (var action in d.Actions)
            {
                if (action.Kind == PlaybackActionKind.FireTrigger)
                    pending.Enqueue(action.FollowUp!.Value);
                else
                    effects.Add(action.Kind);
            }
        }

        return (state, effects);
    }

    [Fact]
    public void Transcript_IdleToPaused_LoadAutoChainsThroughLoadingToPaused()
    {
        // Load fired from Idle should auto-chain Initializing → Preparing →
        // InitialBuffering → Paused, performing exactly: create, initialize, warm.
        var (final, effects) = Drive(InternalPlaybackState.Idle, PlaybackTrigger.Load, Default);

        Assert.Equal(InternalPlaybackState.Paused, final);
        Assert.Equal(
            new[]
            {
                PlaybackActionKind.CreateSession,
                PlaybackActionKind.InitializeSession,
                PlaybackActionKind.WarmUp,
            },
            effects.ToArray()
        );
    }

    [Fact]
    public void Transcript_PausedToEnded_PlayThenLastFrame_RepeatOff()
    {
        // From Paused: Play → Playing (start ticker, play). Then LastFrameRendered with
        // repeat Off → Ended (stop ticker, freeze clock).
        var (afterPlay, playEffects) = Drive(
            InternalPlaybackState.Paused,
            PlaybackTrigger.Play,
            Default
        );
        Assert.Equal(InternalPlaybackState.Playing, afterPlay);
        Assert.Equal(
            new[] { PlaybackActionKind.StartTicker, PlaybackActionKind.PlaySession },
            playEffects.ToArray()
        );

        var (afterEnd, endEffects) = Drive(
            afterPlay,
            PlaybackTrigger.LastFrameRendered,
            Default
        );
        Assert.Equal(InternalPlaybackState.Ended, afterEnd);
        Assert.Equal(
            new[] { PlaybackActionKind.StopTicker, PlaybackActionKind.FreezeClock },
            endEffects.ToArray()
        );
    }

    [Fact]
    public void Transcript_RepeatOneLoop_NeverLeavesPlaying_AcrossManyBoundaries()
    {
        // A RepeatMode.One clip taking many loop boundaries stays in Playing the whole
        // time and runs exactly one rewind per boundary — the attract/kiosk scenario.
        var state = InternalPlaybackState.Playing;
        for (var i = 0; i < 25; i++)
        {
            var (next, effects) = Drive(state, PlaybackTrigger.LastFrameRendered, RepeatOne);
            Assert.Equal(InternalPlaybackState.Playing, next);
            Assert.Equal(new[] { PlaybackActionKind.RunLoopRewind }, effects.ToArray());
            state = next;
        }
    }

    [Fact]
    public void Transcript_EndedSeek_ReturnsToPausedViaInitialBuffering()
    {
        // Seek out of Ended re-warms and settles back at Paused (ready to resume),
        // mirroring the load path's tail.
        var (final, effects) = Drive(InternalPlaybackState.Ended, PlaybackTrigger.Seek, Default);

        Assert.Equal(InternalPlaybackState.Paused, final);
        Assert.Equal(new[] { PlaybackActionKind.WarmUp }, effects.ToArray());
    }

    [Fact]
    public void Transcript_UnloadFromPlaying_StopsTickerAndDisposes_SettlesUnloaded()
    {
        var (final, effects) = Drive(
            InternalPlaybackState.Playing,
            PlaybackTrigger.Unload,
            Default
        );

        Assert.Equal(InternalPlaybackState.Unloaded, final);
        Assert.Equal(
            new[] { PlaybackActionKind.StopTicker, PlaybackActionKind.DisposeSession },
            effects.ToArray()
        );
    }

    [Fact]
    public void Advance_IsPure_SameInputsSameDecision()
    {
        // Determinism: identical (state, trigger, inputs) yields an equal decision every
        // time, with no carried state between calls.
        var a = Advance(InternalPlaybackState.Playing, PlaybackTrigger.LastFrameRendered, RepeatOne);
        var b = Advance(InternalPlaybackState.Playing, PlaybackTrigger.LastFrameRendered, RepeatOne);

        Assert.Equal(a.Handled, b.Handled);
        Assert.Equal(a.NextState, b.NextState);
        Assert.Equal(a.Actions.Select(x => x.Kind), b.Actions.Select(x => x.Kind));
    }
}
