// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Sdl.Core;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Sdl;

/// <summary>
/// Asynchronous work for an <see cref="SdlEventLoop"/> to run off the SDL thread, one command at a
/// time and in the order posted. An SDL event handler posts a player command here and returns,
/// instead of blocking the loop on it or starting it unordered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> Pass the queue to
/// <see cref="SdlEventLoop.Run(SdlApi, SdlVideoSink, SdlCommandQueue, SdlEventLoop.EventHandler?, SdlEventLoop.TickHandler?, TimeSpan?, CancellationToken)"/>,
/// which starts it. Commands posted before then wait for it. When the loop exits, the queue
/// refuses further commands and cancels the token it gave each one, and the commands it already
/// accepted still run, in order. <c>Run</c> returns once the last has finished, so the caller can
/// dispose what the commands used, a player for one, before it destroys the window. A queue serves
/// one loop.
/// </para>
/// <para>
/// <b>What a command may do.</b> A command runs on the thread pool, never on the SDL thread, and
/// must not wait for the loop, which waits for it before returning. It may call anything that does
/// not need the SDL thread: a player's commands and its disposal, file and network IO, and the
/// sink's <see cref="SdlVideoSink.PresentAsync"/>. It must not call SDL, or the
/// <see cref="SdlVideoSink"/> members that run on the SDL thread:
/// <see cref="SdlVideoSink.RenderPendingFrame"/>, <see cref="SdlVideoSink.SetWindowTitle"/> and
/// <see cref="SdlVideoSink.DestroyResources"/>. SDL's window and render calls belong to the thread
/// that runs the loop, which on macOS must be the OS main thread. To show a command's result in
/// the window, store it where the loop's <see cref="SdlEventLoop.TickHandler"/> reads it. A long
/// command should observe its token: <c>Run</c> waits for it after the loop exits, and until it
/// finishes the window drops every event, Quit included.
/// </para>
/// <para>
/// <b>Failures.</b> A command that throws, or whose task faults, is logged as an error, and the
/// next command runs. One that throws <see cref="OperationCanceledException"/> after the loop has
/// exited is logged at debug level: it stopped because the loop asked it to.
/// </para>
/// </remarks>
public sealed partial class SdlCommandQueue
{
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _exit = new();
    private readonly TaskCompletionSource _drained = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private CommandQueue<Func<CancellationToken, Task>> _queue = CommandQueue<
        Func<CancellationToken, Task>
    >.Empty;

    /// <summary>Creates a queue for one <see cref="SdlEventLoop"/>.</summary>
    /// <param name="logger">Where the queue reports commands that fail.</param>
    public SdlCommandQueue(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Queues <paramref name="command"/> to run after the commands posted before it. It never
    /// waits for a command and never runs one on the calling thread, so an SDL event handler can
    /// call it. A command may post another.
    /// </summary>
    /// <param name="command">
    /// The work. Its token is cancelled when the loop exits, so a long command can stop early.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the command will run; <see langword="false"/> once the loop has
    /// exited, when it will not.
    /// </returns>
    public bool Post(Func<CancellationToken, Task> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        CommandStep<Func<CancellationToken, Task>> step;
        lock (_gate)
        {
            step = _queue.Post(command);
            _queue = step.Queue;
        }
        Act(step);
        return step.Signal != CommandSignal.Rejected;
    }

    /// <summary>Starts the queue for the loop: commands run from now on.</summary>
    /// <exception cref="InvalidOperationException">The queue has already served a loop.</exception>
    internal void Start()
    {
        CommandStep<Func<CancellationToken, Task>> step;
        lock (_gate)
        {
            step = _queue.Start();
            _queue = step.Queue;
        }
        if (step.Signal == CommandSignal.Rejected)
        {
            throw new InvalidOperationException(
                "This SdlCommandQueue has already served an event loop. Create a new queue for each call to SdlEventLoop.Run."
            );
        }
        Act(step);
    }

    /// <summary>
    /// Closes the queue as the loop exits: later posts are refused, the commands' token is
    /// cancelled, and the commands already accepted still run.
    /// </summary>
    /// <returns>A task that completes once every accepted command has finished.</returns>
    internal Task Close()
    {
        CommandStep<Func<CancellationToken, Task>> step;
        lock (_gate)
        {
            step = _queue.Close();
            _queue = step.Queue;
        }

        // Cancel runs the token's callbacks, and the continuations of commands waiting on it, on
        // the calling thread, which is the SDL thread. CancelAsync marks the token cancelled now
        // and runs them on the thread pool.
        _ = _exit.CancelAsync();

        Act(step);
        return _drained.Task;
    }

    private void Act(CommandStep<Func<CancellationToken, Task>> step)
    {
        switch (step.Signal)
        {
            case CommandSignal.Start:
                var command = step.Command!;
                _ = Task.Run(() => ExecuteAsync(command));
                break;
            case CommandSignal.Drained:
                _drained.TrySetResult();
                break;
        }
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> command)
    {
        try
        {
            Exception? error = null;
            try
            {
                await command(_exit.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Reported below. A command's failure must not stop the commands behind it.
                error = ex;
            }

            switch (CommandOutcomes.Classify(error, _exit.IsCancellationRequested))
            {
                case CommandOutcome.Failed:
                    LogCommandFailed(_logger, error!);
                    break;
                case CommandOutcome.Canceled:
                    LogCommandCanceled(_logger);
                    break;
            }
        }
        finally
        {
            CommandStep<Func<CancellationToken, Task>> step;
            lock (_gate)
            {
                step = _queue.Finish();
                _queue = step.Queue;
            }
            Act(step);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "An SDL event loop command failed")]
    private static partial void LogCommandFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "An SDL event loop command stopped: the loop exited and cancelled it"
    )]
    private static partial void LogCommandCanceled(ILogger logger);
}
