// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// The side-effect vocabulary of the primary playback state machine — the Mealy
/// output of <see cref="PlaybackProtocol"/>. Each value names <b>one</b> thing the
/// imperative shell must do on a transition; the shell (the dispatch loop in
/// <see cref="PlaybackControllerCore"/>) owns the IO, the session, the clock, the
/// ticker, and the sub-machines, and performs the effect each action names.
/// </summary>
/// <remarks>
/// <para>
/// The set is derived from what the playback machine's per-state effects historically
/// <i>did</i> (when they were Stateless <c>OnEntry</c>/<c>OnExit</c>/<c>InternalTransition</c>
/// handlers, before this core became the executor): create/initialize/dispose the session,
/// warm the decoder, play or pause it, start/stop the position ticker, freeze the clock at
/// end-of-stream, run the loop rewind, and the auto-chain "fire the next loading trigger."
/// The shell's interpreter (<see cref="PlaybackControllerCore"/>'s <c>RunPlaybackAsync</c>)
/// performs each. Keeping the session ABI and timing out of the core (mirroring ADR-0055's
/// decode protocol) is what lets the whole transition table be asserted from a scripted
/// transcript with nothing plugged in — no FFmpeg, no clock, no session.
/// </para>
/// <para>
/// <b>Auto-chain actions.</b> The loading substates auto-advance: <c>Initializing</c>
/// fires <c>HeadersReceived</c>, <c>Preparing</c> fires <c>MetadataParsed</c>,
/// <c>InitialBuffering</c> fires <c>BufferReady</c>. The core surfaces each as a
/// <see cref="FireTrigger"/> action carrying the trigger to re-enter
/// <see cref="PlaybackProtocol.Advance"/> with, so the chain is data in the transcript
/// rather than a re-entrant <c>FireAsync</c> buried in an entry handler.
/// </para>
/// </remarks>
internal enum PlaybackActionKind
{
    /// <summary>
    /// Create a fresh session via the factory (wired with the controller's callback
    /// channel). Carries no payload — the source rides the triggering
    /// <see cref="PlaybackTrigger.Load"/>, which the shell already holds.
    /// </summary>
    CreateSession,

    /// <summary>
    /// <c>await session.InitializeAsync(source)</c>, then capture the loaded
    /// <see cref="MediaInfo"/> / duration snapshot. Faults route to
    /// <see cref="PlaybackTrigger.FatalError"/> as a load failure (the shell records
    /// the pending load failure so <c>LoadAsync</c> returns it).
    /// </summary>
    InitializeSession,

    /// <summary>
    /// <c>await session.WarmUpAsync()</c> — pre-roll a video frame to the gate before
    /// Play opens it. A no-op for audio-only sources. Faults route to
    /// <see cref="PlaybackTrigger.FatalError"/> as an initial-buffering failure.
    /// </summary>
    WarmUp,

    /// <summary><c>await session.PlayAsync()</c> — start or resume forward playback.</summary>
    PlaySession,

    /// <summary><c>await session.PauseAsync()</c> — suspend the session at the current position.</summary>
    PauseSession,

    /// <summary>Start the position ticker worker (it samples the clock on a cadence).</summary>
    StartTicker,

    /// <summary>Stop the position ticker worker.</summary>
    StopTicker,

    /// <summary>
    /// Freeze the clock at end-of-stream so reported position stops climbing past
    /// duration. The documented ADR-0028 §1 exception to session-only clock mutation.
    /// </summary>
    FreezeClock,

    /// <summary>Dispose the session and clear the loaded-media snapshot.</summary>
    DisposeSession,

    /// <summary>
    /// Raise the <c>ErrorOccurred</c> observable with the error carried by the
    /// triggering <see cref="PlaybackTrigger.FatalError"/>.
    /// </summary>
    RaiseError,

    /// <summary>
    /// The RepeatMode.One loop boundary: increment the loop counter, raise
    /// <c>LoopRestarted</c>, and run the loop rewind through the seek state machine
    /// (the internal transition that never leaves <c>Playing</c>).
    /// </summary>
    RunLoopRewind,

    /// <summary>
    /// Auto-chain: re-enter <see cref="PlaybackProtocol.Advance"/> with the carried
    /// follow-up trigger. Models the loading substates' self-firing transitions without
    /// a re-entrant <c>FireAsync</c> in an entry handler.
    /// </summary>
    FireTrigger,
}

/// <summary>
/// One element of a <see cref="PlaybackDecision"/>'s ordered action list: the
/// <see cref="PlaybackActionKind"/> plus the single optional payload an action may
/// carry (today only <see cref="FireTrigger"/> carries one — the follow-up trigger).
/// </summary>
/// <remarks>
/// Kept a single value type rather than a class hierarchy so the whole decision is a
/// plain comparable value an assertion can match cell-by-cell. Most actions carry no
/// payload; <see cref="FollowUp"/> is non-null exactly for <see cref="PlaybackActionKind.FireTrigger"/>.
/// </remarks>
internal readonly record struct PlaybackAction(PlaybackActionKind Kind, PlaybackTrigger? FollowUp = null)
{
    /// <summary>An action with no payload.</summary>
    public static PlaybackAction Of(PlaybackActionKind kind) => new(kind);

    /// <summary>
    /// A <see cref="PlaybackActionKind.FireTrigger"/> action carrying the auto-chain
    /// follow-up trigger the shell must re-enter <see cref="PlaybackProtocol.Advance"/> with.
    /// </summary>
    public static PlaybackAction Fire(PlaybackTrigger followUp) =>
        new(PlaybackActionKind.FireTrigger, followUp);
}

/// <summary>
/// One step of the playback Mealy machine: the destination state paired with the
/// ordered list of side-effect actions the shell must perform for the transition.
/// </summary>
/// <param name="Handled">
/// <see langword="false"/> when the trigger is not permitted from <paramref name="NextState"/>'s
/// source state under the current guard inputs — the shell preserves its existing
/// behavior (drop a stale internal trigger, or fail a user command with
/// <see cref="ErrorCategory.InvalidOperation"/>). When <see langword="false"/>,
/// <see cref="NextState"/> equals the source state and <see cref="Actions"/> is empty.
/// </param>
/// <param name="NextState">The state the machine moves to (equals the source when not handled).</param>
/// <param name="Actions">The ordered effects (source <c>OnExit</c> then destination <c>OnEntry</c>, plus any internal-transition or auto-chain actions).</param>
internal readonly record struct PlaybackDecision(
    bool Handled,
    InternalPlaybackState NextState,
    IReadOnlyList<PlaybackAction> Actions
)
{
    private static readonly IReadOnlyList<PlaybackAction> NoActions = [];

    /// <summary>A handled transition to <paramref name="next"/> with the given ordered actions.</summary>
    public static PlaybackDecision To(InternalPlaybackState next, params PlaybackAction[] actions) =>
        new(Handled: true, NextState: next, Actions: actions.Length == 0 ? NoActions : actions);

    /// <summary>
    /// A handled <i>internal</i> transition: the state does not change, but the
    /// <paramref name="actions"/> still run (the RepeatMode.One loop rewind).
    /// </summary>
    public static PlaybackDecision Internal(
        InternalPlaybackState current,
        params PlaybackAction[] actions
    ) => new(Handled: true, NextState: current, Actions: actions.Length == 0 ? NoActions : actions);

    /// <summary>The trigger is not permitted from <paramref name="current"/>; the shell drops or fails it.</summary>
    public static PlaybackDecision NotHandled(InternalPlaybackState current) =>
        new(Handled: false, NextState: current, Actions: NoActions);
}

/// <summary>
/// The guard inputs the playback transition table reads that are <b>not</b> part of the
/// <c>(state, trigger)</c> pair — the orthogonal repeat region's mode, and whether a
/// live session/source exists. Passed as an immutable value so
/// <see cref="PlaybackProtocol.Advance"/> stays a total function of its arguments with
/// no hidden reads.
/// </summary>
/// <param name="RepeatOne">
/// Whether the repeat sub-machine is in <see cref="RepeatMode.One"/>. Selects the
/// loop-vs-end branch on <see cref="PlaybackTrigger.LastFrameRendered"/> from
/// <see cref="InternalPlaybackState.Playing"/>.
/// </param>
/// <param name="HasSession">
/// Whether a session (and loaded source) currently exists. Gates the replay-from-Ended
/// path — a <see cref="PlaybackTrigger.Play"/> from <see cref="InternalPlaybackState.Ended"/>
/// with no session is an invalid operation, not a replay.
/// </param>
internal readonly record struct PlaybackInputs(bool RepeatOne, bool HasSession = true);

/// <summary>
/// The primary playback state machine expressed as a pure Mealy core:
/// <c>δ : (state, trigger, inputs) → (state', action[])</c>. This is the "sibling pattern
/// one layer up" ADR-0055 names — the 522-LOC transition table at the centre of the repo's
/// churn, lifted out of the Stateless <c>OnEntry</c>/<c>OnExit</c>/<c>InternalTransition</c>
/// callbacks into one total function so the table is asserted from a scripted transcript
/// (see <c>PlaybackProtocolTests</c>) with no FFmpeg, no session, no clock.
/// </summary>
/// <remarks>
/// <para>
/// <b>What stays in the shell.</b> Per ADR-0023 the channel-dispatch shell is unchanged —
/// this purifies only <i>what</i> it dispatches. The core owns no IO, no <c>await</c>, no
/// clock, no <c>Task</c>, and no mutable state across calls; every effect (create/dispose
/// the session, warm/play/pause it, start/stop the ticker, freeze the clock, run the loop
/// rewind, raise observables) is named as a <see cref="PlaybackAction"/> the shell performs.
/// </para>
/// <para>
/// <b>The load-bearing branches the review names (§2.1)</b> are each one cell here:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>loop-vs-end:</b> <c>Playing × LastFrameRendered</c> splits on
/// <see cref="PlaybackInputs.RepeatOne"/> — an internal <see cref="PlaybackActionKind.RunLoopRewind"/>
/// that never leaves <c>Playing</c>, or a transition to <c>Ended</c>.
/// </description></item>
/// <item><description>
/// <b>error routing:</b> <c>FatalError</c> from any non-terminal state routes to
/// <c>Error</c> with <see cref="PlaybackActionKind.DisposeSession"/> + <see cref="PlaybackActionKind.RaiseError"/>.
/// </description></item>
/// <item><description>
/// <b>replay-from-Ended:</b> handled by the shell's higher-level <c>Play</c>-from-<c>Ended</c>
/// recovery (unload + reload + play); the protocol exposes the <c>Ended × Play</c> cell and
/// the <see cref="PlaybackInputs.HasSession"/> guard it consults. See
/// <see cref="PlaybackControllerCore.TryHandleReplayFromEndedAsync"/>.
/// </description></item>
/// <item><description>
/// <b>stale-trigger drop:</b> any unpermitted <c>(state, trigger)</c> returns
/// <see cref="PlaybackDecision.NotHandled"/>; the shell drops a stale internal trigger
/// silently and fails a user command with <see cref="ErrorCategory.InvalidOperation"/>,
/// exactly as before.
/// </description></item>
/// </list>
/// <para>
/// The error trigger is special-cased in the shell: it always fires (it carries exception
/// context that must reach <c>Error</c> regardless of the current state). The core reflects
/// that by handling <c>FatalError</c> from every state that permits it.
/// </para>
/// </remarks>
internal static class PlaybackProtocol
{
    /// <summary>
    /// Compute the transition for <paramref name="trigger"/> from <paramref name="state"/>
    /// under <paramref name="inputs"/>. Total function: every <c>(state, trigger)</c> pair
    /// resolves to a handled transition or <see cref="PlaybackDecision.NotHandled"/>; nothing
    /// throws for an unpermitted pair (that is the shell's stale-trigger/invalid-op surface).
    /// </summary>
    public static PlaybackDecision Advance(
        InternalPlaybackState state,
        PlaybackTrigger trigger,
        PlaybackInputs inputs
    )
    {
        // FatalError is permitted from every non-terminal state and always routes to Error
        // with the same teardown. Handle it uniformly up front so each state's switch below
        // only enumerates its non-error triggers (mirrors the shell special-case where the
        // error trigger fires regardless of current state).
        if (trigger == PlaybackTrigger.FatalError)
            return PermitsFatalError(state)
                ? PlaybackDecision.To(
                    InternalPlaybackState.Error,
                    PlaybackAction.Of(PlaybackActionKind.DisposeSession),
                    PlaybackAction.Of(PlaybackActionKind.RaiseError)
                )
                : PlaybackDecision.NotHandled(state);

        return state switch
        {
            InternalPlaybackState.Idle => trigger switch
            {
                // Load: enter Initializing. The entry creates+initializes the session,
                // captures the media snapshot, then auto-chains HeadersReceived.
                PlaybackTrigger.Load => PlaybackDecision.To(
                    InternalPlaybackState.Initializing,
                    PlaybackAction.Of(PlaybackActionKind.CreateSession),
                    PlaybackAction.Of(PlaybackActionKind.InitializeSession),
                    PlaybackAction.Fire(PlaybackTrigger.HeadersReceived)
                ),
                _ => PlaybackDecision.NotHandled(state),
            },

            InternalPlaybackState.Initializing => trigger switch
            {
                // Preparing entry has no IO — it immediately auto-chains MetadataParsed.
                PlaybackTrigger.HeadersReceived => PlaybackDecision.To(
                    InternalPlaybackState.Preparing,
                    PlaybackAction.Fire(PlaybackTrigger.MetadataParsed)
                ),
                PlaybackTrigger.Unload => ToUnloaded(),
                _ => PlaybackDecision.NotHandled(state),
            },

            InternalPlaybackState.Preparing => trigger switch
            {
                // InitialBuffering entry warms the decoder, then auto-chains BufferReady.
                PlaybackTrigger.MetadataParsed => PlaybackDecision.To(
                    InternalPlaybackState.InitialBuffering,
                    PlaybackAction.Of(PlaybackActionKind.WarmUp),
                    PlaybackAction.Fire(PlaybackTrigger.BufferReady)
                ),
                PlaybackTrigger.Unload => ToUnloaded(),
                _ => PlaybackDecision.NotHandled(state),
            },

            InternalPlaybackState.InitialBuffering => trigger switch
            {
                // BufferReady → Paused: the session hasn't started playing, so no pause
                // is issued (the OnEntryFrom(BufferReady) handler only logs).
                PlaybackTrigger.BufferReady => PlaybackDecision.To(InternalPlaybackState.Paused),
                // Play straight from buffering → Playing (start ticker + play session).
                PlaybackTrigger.Play => ToPlayingFromPlay(),
                PlaybackTrigger.Unload => ToUnloaded(),
                _ => PlaybackDecision.NotHandled(state),
            },

            InternalPlaybackState.Paused => trigger switch
            {
                PlaybackTrigger.Play => ToPlayingFromPlay(),
                PlaybackTrigger.Unload => ToUnloaded(),
                _ => PlaybackDecision.NotHandled(state),
            },

            InternalPlaybackState.Playing => trigger switch
            {
                // loop-vs-end: the marquee branch. RepeatMode.One loops internally
                // (never leaves Playing); otherwise this is end-of-stream.
                PlaybackTrigger.LastFrameRendered => inputs.RepeatOne
                    ? PlaybackDecision.Internal(
                        InternalPlaybackState.Playing,
                        PlaybackAction.Of(PlaybackActionKind.RunLoopRewind)
                    )
                    // Playing OnExit stops the ticker; Ended OnEntry freezes the clock.
                    : PlaybackDecision.To(
                        InternalPlaybackState.Ended,
                        PlaybackAction.Of(PlaybackActionKind.StopTicker),
                        PlaybackAction.Of(PlaybackActionKind.FreezeClock)
                    ),
                // Pause: Playing OnExit stops the ticker; Paused OnEntry(Pause) pauses the session.
                PlaybackTrigger.Pause => PlaybackDecision.To(
                    InternalPlaybackState.Paused,
                    PlaybackAction.Of(PlaybackActionKind.StopTicker),
                    PlaybackAction.Of(PlaybackActionKind.PauseSession)
                ),
                // BufferUnderrun → Rebuffering: ticker stops on exit; Rebuffering entry only logs.
                PlaybackTrigger.BufferUnderrun => PlaybackDecision.To(
                    InternalPlaybackState.Rebuffering,
                    PlaybackAction.Of(PlaybackActionKind.StopTicker)
                ),
                // Unload: ticker stops on exit, then Unloaded entry disposes the session.
                PlaybackTrigger.Unload => PlaybackDecision.To(
                    InternalPlaybackState.Unloaded,
                    PlaybackAction.Of(PlaybackActionKind.StopTicker),
                    PlaybackAction.Of(PlaybackActionKind.DisposeSession)
                ),
                _ => PlaybackDecision.NotHandled(state),
            },

            InternalPlaybackState.Rebuffering => trigger switch
            {
                // BufferReady → Playing: Playing OnEntry starts the ticker. This is NOT
                // the Play trigger, so EnterPlayingFromPlay (play the session) does not run.
                PlaybackTrigger.BufferReady => PlaybackDecision.To(
                    InternalPlaybackState.Playing,
                    PlaybackAction.Of(PlaybackActionKind.StartTicker)
                ),
                PlaybackTrigger.Pause => PlaybackDecision.To(
                    InternalPlaybackState.Paused,
                    PlaybackAction.Of(PlaybackActionKind.PauseSession)
                ),
                PlaybackTrigger.Unload => ToUnloaded(),
                _ => PlaybackDecision.NotHandled(state),
            },

            InternalPlaybackState.Ended => trigger switch
            {
                // Seek out of Ended → InitialBuffering. The InitialBuffering entry warms
                // and auto-chains BufferReady, same as the load path. (The shell routes the
                // Ended-seek through the playback machine, then drives the seek sub-machine.)
                PlaybackTrigger.Seek => PlaybackDecision.To(
                    InternalPlaybackState.InitialBuffering,
                    PlaybackAction.Of(PlaybackActionKind.WarmUp),
                    PlaybackAction.Fire(PlaybackTrigger.BufferReady)
                ),
                // Play from Ended is the replay-from-Ended path (handled by the shell's
                // higher-level recovery when a session exists). With no session it is an
                // invalid operation. The raw Stateless cell here is Ended → Playing.
                PlaybackTrigger.Play => inputs.HasSession
                    ? ToPlayingFromPlay()
                    : PlaybackDecision.NotHandled(state),
                PlaybackTrigger.Unload => ToUnloaded(),
                _ => PlaybackDecision.NotHandled(state),
            },

            InternalPlaybackState.Unloaded => trigger switch
            {
                PlaybackTrigger.Load => PlaybackDecision.To(
                    InternalPlaybackState.Initializing,
                    PlaybackAction.Of(PlaybackActionKind.CreateSession),
                    PlaybackAction.Of(PlaybackActionKind.InitializeSession),
                    PlaybackAction.Fire(PlaybackTrigger.HeadersReceived)
                ),
                PlaybackTrigger.Reset => PlaybackDecision.To(InternalPlaybackState.Idle),
                _ => PlaybackDecision.NotHandled(state),
            },

            InternalPlaybackState.Error => trigger switch
            {
                PlaybackTrigger.Reset => PlaybackDecision.To(InternalPlaybackState.Idle),
                _ => PlaybackDecision.NotHandled(state),
            },

            _ => PlaybackDecision.NotHandled(state),
        };

        // ── Shared cells (states that share an identical destination + action list) ──

        // Unload from a state whose only exit work is "dispose the session" on the
        // Unloaded entry (the source state has no OnExit). Playing's Unload is NOT this —
        // it stops the ticker first — so it is spelled out inline above.
        static PlaybackDecision ToUnloaded() =>
            PlaybackDecision.To(
                InternalPlaybackState.Unloaded,
                PlaybackAction.Of(PlaybackActionKind.DisposeSession)
            );

        // Enter Playing via the Play trigger: Playing OnEntry starts the ticker AND the
        // OnEntryFrom(Play) handler plays the session. Order matches Stateless: the
        // unparameterised OnEntry (StartTicker) runs before the from-trigger OnEntry
        // (PlaySession).
        static PlaybackDecision ToPlayingFromPlay() =>
            PlaybackDecision.To(
                InternalPlaybackState.Playing,
                PlaybackAction.Of(PlaybackActionKind.StartTicker),
                PlaybackAction.Of(PlaybackActionKind.PlaySession)
            );
    }

    /// <summary>
    /// Whether <see cref="PlaybackTrigger.FatalError"/> is permitted from
    /// <paramref name="state"/>. Permitted from the loading substates, the Ready substates,
    /// and <c>Ended</c> — but not <c>Idle</c>, <c>Unloaded</c>, or <c>Error</c> (a fault
    /// there is a stale trigger the shell drops rather than a route to the error state).
    /// </summary>
    private static bool PermitsFatalError(InternalPlaybackState state) =>
        state
            is InternalPlaybackState.Initializing
                or InternalPlaybackState.Preparing
                or InternalPlaybackState.InitialBuffering
                or InternalPlaybackState.Paused
                or InternalPlaybackState.Playing
                or InternalPlaybackState.Rebuffering
                or InternalPlaybackState.Ended;

    /// <summary>
    /// Renders the primary-playback transition table as a Graphviz DOT digraph by folding
    /// every <c>(state, trigger)</c> cell (under both repeat modes and the no-session
    /// guard) through <see cref="Advance"/>. This is the pure-core successor to the
    /// Stateless <c>UmlDotGraph</c> rendering of the retired playback machine — the table
    /// is the source of truth, so the diagram is generated from it rather than from a
    /// parallel configuration. Edge labels carry the trigger and the ordered action kinds;
    /// an internal transition (a self-edge that runs actions without leaving the state) is
    /// drawn dashed.
    /// </summary>
    public static string ToDotGraph()
    {
        var states = Enum.GetValues<InternalPlaybackState>();
        var triggers = Enum.GetValues<PlaybackTrigger>();
        // Cover the guard inputs that change the outcome: repeat-one toggles the
        // Playing × LastFrameRendered loop-vs-end split, no-session gates Ended × Play.
        PlaybackInputs[] inputSets =
        [
            new(RepeatOne: false, HasSession: true),
            new(RepeatOne: true, HasSession: true),
            new(RepeatOne: false, HasSession: false),
        ];

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("digraph PlaybackProtocol {");
        sb.AppendLine("  rankdir=LR;");
        sb.AppendLine("  node [shape=rectangle];");

        var seen = new HashSet<string>();
        foreach (var state in states)
        foreach (var trigger in triggers)
        foreach (var inputs in inputSets)
        {
            var decision = Advance(state, trigger, inputs);
            if (!decision.Handled)
                continue;

            var actionLabel = string.Join(
                ", ",
                decision.Actions.Select(a =>
                    a.Kind == PlaybackActionKind.FireTrigger
                        ? $"FireTrigger({a.FollowUp})"
                        : a.Kind.ToString()
                )
            );
            var label = actionLabel.Length == 0 ? $"{trigger}" : $"{trigger} / {actionLabel}";
            var style = decision.NextState == state ? " style=dashed" : string.Empty;
            var edge = $"  {state} -> {decision.NextState} [label=\"{label}\"{style}];";
            if (seen.Add(edge))
                sb.AppendLine(edge);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }
}
