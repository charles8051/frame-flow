// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FrameFlow.Decoding;

/// <summary>
/// Provides <see cref="IFrameFlowBuilder"/> extension methods for registering
/// the FrameFlow decoding layer — demux session factory, decoder factories,
/// and optionally an audio sink factory — without exposing the raw
/// <see cref="Func{TResult}"/> registration shapes to consumers.
/// </summary>
public static class FrameFlowDecodingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the FrameFlow decoding layer with default
    /// <see cref="DemuxSessionFactory"/> and
    /// <see cref="DecoderFactories"/> implementations.
    /// </summary>
    /// <param name="builder">
    /// The <see cref="IFrameFlowBuilder"/> returned from
    /// <c>services.AddFrameFlow()</c>.
    /// </param>
    /// <returns>The <paramref name="builder"/> instance for continued chaining.</returns>
    /// <remarks>
    /// Use the overload that accepts a <see cref="FrameFlowDecodingOptions"/>
    /// configuration delegate to supply custom decoder factories or an
    /// audio sink factory.
    /// </remarks>
    public static IFrameFlowBuilder AddFrameFlowDecoding(this IFrameFlowBuilder builder) =>
        builder.AddFrameFlowDecoding(configure: null);

    /// <summary>
    /// Registers the FrameFlow decoding layer with optional configuration for
    /// decoder factories and an audio sink factory. Unset options fall back
    /// to the shipped default factories.
    /// </summary>
    /// <param name="builder">
    /// The <see cref="IFrameFlowBuilder"/> returned from
    /// <c>services.AddFrameFlow()</c>.
    /// </param>
    /// <param name="configure">
    /// Optional configuration delegate applied to
    /// <see cref="FrameFlowDecodingOptions"/> before the services are
    /// registered. When <see langword="null"/>, the defaults are used.
    /// </param>
    /// <returns>The <paramref name="builder"/> instance for continued chaining.</returns>
    public static IFrameFlowBuilder AddFrameFlowDecoding(
        this IFrameFlowBuilder builder,
        Action<FrameFlowDecodingOptions>? configure
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new FrameFlowDecodingOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton<IDemuxSessionFactory, DemuxSessionFactory>();

        // ADR-0033: when the caller has provided an explicit
        // VideoDecoderFactory, honour it. Otherwise build a hwaccel-aware
        // factory by resolving FrameFlowOptions.Video.HardwareDecode and the
        // bootstrap-time HardwareDecodeCapabilities from DI at construction
        // time.
        if (options.VideoDecoderFactory is { } explicitVideoFactory)
        {
            builder.Services.TryAddSingleton<Func<IDemuxSession, IVideoDecoder?>>(_ =>
                explicitVideoFactory
            );
        }
        else
        {
            builder.Services.TryAddSingleton<Func<IDemuxSession, IVideoDecoder?>>(sp =>
            {
                var hwOptions =
                    sp.GetService<IOptions<FrameFlowOptions>>()?.Value?.Video?.HardwareDecode
                    ?? new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled };
                var capabilities =
                    sp.GetService<HardwareDecodeCapabilities>() ?? HardwareDecodeCapabilities.Empty;
                var loggerFactory = sp.GetService<ILoggerFactory>();
                return DecoderFactories.CreateVideo(hwOptions, capabilities, loggerFactory);
            });
        }

        // Mirror the video registration: if the caller provided an
        // explicit factory, honour it; otherwise build a logger-aware
        // factory by resolving ILoggerFactory from DI at construction
        // time. Symmetric with CreateVideo above — both threaded
        // loggers prevent AudioDecoder/VideoDecoder diagnostics from
        // being swallowed by NullLogger.Instance.
        if (options.AudioDecoderFactory is { } explicitAudioFactory)
        {
            builder.Services.TryAddSingleton<Func<IDemuxSession, IAudioDecoder?>>(_ =>
                explicitAudioFactory
            );
        }
        else
        {
            builder.Services.TryAddSingleton<Func<IDemuxSession, IAudioDecoder?>>(sp =>
            {
                var loggerFactory = sp.GetService<ILoggerFactory>();
                return DecoderFactories.CreateAudio(loggerFactory);
            });
        }

        // Crossbar ADR-0014 Phase 4 (FrameFlow ADR-0036): IDecodedMediaStreamFactory + its concrete
        // DecodedMediaStreamFactory implementation are deleted.
        // Consumers compose decoders directly via DecoderFactories on
        // top of an IDemuxSession, then wrap with the
        // FrameFlow.Decoding.DecoderSourceAdapters substrate-side.

        return builder;
    }
}
