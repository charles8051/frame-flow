// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media.Tests;

/// <summary>
/// Pins <see cref="LinkedCancellation"/>: it cancels as a linked source does, and its dispose does
/// not wait for a link callback that another thread is still running (#785). The canceller is held
/// inside the callback chain by a gate the test opens, so the dispose either returns while the
/// canceller is held or does not return at all.
/// </summary>
public sealed class LinkedCancellationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public void AnyLinkedToken_CancelsIt()
    {
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        using var linked = LinkedCancellation.Create(first.Token, second.Token);

        Assert.False(linked.IsCancellationRequested);
        second.Cancel();

        Assert.True(linked.IsCancellationRequested);
        Assert.True(linked.Token.IsCancellationRequested);
    }

    [Fact]
    public void ATokenAlreadyCancelled_CancelsItAtOnce()
    {
        using var linked = LinkedCancellation.Create(new CancellationToken(canceled: true), CancellationToken.None);

        Assert.True(linked.IsCancellationRequested);
    }

    [Fact]
    public void TokensThatCannotCancel_LeaveItUncancelled()
    {
        using var linked = LinkedCancellation.Create(CancellationToken.None, default);

        Assert.False(linked.IsCancellationRequested);
        Assert.True(linked.Token.CanBeCanceled);
    }

    [Fact]
    public void OnceDisposed_ALinkedTokenNoLongerReachesIt()
    {
        using var parent = new CancellationTokenSource();
        var linked = LinkedCancellation.Create(parent.Token);
        var token = linked.Token;

        linked.Dispose();
        parent.Cancel();

        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public async Task ItsDispose_DoesNotWaitForALinkCallbackAnotherThreadIsRunning()
    {
        using var parent = new CancellationTokenSource();
        var linked = LinkedCancellation.Create(parent.Token);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Runs inside the link's callback, on the thread that cancels the parent, and keeps that
        // thread there until the test opens the gate.
        linked.Token.Register(() =>
        {
            entered.TrySetResult();
            release.Wait();
        });

        var cancelling = Task.Run(parent.Cancel);
        try
        {
            await entered.Task.WaitAsync(Bound);

            // The bound only turns a dispose that waits for the held callback into a failure.
            await Task.Run(linked.Dispose).WaitAsync(Bound);
        }
        finally
        {
            release.Set();
        }

        await cancelling.WaitAsync(Bound);
        Assert.True(linked.Token.IsCancellationRequested);
    }
}
