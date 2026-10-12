// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Silk.NET.SDL;

namespace FrameFlow.Sdl;

/// <summary>
/// Runs the standard SDL2 event-loop pump for an <see cref="SdlVideoSink"/>:
/// polls events, dispatches them to a consumer-supplied handler, calls
/// <see cref="SdlVideoSink.RenderPendingFrame"/> each tick, and exits on
/// <see cref="EventType.Quit"/> or cancellation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> SDL requires <c>SDL_PollEvent</c>, <c>SDL_RenderPresent</c>,
/// and <c>SDL_CreateWindow</c> all run on the same OS thread. On macOS this
/// must be the actual OS main thread (NSAppKit-imposed). Call
/// <c>Run</c> from <c>Main</c> directly — not from a
/// <see cref="System.Threading.Thread"/> the runtime created.
/// </para>
/// <para>
/// <b>Pacing.</b> The loop sleeps <see cref="DefaultPollInterval"/>
/// (~8 ms = ~120 Hz) between polls. The render call is non-blocking — it
/// only paints if the sink has a pending frame. Adjust
/// <c>pollInterval</c> if you need a different cadence.
/// </para>
/// <para>
/// <b>Cancellation.</b> The loop checks <c>ct</c> between each
/// poll cycle and exits cleanly when cancelled. SDL's own
/// <see cref="EventType.Quit"/> (Cmd-Q, window close, etc.) also exits
/// the loop.
/// </para>
/// <para>
/// <b>Asynchronous work.</b> The handlers are synchronous and run on the SDL
/// thread. To start asynchronous work from one, such as a player command,
/// pass an <see cref="SdlCommandQueue"/> to <c>Run</c> and post the work to
/// it: the queue runs it off the SDL thread, in order, and <c>Run</c> waits
/// for it before returning.
/// </para>
/// </remarks>
public static class SdlEventLoop
{
    /// <summary>
    /// Default time between event-poll cycles (~120 Hz).
    /// </summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Handler invoked for each polled SDL event other than
    /// <see cref="EventType.Quit"/>. Return <see langword="true"/> to keep
    /// the loop running; <see langword="false"/> to exit.
    /// </summary>
    /// <param name="sdlEvent">
    /// The polled SDL event, passed by ref so handlers can access the
    /// packed-union fields without copying.
    /// </param>
    public delegate bool EventHandler(ref Event sdlEvent);

    /// <summary>
    /// Optional per-tick callback invoked after rendering and before the
    /// next event poll. Useful for checking playback completion, updating
    /// stats, or driving any other UI work that needs to happen on the
    /// SDL thread.
    /// </summary>
    public delegate void TickHandler();

    /// <summary>
    /// Runs the event loop on the calling thread until
    /// <see cref="EventType.Quit"/> is received, <paramref name="onEvent"/>
    /// returns <see langword="false"/>, or <paramref name="ct"/> is
    /// cancelled. Returns 0 on clean exit.
    /// </summary>
    /// <param name="sdl">The bootstrapped SDL2 wrapper.</param>
    /// <param name="videoSink">
    /// The video sink whose <see cref="SdlVideoSink.RenderPendingFrame"/>
    /// will be called each tick.
    /// </param>
    /// <param name="onEvent">
    /// Optional handler for keyboard, drag/drop, and other SDL events.
    /// </param>
    /// <param name="onTick">
    /// Optional per-tick callback (after render, before next poll).
    /// </param>
    /// <param name="pollInterval">
    /// Sleep duration between poll cycles. Defaults to
    /// <see cref="DefaultPollInterval"/>.
    /// </param>
    /// <param name="ct">Cancellation token; loop exits cleanly when set.</param>
    /// <returns>0 on clean exit.</returns>
    public static int Run(
        SdlApi sdl,
        SdlVideoSink videoSink,
        EventHandler? onEvent = null,
        TickHandler? onTick = null,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(sdl);
        ArgumentNullException.ThrowIfNull(videoSink);

        return RunCore(
            new SdlPump(sdl, videoSink),
            commands: null,
            onEvent,
            onTick,
            ToSleepMs(pollInterval),
            ct
        );
    }

    /// <summary>
    /// Runs the event loop on the calling thread, as the overload without
    /// <paramref name="commands"/> does, and runs the commands posted to
    /// <paramref name="commands"/> beside it: one at a time, in the order
    /// posted, off the SDL thread. Returns once the loop has exited and every
    /// command has finished.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A handler posts a player command to <paramref name="commands"/> and
    /// returns, so the command neither blocks rendering and input nor races
    /// the commands posted before it. <see cref="SdlCommandQueue"/> says what
    /// a command may and may not do; in short, it must not touch the window.
    /// </para>
    /// <para>
    /// <b>Exit.</b> When the loop exits, for <see cref="EventType.Quit"/>, a
    /// handler that returns <see langword="false"/>, <paramref name="ct"/>,
    /// or a handler that throws, the queue refuses further commands and
    /// cancels the token it gave each one, and the commands it already
    /// accepted still run. The loop waits for the last of them. While it
    /// waits it keeps polling SDL, so the window stays responsive, but drops
    /// the events: no handler is called and nothing is rendered. When
    /// <c>Run</c> returns no command is running, so the caller can dispose
    /// the player the commands used and then destroy the window:
    /// </para>
    /// <code>
    /// var commands = new SdlCommandQueue(logger);
    /// SdlEventLoop.Run(sdl, videoSink, commands, onEvent: (ref Event e) =>
    /// {
    ///     if ((EventType)e.Type == EventType.Keydown &amp;&amp; (KeyCode)e.Key.Keysym.Sym == KeyCode.KSpace)
    ///         commands.Post(ct => player.PauseAsync(ct));
    ///     return true;
    /// });
    /// player.DisposeAsync().AsTask().GetAwaiter().GetResult();
    /// videoSink.DestroyResources();
    /// </code>
    /// </remarks>
    /// <param name="sdl">The bootstrapped SDL2 wrapper.</param>
    /// <param name="videoSink">
    /// The video sink whose <see cref="SdlVideoSink.RenderPendingFrame"/>
    /// will be called each tick.
    /// </param>
    /// <param name="commands">
    /// The queue the handlers post to. A queue serves one loop.
    /// </param>
    /// <param name="onEvent">
    /// Optional handler for keyboard, drag/drop, and other SDL events.
    /// </param>
    /// <param name="onTick">
    /// Optional per-tick callback (after render, before next poll).
    /// </param>
    /// <param name="pollInterval">
    /// Sleep duration between poll cycles. Defaults to
    /// <see cref="DefaultPollInterval"/>.
    /// </param>
    /// <param name="ct">Cancellation token; loop exits cleanly when set.</param>
    /// <returns>0 on clean exit.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="commands"/> has already served a loop.
    /// </exception>
    public static int Run(
        SdlApi sdl,
        SdlVideoSink videoSink,
        SdlCommandQueue commands,
        EventHandler? onEvent = null,
        TickHandler? onTick = null,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(sdl);
        ArgumentNullException.ThrowIfNull(videoSink);
        ArgumentNullException.ThrowIfNull(commands);

        return RunCore(
            new SdlPump(sdl, videoSink),
            commands,
            onEvent,
            onTick,
            ToSleepMs(pollInterval),
            ct
        );
    }

    /// <summary>
    /// The loop both overloads run, over <paramref name="pump"/> so tests can
    /// drive it without a display.
    /// </summary>
    internal static int RunCore(
        ISdlPump pump,
        SdlCommandQueue? commands,
        EventHandler? onEvent,
        TickHandler? onTick,
        int sleepMs,
        CancellationToken ct
    )
    {
        commands?.Start();
        try
        {
            var quit = false;
            var evt = new Event();

            while (!quit && !ct.IsCancellationRequested)
            {
                pump.RenderPendingFrame();
                onTick?.Invoke();

                while (pump.PollEvent(ref evt))
                {
                    if ((EventType)evt.Type == EventType.Quit)
                    {
                        quit = true;
                        continue;
                    }
                    if (onEvent is not null && !onEvent(ref evt))
                    {
                        quit = true;
                    }
                }

                if (!quit && sleepMs > 0)
                {
                    System.Threading.Thread.Sleep(sleepMs);
                }
            }

            return 0;
        }
        finally
        {
            if (commands is not null)
                Drain(pump, commands.Close(), sleepMs);
        }
    }

    // Waits for the commands while keeping the window responsive: SDL is
    // polled at the loop's cadence, and its events are dropped.
    private static void Drain(ISdlPump pump, Task drained, int sleepMs)
    {
        var evt = new Event();
        while (!drained.IsCompleted)
        {
            while (pump.PollEvent(ref evt))
                pump.Discard(ref evt);
            drained.Wait(sleepMs);
        }
    }

    private static int ToSleepMs(TimeSpan? pollInterval)
    {
        var sleepMs = (int)(pollInterval ?? DefaultPollInterval).TotalMilliseconds;
        return sleepMs < 0 ? 0 : sleepMs;
    }
}
