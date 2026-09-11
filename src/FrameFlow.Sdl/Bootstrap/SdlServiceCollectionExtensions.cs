// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FrameFlow.Sdl.Bootstrap;

/// <summary>
/// Extension methods for registering SDL2 bootstrap services with
/// <see cref="IFrameFlowBuilder"/>.
/// </summary>
public static class SdlServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISdlBootstrapper"/> as a singleton and
    /// <see cref="SdlApi"/> as a singleton whose factory calls
    /// <see cref="ISdlBootstrapper.CreateSdlApi"/>.
    /// </summary>
    /// <param name="builder">
    /// The <see cref="IFrameFlowBuilder"/> returned from
    /// <see cref="FrameFlowServiceCollectionExtensions.AddFrameFlow"/>.
    /// </param>
    /// <returns>The <paramref name="builder"/> instance for continued chaining.</returns>
    /// <remarks>
    /// <see cref="ISdlBootstrapper.Initialize"/> is not called automatically.
    /// Either call it manually before resolving <see cref="SdlApi"/>, or chain
    /// <see cref="AddHostedSdlBootstrap"/> to initialize at hosted startup.
    /// </remarks>
    public static IFrameFlowBuilder AddFrameFlowSdl(this IFrameFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<ISdlBootstrapper>(sp =>
        {
            var opts = sp.GetService<IOptions<SdlNativeOptions>>()?.Value ?? new SdlNativeOptions();
            var loggerFactory = sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            return new SdlBootstrapper(opts, loggerFactory);
        });

        builder.Services.TryAddSingleton<SdlApi>(sp =>
        {
            var bootstrapper = sp.GetRequiredService<ISdlBootstrapper>();
            return bootstrapper.CreateSdlApi();
        });

        return builder;
    }

    /// <summary>
    /// Adds a hosted service that calls <see cref="ISdlBootstrapper.Initialize"/> at
    /// application startup, ensuring SDL2 is resolved before any hosted component runs.
    /// </summary>
    /// <param name="builder">
    /// The <see cref="IFrameFlowBuilder"/> returned from
    /// <see cref="FrameFlowServiceCollectionExtensions.AddFrameFlow"/>.
    /// </param>
    /// <returns>The <paramref name="builder"/> instance for continued chaining.</returns>
    /// <remarks>
    /// Must be chained after <see cref="AddFrameFlowSdl"/>:
    /// <code>builder.AddFrameFlowSdl().AddHostedSdlBootstrap();</code>
    /// </remarks>
    public static IFrameFlowBuilder AddHostedSdlBootstrap(this IFrameFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHostedService<SdlHostedService>();
        return builder;
    }

    /// <summary>
    /// Registers an already-constructed <see cref="SdlVideoSink"/> with the
    /// service collection — both as <see cref="IVideoSink"/> (for the
    /// playback pipeline) and as the concrete <see cref="SdlVideoSink"/>
    /// (for callers that need to invoke <see cref="SdlVideoSink.RenderPendingFrame"/>
    /// from the SDL render loop). The sink's <see cref="SdlVideoSink.FramePool"/>
    /// is also registered as <see cref="IFramePool"/>.
    /// </summary>
    /// <param name="builder">
    /// The <see cref="IFrameFlowBuilder"/> returned from
    /// <see cref="FrameFlowServiceCollectionExtensions.AddFrameFlow"/>.
    /// </param>
    /// <param name="sink">The sink to register.</param>
    /// <returns>The <paramref name="builder"/> instance for continued chaining.</returns>
    /// <remarks>
    /// <para>
    /// Use this overload when the consumer constructs the sink themselves
    /// — typically because the SDL window setup is platform-sensitive
    /// (macOS requires the OS main thread, see <see cref="SdlBootstrapper"/>
    /// remarks) and best done outside the DI container.
    /// </para>
    /// <para>
    /// For the simple-case where you just want a window with default
    /// dimensions, use the <see cref="AddFrameFlowSdlVideoSink(IFrameFlowBuilder, SdlApi, string, int, int, out SdlVideoSink, ILogger{SdlVideoSink}?)"/>
    /// overload instead — it constructs the sink internally and outputs
    /// it for the caller.
    /// </para>
    /// </remarks>
    public static IFrameFlowBuilder AddFrameFlowSdlVideoSink(
        this IFrameFlowBuilder builder,
        SdlVideoSink sink
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sink);

        builder.Services.TryAddSingleton<IFramePool>(sink.FramePool);
        builder.Services.TryAddSingleton<IVideoSink>(sink);
        // Also register the concrete type so the SDL event loop can
        // resolve it for RenderPendingFrame() calls without an
        // upcast to IVideoSink.
        builder.Services.TryAddSingleton(sink);
        return builder;
    }

    /// <summary>
    /// Convenience overload that constructs an <see cref="SdlVideoSink"/>
    /// against a freshly-allocated <see cref="CpuFramePool"/> and registers
    /// both with the service collection. Outputs the sink so the caller
    /// can pass it to <see cref="SdlEventLoop.Run"/> (or invoke
    /// <see cref="SdlVideoSink.RenderPendingFrame"/> directly).
    /// </summary>
    /// <param name="builder">
    /// The <see cref="IFrameFlowBuilder"/> returned from
    /// <see cref="FrameFlowServiceCollectionExtensions.AddFrameFlow"/>.
    /// </param>
    /// <param name="sdl">The bootstrapped SDL2 wrapper.</param>
    /// <param name="windowTitle">Initial window title.</param>
    /// <param name="width">Initial window width in pixels.</param>
    /// <param name="height">Initial window height in pixels.</param>
    /// <param name="sink">The created <see cref="SdlVideoSink"/>.</param>
    /// <param name="logger">Optional sink logger.</param>
    /// <returns>The <paramref name="builder"/> instance for continued chaining.</returns>
    /// <example>
    /// <code>
    /// services
    ///     .AddFrameFlow()
    ///     .AddFrameFlowSdl()
    ///     .AddFrameFlowSdlVideoSink(sdl, "Player", 1280, 720, out var videoSink);
    /// // ...
    /// SdlEventLoop.Run(sdl, videoSink, onEvent: ...);
    /// </code>
    /// </example>
    public static IFrameFlowBuilder AddFrameFlowSdlVideoSink(
        this IFrameFlowBuilder builder,
        SdlApi sdl,
        string windowTitle,
        int width,
        int height,
        out SdlVideoSink sink,
        ILogger<SdlVideoSink>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sdl);
        ArgumentException.ThrowIfNullOrEmpty(windowTitle);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        var framePool = new CpuFramePool(NullLogger<CpuFramePool>.Instance);
        sink = new SdlVideoSink(sdl, framePool, windowTitle, width, height, logger);
        return AddFrameFlowSdlVideoSink(builder, sink);
    }
}
