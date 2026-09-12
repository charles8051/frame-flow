// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using FrameFlow.Audio.TestKit;

namespace FrameFlow.Integration.Tests.Harness.Capture;

/// <summary>
/// Advances a <see cref="FakeOpenAlDevice"/>'s play cursor in real time, so a
/// real <c>OpenAlAudioSink</c> driven by that device behaves the way one driven
/// by a sound card does.
/// </summary>
/// <remarks>
/// <para>
/// The fake plays nothing on its own; without a pump the sink's sample-counter
/// clock never moves, the pacer never releases a frame, and playback stalls at
/// the first buffer.
/// </para>
/// <para>
/// <b>The pump runs at 1x and cannot usefully run faster.</b> The sink holds its
/// published clock to the rate a playing device can consume at: a reading that
/// outruns elapsed playing time is treated as audio the device dropped rather
/// than played, and clamped (#127). A pump that advanced the cursor at 4x would
/// have every extra second clamped straight back off, so a clip costs about its
/// own duration to play. That is the price of covering the real sink, and it is
/// why this drives the short corpus clips rather than the whole corpus.
/// </para>
/// <para>
/// Each tick advances by the wall time actually elapsed since the last tick,
/// not by the nominal interval, so a descheduled pump thread under-advances
/// rather than silently granting the device more audio than real time allows.
/// </para>
/// </remarks>
internal sealed class FakeDevicePump : IAsyncDisposable
{
    // Well under the sink's ~50 ms buffer, so a buffer boundary is never crossed
    // in a single jump and the queue depth moves the way a device moves it.
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(10);

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeDevicePump(FakeOpenAlDevice device, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        _loop = Task.Run(() => RunAsync(device, interval ?? DefaultInterval, _stop.Token));
    }

    private static async Task RunAsync(
        FakeOpenAlDevice device,
        TimeSpan interval,
        CancellationToken cancellationToken
    )
    {
        var clock = Stopwatch.StartNew();
        var consumed = TimeSpan.Zero;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

                var now = clock.Elapsed;
                device.AdvancePlayback(now - consumed);
                consumed = now;
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by the harness at the end of playback.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on the cancellation path.
        }
        _stop.Dispose();
    }
}
