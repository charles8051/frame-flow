// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Sdl.Tests;

/// <summary>
/// The shell around the command queue's core (#605): where commands run, what it reports, and
/// how closing cancels them. Each test waits on a signal a command raises; <see cref="Bound"/>
/// only stops a broken queue from hanging the suite.
/// </summary>
public sealed class SdlCommandQueueTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_command_runs_on_the_thread_pool_not_on_the_thread_that_posted_it()
    {
        var queue = new SdlCommandQueue(NullLogger.Instance);
        queue.Start();
        var ran = new TaskCompletionSource<(int Thread, bool Pooled)>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        // A thread of its own stands in for the SDL thread: the pool can never hand it a command.
        var postingThread = 0;
        var poster = new Thread(() =>
        {
            postingThread = Environment.CurrentManagedThreadId;
            queue.Post(_ =>
            {
                ran.SetResult(
                    (Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread)
                );
                return Task.CompletedTask;
            });
        });
        poster.Start();
        poster.Join();

        var (thread, pooled) = await ran.Task.WaitAsync(Bound);

        Assert.NotEqual(postingThread, thread);
        Assert.True(pooled);
    }

    [Fact]
    public async Task Commands_run_one_at_a_time_in_the_order_posted()
    {
        var queue = new SdlCommandQueue(NullLogger.Instance);
        var log = new List<string>();
        var gate = new Lock();

        Func<CancellationToken, Task> Command(string name) =>
            async _ =>
            {
                lock (gate)
                    log.Add($"{name} start");
                await Task.Yield();
                lock (gate)
                    log.Add($"{name} end");
            };

        queue.Post(Command("a"));
        queue.Post(Command("b"));
        queue.Start();
        queue.Post(Command("c"));
        await queue.Close().WaitAsync(Bound);

        Assert.Equal(["a start", "a end", "b start", "b end", "c start", "c end"], log);
    }

    [Fact]
    public async Task A_failing_command_is_logged_as_an_error_and_the_next_one_still_runs()
    {
        var logger = new RecordingLogger();
        var queue = new SdlCommandQueue(logger);
        var thrown = new InvalidOperationException("thrown");
        var faulted = new IOException("faulted");
        var ranAfter = false;

        queue.Start();
        queue.Post(_ => throw thrown);
        queue.Post(async _ =>
        {
            await Task.Yield();
            throw faulted;
        });
        queue.Post(_ =>
        {
            ranAfter = true;
            return Task.CompletedTask;
        });
        await queue.Close().WaitAsync(Bound);

        Assert.True(ranAfter);
        Assert.Equal(
            [thrown, faulted],
            logger.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Exception)
        );
    }

    [Fact]
    public async Task Closing_cancels_the_running_command_which_is_not_logged_as_a_failure()
    {
        var logger = new RecordingLogger();
        var queue = new SdlCommandQueue(logger);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.Start();
        queue.Post(async ct =>
        {
            running.SetResult();
            await new TaskCompletionSource().Task.WaitAsync(ct);
        });
        await running.Task.WaitAsync(Bound);

        await queue.Close().WaitAsync(Bound);

        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task Closing_never_runs_a_cancelled_command_on_the_thread_that_closed_the_queue()
    {
        var queue = new SdlCommandQueue(NullLogger.Instance);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedOn = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        queue.Start();
        queue.Post(async ct =>
        {
            // Completed from the token's callback, with no RunContinuationsAsynchronously, so the
            // rest of this command runs on whichever thread runs that callback.
            var cancelled = new TaskCompletionSource();
            using var registration = ct.Register(() => cancelled.TrySetResult());
            waiting.SetResult();
            await cancelled.Task;
            resumedOn.SetResult(Environment.CurrentManagedThreadId);
        });
        await waiting.Task.WaitAsync(Bound);

        // A thread of its own stands in for the SDL thread, which closes the queue as the loop exits.
        var closingThread = 0;
        Task drained = Task.CompletedTask;
        var closer = new Thread(() =>
        {
            closingThread = Environment.CurrentManagedThreadId;
            drained = queue.Close();
        });
        closer.Start();
        closer.Join();

        Assert.NotEqual(closingThread, await resumedOn.Task.WaitAsync(Bound));
        await drained.WaitAsync(Bound);
    }

    [Fact]
    public async Task A_command_may_post_another_which_runs_after_it()
    {
        var queue = new SdlCommandQueue(NullLogger.Instance);
        var log = new List<string>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.Start();
        queue.Post(_ =>
        {
            queue.Post(_ =>
            {
                log.Add("second");
                return Task.CompletedTask;
            });
            log.Add("first");
            first.SetResult();
            return Task.CompletedTask;
        });
        await first.Task.WaitAsync(Bound);
        await queue.Close().WaitAsync(Bound);

        Assert.Equal(["first", "second"], log);
    }

    [Fact]
    public async Task A_post_after_the_queue_closed_is_refused()
    {
        var queue = new SdlCommandQueue(NullLogger.Instance);
        queue.Start();
        var drained = queue.Close();

        Assert.False(queue.Post(_ => Task.CompletedTask));
        await drained.WaitAsync(Bound);
    }

    [Fact]
    public void A_queue_serves_one_loop()
    {
        var queue = new SdlCommandQueue(NullLogger.Instance);
        queue.Start();

        Assert.Throws<InvalidOperationException>(queue.Start);
    }

    [Fact]
    public void A_queue_needs_a_logger_and_a_post_needs_a_command()
    {
        Assert.Throws<ArgumentNullException>(() => new SdlCommandQueue(null!));
        Assert.Throws<ArgumentNullException>(() =>
            new SdlCommandQueue(NullLogger.Instance).Post(null!)
        );
    }
}
