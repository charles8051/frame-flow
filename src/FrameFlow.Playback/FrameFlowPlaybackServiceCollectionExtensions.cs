// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FrameFlow.Playback;

/// <summary>
/// Provides <see cref="IFrameFlowBuilder"/> extension methods for registering FrameFlow
/// playback services.
/// </summary>
/// <remarks>
/// <para>
/// <b>Post-Phase-4 (Crossbar ADR-0014).</b> The substrate's <see cref="PlaybackControllerCore"/>
/// is constructed via the static <see cref="PlaybackController.Create"/> factory —
/// not through DI. This extension is reduced to
/// registering the playback clock for consumers who want the default
/// <see cref="PlaybackClock"/> implementation in their DI container.
/// </para>
/// </remarks>
public static class FrameFlowPlaybackServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IPlaybackClock"/> (default
    /// <see cref="PlaybackClock"/>) into the service collection. Consumers who
    /// build playback controllers via <see cref="PlaybackController.Create"/>
    /// don't need this — the static factory wires its own clock.
    /// </summary>
    public static IFrameFlowBuilder AddFrameFlowPlayback(this IFrameFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddTransient<IPlaybackClock, PlaybackClock>();
        return builder;
    }
}
