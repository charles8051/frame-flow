// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Player.Core;

/// <summary>
/// The pure core of <see cref="MediaTransportExtensions.WaitForEndAsync"/>: a fold over what a
/// transport reports, which decides once whether playback ended, failed, or can no longer end.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it folds.</b> The state the transport was in once the wait had subscribed, each state
/// it moves to, each error it raises with the state it was in at that moment, and the end of its
/// streams. The shell reads the transport and feeds the readings in; everything it decides is
/// here, so the ordering cases are tested without a player, a thread or a clock.
/// </para>
/// <para>
/// <b>Why an error carries the state it arrived in.</b> The controller flips to
/// <see cref="PlaybackState.Error"/> first and raises <c>ErrorOccurred</c> after. A wait that
/// subscribed between the two never sees the state change, only the error, and the state read
/// alongside the error is what tells a fatal error from an item failure the player carries on
/// from.
/// </para>
/// <para>
/// Decided once: the first outcome stands and every later input is ignored.
/// </para>
/// </remarks>
internal readonly record struct PlaybackEndWatch
{
    private PlaybackEndWatch(Result? outcome, bool inError)
    {
        Outcome = outcome;
        InError = inError;
    }

    /// <summary>The result the wait returns, or null while it is still waiting.</summary>
    public Result? Outcome { get; }

    /// <summary>Whether the transport has been seen entering <see cref="PlaybackState.Error"/>.</summary>
    public bool InError { get; }

    /// <summary>A wait that has decided nothing.</summary>
    public static PlaybackEndWatch Waiting => default;

    /// <summary>The failure for a player disposed before playback ended.</summary>
    public static PlaybackError Disposed { get; } =
        new(ErrorCategory.InvalidOperation, "The player was disposed before playback ended.");

    /// <summary>
    /// The failure for a player already in <see cref="PlaybackState.Error"/> when the wait began.
    /// Its error was raised on <c>ErrorOccurred</c> before then and is not replayed.
    /// </summary>
    public static PlaybackError AlreadyFailed { get; } =
        new(
            ErrorCategory.InvalidOperation,
            "The player was already in Error when the wait began. The error that put it there was "
                + "raised on ErrorOccurred before the wait subscribed."
        );

    /// <summary>The failure for a player disposed after it entered Error and before it said why.</summary>
    public static PlaybackError FailedWithoutDetail { get; } =
        new(
            ErrorCategory.InvalidOperation,
            "The player entered Error and was disposed before it raised the error."
        );

    /// <summary>The state the transport was in once the wait had subscribed.</summary>
    public PlaybackEndWatch Started(PlaybackState current) =>
        current switch
        {
            PlaybackState.Ended => Decide(Result.Ok()),
            PlaybackState.Error => Decide(Result.Fail(AlreadyFailed)),
            _ => this,
        };

    /// <summary>A state the transport moved to.</summary>
    public PlaybackEndWatch StateChanged(PlaybackState state) =>
        state switch
        {
            PlaybackState.Ended => Decide(Result.Ok()),
            PlaybackState.Error => Outcome is null ? new PlaybackEndWatch(null, inError: true) : this,
            _ => this,
        };

    /// <summary>
    /// An error the transport raised, with the state it was in when it did. An error raised in
    /// <see cref="PlaybackState.Error"/> is the one that ended playback. Any other is an item
    /// failure the player carried on from, and the wait goes on.
    /// </summary>
    public PlaybackEndWatch ErrorRaised(PlaybackError error, PlaybackState stateNow) =>
        InError || stateNow == PlaybackState.Error ? Decide(Result.Fail(error)) : this;

    /// <summary>The transport's streams completed, which they do when it is disposed.</summary>
    public PlaybackEndWatch Completed() =>
        Decide(Result.Fail(InError ? FailedWithoutDetail : Disposed));

    /// <summary>One of the transport's streams faulted, so the end of playback cannot be observed.</summary>
    public PlaybackEndWatch Faulted(Exception error) =>
        Decide(
            Result.Fail(
                ErrorCategory.System,
                "The player's event stream failed, so the end of playback cannot be observed.",
                error
            )
        );

    private PlaybackEndWatch Decide(Result outcome) =>
        Outcome is null ? new PlaybackEndWatch(outcome, InError) : this;
}
