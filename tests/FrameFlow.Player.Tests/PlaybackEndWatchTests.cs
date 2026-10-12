// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Player.Core;

namespace FrameFlow.Player.Tests;

/// <summary>
/// What <see cref="MediaTransportExtensions.WaitForEndAsync"/> decides from what a transport
/// reports, with no transport (#610).
/// </summary>
public sealed class PlaybackEndWatchTests
{
    private static readonly PlaybackError Fatal = new(ErrorCategory.Decode, "The decoder gave up.");
    private static readonly PlaybackError ItemFailure = new(ErrorCategory.Io, "One item would not open.");

    [Fact]
    public void Ended_IsSuccess()
    {
        var watch = PlaybackEndWatch.Waiting.Started(PlaybackState.Playing).StateChanged(PlaybackState.Ended);

        Assert.Equal(Result.Ok(), watch.Outcome);
    }

    [Fact]
    public void AlreadyEnded_IsSuccessAtOnce()
    {
        Assert.Equal(Result.Ok(), PlaybackEndWatch.Waiting.Started(PlaybackState.Ended).Outcome);
    }

    [Theory]
    [InlineData(PlaybackState.Idle)]
    [InlineData(PlaybackState.Loading)]
    [InlineData(PlaybackState.Paused)]
    [InlineData(PlaybackState.Playing)]
    [InlineData(PlaybackState.Rebuffering)]
    [InlineData(PlaybackState.Unloaded)]
    public void AnyOtherState_KeepsWaiting(PlaybackState state)
    {
        var watch = PlaybackEndWatch.Waiting.Started(state).StateChanged(state);

        Assert.Null(watch.Outcome);
    }

    [Fact]
    public void Error_FailsWithTheErrorThePlayerRaisesAfterIt()
    {
        // The controller flips the state first and raises the error after.
        var inError = PlaybackEndWatch.Waiting.Started(PlaybackState.Playing).StateChanged(PlaybackState.Error);
        Assert.Null(inError.Outcome);

        var watch = inError.ErrorRaised(Fatal, PlaybackState.Error);

        Assert.False(watch.Outcome!.Value.IsSuccess);
        Assert.Same(Fatal, watch.Outcome.Value.Error);
    }

    [Fact]
    public void AnErrorRaisedInError_FailsEvenWhenTheStateChangeWasMissed()
    {
        // Subscribed after the flip and before the raise: the state change never arrives, the
        // error does, and the state read alongside it says it is the fatal one.
        var watch = PlaybackEndWatch.Waiting.ErrorRaised(Fatal, PlaybackState.Error);

        Assert.Same(Fatal, watch.Outcome!.Value.Error);
    }

    [Fact]
    public void AnItemFailureThePlayerCarriesOnFrom_KeepsWaiting()
    {
        var watch = PlaybackEndWatch.Waiting
            .Started(PlaybackState.Playing)
            .ErrorRaised(ItemFailure, PlaybackState.Playing);

        Assert.Null(watch.Outcome);
        Assert.Equal(Result.Ok(), watch.StateChanged(PlaybackState.Ended).Outcome);
    }

    [Fact]
    public void AlreadyInError_FailsAtOnce_WithoutTheErrorItCannotReplay()
    {
        var watch = PlaybackEndWatch.Waiting.Started(PlaybackState.Error);

        Assert.Same(PlaybackEndWatch.AlreadyFailed, watch.Outcome!.Value.Error);
    }

    [Fact]
    public void AMoveIntoErrorSeenBeforeTheStateWasRead_WaitsForTheError()
    {
        // Whatever order the readings come in, a wait that saw the move into Error on the stream
        // knows the error follows it, so it will arrive too, and is the one to report.
        var watch = PlaybackEndWatch.Waiting.StateChanged(PlaybackState.Error).Started(PlaybackState.Error);
        Assert.Null(watch.Outcome);

        Assert.Same(Fatal, watch.ErrorRaised(Fatal, PlaybackState.Error).Outcome!.Value.Error);
    }

    [Fact]
    public void AMoveIntoErrorBetweenTheTwoSubscriptions_WaitsForTheError()
    {
        // The first read, with errors already watched, found the player playing. The move into
        // Error came after it, so the error that follows the move comes to this wait.
        var watch = PlaybackEndWatch.Waiting.Started(PlaybackState.Playing).Watching(PlaybackState.Error);
        Assert.Null(watch.Outcome);

        Assert.Same(Fatal, watch.ErrorRaised(Fatal, PlaybackState.Error).Outcome!.Value.Error);
    }

    [Fact]
    public void AnEndBetweenTheTwoSubscriptions_IsSuccess()
    {
        var watch = PlaybackEndWatch.Waiting.Started(PlaybackState.Playing).Watching(PlaybackState.Ended);

        Assert.Equal(Result.Ok(), watch.Outcome);
    }

    [Fact]
    public void InErrorAtTheFirstRead_FailsAtOnce_WhateverTheSecondRead()
    {
        // The first read is the one that can say the error may already have gone by.
        var watch = PlaybackEndWatch.Waiting.Started(PlaybackState.Error).Watching(PlaybackState.Error);

        Assert.Same(PlaybackEndWatch.AlreadyFailed, watch.Outcome!.Value.Error);
    }

    [Fact]
    public void Disposal_Fails()
    {
        var watch = PlaybackEndWatch.Waiting.Started(PlaybackState.Playing).Completed();

        Assert.Same(PlaybackEndWatch.Disposed, watch.Outcome!.Value.Error);
        Assert.Equal(ErrorCategory.InvalidOperation, watch.Outcome.Value.Error!.Category);
    }

    [Fact]
    public void DisposalInErrorBeforeTheErrorArrived_FailsAndSaysSo()
    {
        var watch = PlaybackEndWatch.Waiting.StateChanged(PlaybackState.Error).Completed();

        Assert.Same(PlaybackEndWatch.FailedWithoutDetail, watch.Outcome!.Value.Error);
    }

    [Fact]
    public void AFaultedStream_Fails_CarryingTheFault()
    {
        var fault = new InvalidOperationException("stream broke");

        var watch = PlaybackEndWatch.Waiting.Faulted(fault);

        Assert.Same(fault, watch.Outcome!.Value.Error!.Inner);
    }

    [Fact]
    public void TheFirstOutcomeStands()
    {
        var ended = PlaybackEndWatch.Waiting.StateChanged(PlaybackState.Ended);

        // Disposal after the end, or an error after it, does not rewrite what happened.
        Assert.Equal(Result.Ok(), ended.Completed().Outcome);
        Assert.Equal(Result.Ok(), ended.StateChanged(PlaybackState.Error).ErrorRaised(Fatal, PlaybackState.Error).Outcome);

        var failed = PlaybackEndWatch.Waiting.ErrorRaised(Fatal, PlaybackState.Error);
        Assert.Same(Fatal, failed.StateChanged(PlaybackState.Ended).Completed().Outcome!.Value.Error);
    }
}
