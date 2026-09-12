// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using FrameFlow.Audio.OpenAL;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("FrameFlow.Audio.Tests")]

namespace FrameFlow.Audio.TestKit;

/// <summary>
/// Builds a real <see cref="OpenAlAudioSink"/> over a <see cref="FakeOpenAlDevice"/>.
/// </summary>
/// <remarks>
/// <para>
/// The sink's device seam (<c>IOpenAlApi</c> / <c>IOpenAlContextLease</c>) is
/// internal to <c>FrameFlow.Audio.OpenAL</c>, so a suite outside that assembly's
/// friend list cannot wire one itself. This is the public door: a caller hands
/// over a fake device and gets back the production sink, with no internals access
/// of its own (#146).
/// </para>
/// <para>
/// The sink returned is the real one. It is the component the integration suite
/// has never covered, because every content test there substitutes a capturing
/// sink for it and therefore records what the pipeline handed the sink rather
/// than what the sink handed OpenAL.
/// </para>
/// </remarks>
public static class FakeOpenAlSink
{
    /// <summary>
    /// Creates an <see cref="OpenAlAudioSink"/> that talks to
    /// <paramref name="device"/> instead of to a real OpenAL device.
    /// </summary>
    /// <param name="device">The fake the sink will drive.</param>
    /// <param name="logger">Optional logger; null stays silent.</param>
    /// <param name="timeProvider">
    /// Supplies the sleep in the sink's pacing loop. Null uses the sink's own
    /// default, which is the high-resolution provider production uses.
    /// </param>
    public static OpenAlAudioSink Create(
        FakeOpenAlDevice device,
        ILogger<OpenAlAudioSink>? logger = null,
        TimeProvider? timeProvider = null
    )
    {
        ArgumentNullException.ThrowIfNull(device);
        return new OpenAlAudioSink(logger, timeProvider, device.Lease);
    }
}
