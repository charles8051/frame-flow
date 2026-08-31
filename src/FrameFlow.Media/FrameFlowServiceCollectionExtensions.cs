// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FrameFlow;

/// <summary>
/// Provides <see cref="IServiceCollection"/> extension methods for registering FrameFlow
/// core services into a DI container.
/// </summary>
public static class FrameFlowServiceCollectionExtensions
{
    /// <summary>
    /// Registers FrameFlow core services and options into the service collection.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">
    /// An optional delegate to configure <see cref="FrameFlowOptions"/>. When omitted,
    /// default values are used. The same options can also be bound from configuration
    /// via <c>services.Configure&lt;FrameFlowOptions&gt;(config.GetSection("FrameFlow"))</c>
    /// before or after calling this method.
    /// </param>
    /// <returns>
    /// An <see cref="IFrameFlowBuilder"/> that can be used to add optional FrameFlow
    /// adapters (audio backend, video presenter, hosted bootstrap).
    /// </returns>
    /// <remarks>
    /// Calling this method multiple times is safe — subsequent calls are no-ops for
    /// services already registered via <c>TryAdd*</c> semantics.
    /// </remarks>
    /// <example>
    /// Minimal registration using defaults:
    /// <code>
    /// services.AddFrameFlow();
    /// </code>
    ///
    /// Registration with inline options configuration:
    /// <code>
    /// services.AddFrameFlow(options =>
    /// {
    ///     options.Playback.InitialRepeatMode = RepeatMode.One;
    ///     options.Audio.EnableAudio = true;
    /// });
    /// </code>
    ///
    /// Registration with configuration section binding (works with or without the delegate):
    /// <code>
    /// services
    ///     .Configure&lt;FrameFlowOptions&gt;(configuration.GetSection("FrameFlow"))
    ///     .AddFrameFlow();
    /// </code>
    /// </example>
    public static IFrameFlowBuilder AddFrameFlow(
        this IServiceCollection services,
        Action<FrameFlowOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register the top-level options type.
        var optionsBuilder = services.AddOptions<FrameFlowOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        // Register focused sub-option types, bound from the parent options so callers
        // can either configure FrameFlowOptions as a whole or bind sub-sections directly.
        services
            .AddOptions<FrameFlowPlaybackOptions>()
            .Configure<IOptions<FrameFlowOptions>>(
                (playback, parent) =>
                {
                    var src = parent.Value.Playback;
                    playback.InitialRepeatMode = src.InitialRepeatMode;
                }
            );

        services
            .AddOptions<FrameFlowVideoOptions>()
            .Configure<IOptions<FrameFlowOptions>>(
                (video, parent) =>
                {
                    var src = parent.Value.Video;
                    // ADR-0033: forward the hardware-decode policy so consumers
                    // that bind FrameFlowVideoOptions directly see the same
                    // values as those who go through FrameFlowOptions.Video.
                    video.HardwareDecode = src.HardwareDecode;
                }
            );

        services
            .AddOptions<FrameFlowAudioOptions>()
            .Configure<IOptions<FrameFlowOptions>>(
                (audio, parent) =>
                {
                    var src = parent.Value.Audio;
                    audio.EnableAudio = src.EnableAudio;
                    audio.PreferredChannels = src.PreferredChannels;
                }
            );

        services
            .AddOptions<FrameFlowBufferingOptions>()
            .Configure<IOptions<FrameFlowOptions>>(
                (buffering, parent) =>
                {
                    var src = parent.Value.Buffering;
                    buffering.MaxQueuedAudioPackets = src.MaxQueuedAudioPackets;
                    buffering.MaxQueuedVideoPackets = src.MaxQueuedVideoPackets;
                    buffering.MaxPendingFrames = src.MaxPendingFrames;
                }
            );

        return new FrameFlowDiBuilder(services);
    }
}

/// <summary>
/// Internal implementation of <see cref="IFrameFlowBuilder"/> for the DI registration path.
/// </summary>
internal sealed class FrameFlowDiBuilder(IServiceCollection services) : IFrameFlowBuilder
{
    /// <inheritdoc />
    public IServiceCollection Services { get; } = services;
}
