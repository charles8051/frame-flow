// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Silk.NET.SDL;

namespace FrameFlow.SDL;

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
/// <see cref="Run"/> from <c>Main</c> directly — not from a
/// <see cref="System.Threading.Thread"/> the runtime created.
/// </para>
/// <para>
/// <b>Pacing.</b> The loop sleeps <see cref="DefaultPollInterval"/>
/// (~8 ms = ~120 Hz) between polls. The render call is non-blocking — it
/// only paints if the sink has a pending frame. Adjust
/// <paramref name="pollInterval"/> if you need a different cadence.
/// </para>
/// <para>
/// <b>Cancellation.</b> The loop checks <paramref name="ct"/> between each
/// poll cycle and exits cleanly when cancelled. SDL's own
/// <see cref="EventType.Quit"/> (Cmd-Q, window close, etc.) also exits
/// the loop.
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
        Sdl sdl,
        SdlVideoSink videoSink,
        EventHandler? onEvent = null,
        TickHandler? onTick = null,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(sdl);
        ArgumentNullException.ThrowIfNull(videoSink);

        var sleepMs = (int)(pollInterval ?? DefaultPollInterval).TotalMilliseconds;
        if (sleepMs < 0)
            sleepMs = 0;

        var quit = false;
        var evt = new Event();

        while (!quit && !ct.IsCancellationRequested)
        {
            videoSink.RenderPendingFrame();
            onTick?.Invoke();

            while (sdl.PollEvent(ref evt) != 0)
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
}
