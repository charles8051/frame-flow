// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Sdl.Core;

namespace FrameFlow.Sdl.Tests;

/// <summary>
/// The order <see cref="SdlCommandQueue"/> runs commands in, and when it has drained (#605), as
/// pure transitions over strings standing in for commands.
/// </summary>
public sealed class CommandQueueTests
{
    private static CommandQueue<string> Started => CommandQueue<string>.Empty.Start().Queue;

    [Fact]
    public void A_command_posted_before_the_loop_starts_waits_for_it()
    {
        var posted = CommandQueue<string>.Empty.Post("a");

        Assert.Equal(CommandSignal.None, posted.Signal);
        Assert.Equal(["a"], posted.Queue.Waiting);

        var started = posted.Queue.Start();

        Assert.Equal(CommandSignal.Start, started.Signal);
        Assert.Equal("a", started.Command);
        Assert.True(started.Queue.Busy);
        Assert.Empty(started.Queue.Waiting);
    }

    [Fact]
    public void A_command_posted_to_an_idle_started_queue_starts_now()
    {
        var posted = Started.Post("a");

        Assert.Equal(CommandSignal.Start, posted.Signal);
        Assert.Equal("a", posted.Command);
        Assert.True(posted.Queue.Busy);
    }

    [Fact]
    public void A_command_posted_while_one_runs_waits_and_each_starts_when_the_one_before_finishes()
    {
        var a = Started.Post("a");
        var b = a.Queue.Post("b");
        var c = b.Queue.Post("c");

        Assert.Equal(CommandSignal.None, b.Signal);
        Assert.Equal(CommandSignal.None, c.Signal);
        Assert.Equal(["b", "c"], c.Queue.Waiting);

        var afterA = c.Queue.Finish();
        Assert.Equal((CommandSignal.Start, "b"), (afterA.Signal, afterA.Command));

        var afterB = afterA.Queue.Finish();
        Assert.Equal((CommandSignal.Start, "c"), (afterB.Signal, afterB.Command));

        var afterC = afterB.Queue.Finish();
        Assert.Equal(CommandSignal.None, afterC.Signal);
        Assert.False(afterC.Queue.Busy);
        Assert.False(afterC.Queue.IsDrained);
    }

    [Fact]
    public void Closing_an_idle_queue_drains_it_at_once()
    {
        var closed = Started.Close();

        Assert.Equal(CommandSignal.Drained, closed.Signal);
        Assert.True(closed.Queue.IsDrained);
    }

    [Fact]
    public void Closing_while_a_command_runs_drains_when_it_finishes()
    {
        var closed = Started.Post("a").Queue.Close();

        Assert.Equal(CommandSignal.None, closed.Signal);
        Assert.False(closed.Queue.IsDrained);

        var finished = closed.Queue.Finish();

        Assert.Equal(CommandSignal.Drained, finished.Signal);
        Assert.True(finished.Queue.IsDrained);
    }

    [Fact]
    public void Closing_keeps_the_waiting_commands_and_runs_them_in_order_before_it_drains()
    {
        var closed = Started.Post("a").Queue.Post("b").Queue.Post("c").Queue.Close();

        Assert.Equal(CommandSignal.None, closed.Signal);
        Assert.Equal(["b", "c"], closed.Queue.Waiting);

        var afterA = closed.Queue.Finish();
        Assert.Equal((CommandSignal.Start, "b"), (afterA.Signal, afterA.Command));

        var afterB = afterA.Queue.Finish();
        Assert.Equal((CommandSignal.Start, "c"), (afterB.Signal, afterB.Command));

        var afterC = afterB.Queue.Finish();
        Assert.Equal(CommandSignal.Drained, afterC.Signal);
    }

    [Fact]
    public void A_closed_queue_refuses_a_post_and_is_unchanged()
    {
        var closed = Started.Post("a").Queue.Close().Queue;

        var refused = closed.Post("b");

        Assert.Equal(CommandSignal.Rejected, refused.Signal);
        Assert.Null(refused.Command);
        Assert.Same(closed, refused.Queue);
    }

    [Fact]
    public void A_queue_serves_one_loop()
    {
        Assert.Equal(CommandSignal.Rejected, Started.Start().Signal);
        Assert.Equal(CommandSignal.Rejected, Started.Close().Queue.Start().Signal);
    }

    [Fact]
    public void Closing_a_queue_that_never_started_still_runs_what_it_accepted()
    {
        var closed = CommandQueue<string>.Empty.Post("a").Queue.Close();

        Assert.Equal((CommandSignal.Start, "a"), (closed.Signal, closed.Command));
        Assert.Equal(CommandSignal.Drained, closed.Queue.Finish().Signal);
    }

    [Fact]
    public void Closing_again_is_a_no_op_so_the_queue_drains_once()
    {
        var drained = Started.Close().Queue;

        var again = drained.Close();

        Assert.Equal(CommandSignal.None, again.Signal);
        Assert.Same(drained, again.Queue);
    }

    [Fact]
    public void A_finish_with_nothing_running_is_a_no_op()
    {
        var idle = Started;

        var finished = idle.Finish();

        Assert.Equal(CommandSignal.None, finished.Signal);
        Assert.Same(idle, finished.Queue);
    }

    /// <summary>
    /// Random interleavings of posts, finishes, the start and the close, driven as the shell drives
    /// the queue: every accepted command starts exactly once and in the order posted, no command
    /// starts while another runs, and the queue drains exactly once, after the last command.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Every_accepted_command_starts_once_in_order_and_the_queue_drains_once(int seed)
    {
        var random = new Random(seed);
        var queue = CommandQueue<string>.Empty;
        var accepted = new List<string>();
        var started = new List<string>();
        var running = false;
        var drains = 0;
        var next = 0;

        void Apply(CommandStep<string> step)
        {
            queue = step.Queue;
            switch (step.Signal)
            {
                case CommandSignal.Start:
                    Assert.False(running, "a command started while another was running");
                    Assert.Equal(0, drains);
                    running = true;
                    started.Add(step.Command!);
                    break;
                case CommandSignal.Drained:
                    Assert.False(running, "the queue drained while a command was running");
                    drains++;
                    break;
            }
        }

        for (var i = 0; i < 200; i++)
        {
            switch (random.Next(10))
            {
                case < 5:
                    var command = $"c{next++}";
                    var posted = queue.Post(command);
                    if (posted.Signal != CommandSignal.Rejected)
                        accepted.Add(command);
                    else
                        Assert.True(queue.Closed);
                    Apply(posted);
                    break;
                case < 8:
                    if (running)
                    {
                        running = false;
                        Apply(queue.Finish());
                    }
                    break;
                case 8:
                    if (!queue.Started)
                        Apply(queue.Start());
                    break;
                default:
                    if (random.Next(10) == 0)
                        Apply(queue.Close());
                    break;
            }
        }

        Apply(queue.Close());
        while (running)
        {
            running = false;
            Apply(queue.Finish());
        }

        Assert.Equal(accepted, started);
        Assert.Equal(1, drains);
        Assert.True(queue.IsDrained);
    }

    [Fact]
    public void A_command_that_returns_completed()
    {
        Assert.Equal(CommandOutcome.Completed, CommandOutcomes.Classify(null, loopExited: false));
        Assert.Equal(CommandOutcome.Completed, CommandOutcomes.Classify(null, loopExited: true));
    }

    [Fact]
    public void A_command_that_throws_failed_whether_or_not_the_loop_exited()
    {
        var error = new InvalidOperationException();

        Assert.Equal(CommandOutcome.Failed, CommandOutcomes.Classify(error, loopExited: false));
        Assert.Equal(CommandOutcome.Failed, CommandOutcomes.Classify(error, loopExited: true));
    }

    [Fact]
    public void A_cancellation_after_the_loop_exited_is_a_cancellation()
    {
        Assert.Equal(
            CommandOutcome.Canceled,
            CommandOutcomes.Classify(new OperationCanceledException(), loopExited: true)
        );
        Assert.Equal(
            CommandOutcome.Canceled,
            CommandOutcomes.Classify(new TaskCanceledException(), loopExited: true)
        );
    }

    [Fact]
    public void A_cancellation_the_loop_did_not_ask_for_is_a_failure()
    {
        Assert.Equal(
            CommandOutcome.Failed,
            CommandOutcomes.Classify(new OperationCanceledException(), loopExited: false)
        );
    }
}
