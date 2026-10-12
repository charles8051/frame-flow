// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.SDL;

namespace FrameFlow.Sdl.Tests;

/// <summary>
/// <see cref="SdlEventLoop"/> over a fake pump, so it runs with no display (#605). The loop runs on
/// a pool thread of its own, standing in for the SDL thread. <see cref="Bound"/> only stops a
/// broken loop from hanging the suite.
/// </summary>
public sealed class SdlEventLoopTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // The loop polls SDL until it has nothing to read, so after a Quit it polls once more and
    // exits. A later empty poll is one the loop makes only while it waits for its commands.
    private const int APollMadeOnlyWhileDraining = 3;

    [Fact]
    public async Task Run_returns_only_after_the_commands_it_accepted_have_finished()
    {
        var pump = new FakePump(EventType.Quit);
        var commands = new SdlCommandQueue(NullLogger.Instance);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;

        commands.Post(async _ =>
        {
            await gate.Task;
            Volatile.Write(ref finished, true);
        });
        pump.OnEmptyPoll = count =>
        {
            if (count == APollMadeOnlyWhileDraining)
                gate.TrySetResult();
        };

        var exit = await RunAsync(pump, commands);

        Assert.Equal(0, exit);
        Assert.True(Volatile.Read(ref finished));
    }

    [Fact]
    public async Task A_handler_that_throws_still_waits_for_the_commands()
    {
        var pump = new FakePump(EventType.Keydown);
        var commands = new SdlCommandQueue(NullLogger.Instance);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;

        commands.Post(async _ =>
        {
            await gate.Task;
            Volatile.Write(ref finished, true);
        });
        // The handler throws on the first event, so every empty poll is one made while draining.
        pump.OnEmptyPoll = count =>
        {
            if (count == 2)
                gate.TrySetResult();
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(pump, commands, onEvent: (ref Event _) => throw new InvalidOperationException())
        );
        Assert.True(Volatile.Read(ref finished));
    }

    [Fact]
    public async Task While_it_waits_for_the_commands_the_loop_drops_the_events_it_polls()
    {
        var pump = new FakePump(EventType.Quit);
        var commands = new SdlCommandQueue(NullLogger.Instance);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = new List<EventType>();

        commands.Post(_ => gate.Task);
        pump.OnEmptyPoll = count =>
        {
            // A file dropped on the window after Quit, then the gate a few polls later, so the
            // loop has polled the drop before it can return.
            if (count == APollMadeOnlyWhileDraining)
                pump.Enqueue(EventType.Dropfile);
            if (count == APollMadeOnlyWhileDraining + 2)
                gate.TrySetResult();
        };

        await RunAsync(
            pump,
            commands,
            onEvent: (ref Event e) =>
            {
                handled.Add((EventType)e.Type);
                return true;
            }
        );

        Assert.Empty(handled);
        Assert.Equal([EventType.Dropfile], pump.Discarded);
    }

    [Fact]
    public async Task A_command_a_handler_posts_runs_off_the_loop_thread_before_Run_returns()
    {
        var pump = new FakePump(EventType.Keydown, EventType.Quit);
        var commands = new SdlCommandQueue(NullLogger.Instance);
        var loopThread = 0;
        var commandThread = 0;

        await RunAsync(
            pump,
            commands,
            onEvent: (ref Event _) =>
            {
                loopThread = Environment.CurrentManagedThreadId;
                commands.Post(_ =>
                {
                    Volatile.Write(ref commandThread, Environment.CurrentManagedThreadId);
                    return Task.CompletedTask;
                });
                return true;
            }
        );

        Assert.NotEqual(0, Volatile.Read(ref commandThread));
        Assert.NotEqual(loopThread, commandThread);
    }

    [Fact]
    public async Task A_queue_that_served_a_loop_is_refused_by_the_next()
    {
        var commands = new SdlCommandQueue(NullLogger.Instance);
        await RunAsync(new FakePump(EventType.Quit), commands);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(new FakePump(EventType.Quit), commands)
        );
    }

    [Fact]
    public async Task Quit_ends_the_loop_and_is_not_passed_to_the_handler()
    {
        var pump = new FakePump(EventType.Keydown, EventType.Quit);
        var handled = new List<EventType>();

        var exit = await RunAsync(
            pump,
            commands: null,
            onEvent: (ref Event e) =>
            {
                handled.Add((EventType)e.Type);
                return true;
            }
        );

        Assert.Equal(0, exit);
        Assert.Equal([EventType.Keydown], handled);
        Assert.Equal(1, pump.Renders);
    }

    [Fact]
    public async Task A_handler_that_returns_false_ends_the_loop()
    {
        var pump = new FakePump(EventType.Keydown);
        var ticks = 0;

        var exit = await RunAsync(
            pump,
            commands: null,
            onEvent: (ref Event _) => false,
            onTick: () => ticks++
        );

        Assert.Equal(0, exit);
        Assert.Equal(1, ticks);
    }

    [Fact]
    public async Task A_cancelled_token_ends_the_loop_before_it_renders()
    {
        var pump = new FakePump();

        var exit = await RunAsync(pump, commands: null, ct: new CancellationToken(canceled: true));

        Assert.Equal(0, exit);
        Assert.Equal(0, pump.Renders);
    }

    private static Task<int> RunAsync(
        FakePump pump,
        SdlCommandQueue? commands,
        SdlEventLoop.EventHandler? onEvent = null,
        SdlEventLoop.TickHandler? onTick = null,
        CancellationToken ct = default
    ) =>
        Task.Run(() => SdlEventLoop.RunCore(pump, commands, onEvent, onTick, sleepMs: 1, ct))
            .WaitAsync(Bound, CancellationToken.None);

    /// <summary>
    /// Hands the loop a scripted list of events, then reports each poll that finds none.
    /// </summary>
    private sealed class FakePump(params EventType[] script) : ISdlPump
    {
        private readonly Queue<EventType> _script = new(script);
        private int _emptyPolls;

        /// <summary>Called on the loop's thread with the count of empty polls so far.</summary>
        public Action<int>? OnEmptyPoll { get; set; }

        public int Renders { get; private set; }

        public List<EventType> Discarded { get; } = [];

        public void Enqueue(EventType type) => _script.Enqueue(type);

        public void RenderPendingFrame() => Renders++;

        public bool PollEvent(ref Event sdlEvent)
        {
            if (_script.TryDequeue(out var type))
            {
                sdlEvent = new Event { Type = (uint)type };
                return true;
            }
            OnEmptyPoll?.Invoke(++_emptyPolls);
            return false;
        }

        public void Discard(ref Event sdlEvent) => Discarded.Add((EventType)sdlEvent.Type);
    }
}
