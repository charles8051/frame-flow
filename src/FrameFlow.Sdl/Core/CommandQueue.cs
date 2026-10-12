// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace FrameFlow.Sdl.Core;

/// <summary>
/// The order an <see cref="SdlCommandQueue"/> runs its commands in, and when it has drained
/// (#605). Pure: each transition returns the next queue and what the shell must do.
/// </summary>
/// <remarks>
/// <para>
/// One command runs at a time, in the order they were posted. A command posted before the loop
/// starts the queue waits for it. Closing the queue refuses later posts and keeps everything it
/// already accepted: those commands still run, in order, and the queue has drained when the last
/// of them has finished. So every accepted command starts exactly once, and
/// <see cref="CommandSignal.Drained"/> is reported exactly once.
/// </para>
/// </remarks>
/// <typeparam name="T">The command.</typeparam>
internal sealed class CommandQueue<T>
    where T : class
{
    private CommandQueue(ImmutableQueue<T> waiting, bool started, bool busy, bool closed)
    {
        Waiting = waiting;
        Started = started;
        Busy = busy;
        Closed = closed;
    }

    /// <summary>A queue that has accepted nothing and has not been started.</summary>
    public static CommandQueue<T> Empty { get; } =
        new(ImmutableQueue<T>.Empty, started: false, busy: false, closed: false);

    /// <summary>The accepted commands that have not started, oldest first.</summary>
    public ImmutableQueue<T> Waiting { get; }

    /// <summary>Whether the loop has started the queue, so commands may run.</summary>
    public bool Started { get; }

    /// <summary>Whether a command is running.</summary>
    public bool Busy { get; }

    /// <summary>Whether the queue refuses new commands.</summary>
    public bool Closed { get; }

    /// <summary>Whether the queue is closed and every command it accepted has finished.</summary>
    public bool IsDrained => Closed && !Busy && Waiting.IsEmpty;

    /// <summary>
    /// Accepts <paramref name="command"/>. It starts now when the queue is started and idle, and
    /// otherwise waits behind the commands before it. A closed queue refuses it.
    /// </summary>
    public CommandStep<T> Post(T command)
    {
        if (Closed)
            return new(this, CommandSignal.Rejected, null);
        if (Started && !Busy)
            return new(With(busy: true), CommandSignal.Start, command);
        return new(With(waiting: Waiting.Enqueue(command)), CommandSignal.None, null);
    }

    /// <summary>
    /// Starts the queue: the oldest waiting command starts now. A queue that was already started,
    /// or closed, refuses: a queue serves one loop.
    /// </summary>
    public CommandStep<T> Start()
    {
        if (Started)
            return new(this, CommandSignal.Rejected, null);
        return With(started: true).StartNext();
    }

    /// <summary>
    /// Records that the running command finished: the next waiting command starts, or the queue
    /// goes idle, or, closed with nothing left, it has drained. With no command running it is a
    /// no-op.
    /// </summary>
    public CommandStep<T> Finish()
    {
        if (!Busy)
            return new(this, CommandSignal.None, null);
        return With(busy: false).StartNext();
    }

    /// <summary>
    /// Closes the queue: later posts are refused, and the commands already accepted still run. It
    /// has drained now if nothing is running or waiting. Closing a queue that was never started
    /// starts it, so what it accepted still runs. Closing it again is a no-op.
    /// </summary>
    public CommandStep<T> Close()
    {
        if (Closed)
            return new(this, CommandSignal.None, null);
        var closed = With(started: true, closed: true);
        return closed.Busy ? new(closed, CommandSignal.None, null) : closed.StartNext();
    }

    // Called only when no command is running and the queue is started.
    private CommandStep<T> StartNext()
    {
        if (!Waiting.IsEmpty)
        {
            var waiting = Waiting.Dequeue(out var next);
            return new(With(waiting: waiting, busy: true), CommandSignal.Start, next);
        }
        return Closed
            ? new(this, CommandSignal.Drained, null)
            : new(this, CommandSignal.None, null);
    }

    private CommandQueue<T> With(
        ImmutableQueue<T>? waiting = null,
        bool? started = null,
        bool? busy = null,
        bool? closed = null
    ) => new(waiting ?? Waiting, started ?? Started, busy ?? Busy, closed ?? Closed);
}

/// <summary>What the shell does after a <see cref="CommandQueue{T}"/> transition.</summary>
internal enum CommandSignal
{
    /// <summary>Nothing: the command waits its turn, or the queue is idle.</summary>
    None,

    /// <summary>Run <see cref="CommandStep{T}.Command"/> now.</summary>
    Start,

    /// <summary>The queue is closed and every command it accepted has finished.</summary>
    Drained,

    /// <summary>The queue refused: a post after it closed, or a second start.</summary>
    Rejected,
}

/// <summary>The queue after a transition, and what the shell does next.</summary>
/// <param name="Queue">The queue after the transition.</param>
/// <param name="Signal">What the shell does next.</param>
/// <param name="Command">The command to run when <paramref name="Signal"/> is <see cref="CommandSignal.Start"/>.</param>
internal readonly record struct CommandStep<T>(CommandQueue<T> Queue, CommandSignal Signal, T? Command)
    where T : class;

/// <summary>How a command ended, which decides how the queue reports it.</summary>
internal enum CommandOutcome
{
    /// <summary>It ran to completion.</summary>
    Completed,

    /// <summary>It stopped on the token the queue cancelled when the loop exited.</summary>
    Canceled,

    /// <summary>It threw, or its task faulted.</summary>
    Failed,
}

/// <summary>Classifies how a command ended. Pure.</summary>
internal static class CommandOutcomes
{
    /// <summary>
    /// How a command ended, from what it threw and whether the loop had exited. An
    /// <see cref="OperationCanceledException"/> is a cancellation only once the loop has exited
    /// and cancelled the command's token; before then nothing the queue did cancelled it, so it is
    /// a failure like any other exception.
    /// </summary>
    /// <param name="error">What the command threw, or null when it completed.</param>
    /// <param name="loopExited">Whether the loop had exited and cancelled the commands' token.</param>
    public static CommandOutcome Classify(Exception? error, bool loopExited) =>
        error switch
        {
            null => CommandOutcome.Completed,
            OperationCanceledException when loopExited => CommandOutcome.Canceled,
            _ => CommandOutcome.Failed,
        };
}
