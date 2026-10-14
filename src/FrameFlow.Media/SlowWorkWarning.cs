// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Says once that a wait for work has gone on past an interval (ADR-0086, decision 3.3). A dispose
/// that waits for work which has not ended logs what that work is, so a stuck dispose names its
/// cause instead of hanging without a word.
/// </summary>
internal static class SlowWorkWarning
{
    /// <summary>How long a dispose waits for its work before it says what it is waiting for.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Waits for <paramref name="work"/>, and calls <paramref name="warn"/> once if it has not ended
    /// <paramref name="interval"/> after the call. Completes as <paramref name="work"/> does.
    /// </summary>
    /// <param name="work">The work waited for.</param>
    /// <param name="interval">How long to wait before warning.</param>
    /// <param name="timeProvider">Whose timer measures the interval, and runs <paramref name="warn"/>.</param>
    /// <param name="warn">
    /// Logs what has not ended. Runs on a timer of <paramref name="timeProvider"/>, at most once, and
    /// never after <paramref name="work"/> has been seen to end. What it throws is dropped: a warning
    /// that fails must not take the process down from a timer thread.
    /// </param>
    public static async Task AwaitAsync(
        Task work,
        TimeSpan interval,
        TimeProvider timeProvider,
        Action warn
    )
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(warn);

        if (work.IsCompleted)
        {
            await work.ConfigureAwait(false);
            return;
        }

        // One shot: no period, so it fires once or not at all.
        var timer = timeProvider.CreateTimer(
            static state =>
            {
                var (waitedFor, say) = ((Task, Action))state!;
                if (waitedFor.IsCompleted)
                    return;
                try
                {
                    say();
                }
                catch
                {
                    // Dropped, as the parameter says.
                }
            },
            (work, warn),
            interval,
            Timeout.InfiniteTimeSpan
        );

        try
        {
            await work.ConfigureAwait(false);
        }
        finally
        {
            // Waits for a warning already running, so none is raised once this has returned.
            await timer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
