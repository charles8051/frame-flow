// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FrameFlow.Audio.OpenAL;

/// <summary>
/// Provides <see cref="IFrameFlowBuilder"/> extension methods for registering the
/// OpenAL audio sink adapter.
/// </summary>
public static class FrameFlowOpenAlServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="OpenAlAudioSink"/> as the application's
    /// <see cref="IAudioSink"/> singleton. The DI provider owns the sink
    /// lifecycle and disposes it on container teardown (per ADR-0044).
    /// </summary>
    /// <param name="builder">
    /// The <see cref="IFrameFlowBuilder"/> returned from
    /// <see cref="FrameFlowServiceCollectionExtensions.AddFrameFlow"/>.
    /// </param>
    /// <returns>The <paramref name="builder"/> instance for continued chaining.</returns>
    /// <remarks>
    /// <para>
    /// The sink is registered as a singleton; the DI container constructs it
    /// lazily on first resolution and disposes it when the container is torn
    /// down. Per ADR-0044, the playback session uses the sink via
    /// Activate/Deactivate but never disposes it. <c>TryAddSingleton</c>
    /// preserves any prior <see cref="IAudioSink"/> registration so consumers
    /// can substitute an alternative backend.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// services
    ///     .AddFrameFlow()
    ///     .AddFrameFlowDecoding()
    ///     .AddFrameFlowPlayback()
    ///     .AddFrameFlowOpenAlAudio();
    /// </code>
    /// </example>
    public static IFrameFlowBuilder AddFrameFlowOpenAlAudio(this IFrameFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<IAudioSink, OpenAlAudioSink>();
        return builder;
    }
}
