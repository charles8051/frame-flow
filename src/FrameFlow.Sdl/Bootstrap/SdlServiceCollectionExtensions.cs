// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.SDL;

namespace FrameFlow.SDL.Bootstrap;

/// <summary>
/// Extension methods for registering SDL2 bootstrap services with
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class SdlServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISdlBootstrapper"/> as a singleton and
    /// <see cref="Sdl"/> as a singleton whose factory calls
    /// <see cref="ISdlBootstrapper.CreateSdlApi"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ISdlBootstrapper.Initialize"/> is not called automatically.
    /// Either call it manually before resolving <see cref="Sdl"/>, or chain
    /// <see cref="AddHostedSdlBootstrap"/> to initialize at hosted startup.
    /// </remarks>
    public static IServiceCollection AddFrameFlowSdl(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISdlBootstrapper>(sp =>
        {
            var opts = sp.GetService<IOptions<SdlNativeOptions>>()?.Value ?? new SdlNativeOptions();
            var loggerFactory = sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            return new SdlBootstrapper(opts, loggerFactory);
        });

        services.TryAddSingleton<Sdl>(sp =>
        {
            var bootstrapper = sp.GetRequiredService<ISdlBootstrapper>();
            return bootstrapper.CreateSdlApi();
        });

        return services;
    }

    /// <summary>
    /// Adds a hosted service that calls <see cref="ISdlBootstrapper.Initialize"/> at
    /// application startup, ensuring SDL2 is resolved before any hosted component runs.
    /// </summary>
    /// <remarks>
    /// Must be chained after <see cref="AddFrameFlowSdl"/>:
    /// <code>services.AddFrameFlowSdl().AddHostedSdlBootstrap();</code>
    /// </remarks>
    public static IServiceCollection AddHostedSdlBootstrap(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<SdlHostedService>();
        return services;
    }

    /// <summary>
    /// Registers an already-constructed <see cref="SdlVideoSink"/> with the
    /// service collection — both as <see cref="IVideoSink"/> (for the
    /// playback pipeline) and as the concrete <see cref="SdlVideoSink"/>
    /// (for callers that need to invoke <see cref="SdlVideoSink.RenderPendingFrame"/>
    /// from the SDL render loop). The sink's <see cref="SdlVideoSink.FramePool"/>
    /// is also registered as <see cref="IFramePool"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use this overload when the consumer constructs the sink themselves
    /// — typically because the SDL window setup is platform-sensitive
    /// (macOS requires the OS main thread, see <see cref="SdlBootstrapper"/>
    /// remarks) and best done outside the DI container.
    /// </para>
    /// <para>
    /// For the simple-case where you just want a window with default
    /// dimensions, use the <see cref="AddFrameFlowSdlVideoSink(IServiceCollection, Sdl, string, int, int, out SdlVideoSink, ILogger{SdlVideoSink}?)"/>
    /// overload instead — it constructs the sink internally and outputs
    /// it for the caller.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddFrameFlowSdlVideoSink(
        this IServiceCollection services,
        SdlVideoSink sink
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sink);

        services.TryAddSingleton<IFramePool>(sink.FramePool);
        services.TryAddSingleton<IVideoSink>(sink);
        // Also register the concrete type so the SDL event loop can
        // resolve it for RenderPendingFrame() calls without an
        // upcast to IVideoSink.
        services.TryAddSingleton(sink);
        return services;
    }

    /// <summary>
    /// Convenience overload that constructs an <see cref="SdlVideoSink"/>
    /// against a freshly-allocated <see cref="CpuFramePool"/> and registers
    /// both with the service collection. Outputs the sink so the caller
    /// can pass it to <see cref="SdlEventLoop.Run"/> (or invoke
    /// <see cref="SdlVideoSink.RenderPendingFrame"/> directly).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="sdl">The bootstrapped SDL2 wrapper.</param>
    /// <param name="windowTitle">Initial window title.</param>
    /// <param name="width">Initial window width in pixels.</param>
    /// <param name="height">Initial window height in pixels.</param>
    /// <param name="sink">The created <see cref="SdlVideoSink"/>.</param>
    /// <param name="logger">Optional sink logger.</param>
    /// <example>
    /// <code>
    /// services.AddFrameFlowSdl();
    /// services.AddFrameFlowSdlVideoSink(sdl, "Player", 1280, 720, out var videoSink);
    /// // ...
    /// SdlEventLoop.Run(sdl, videoSink, onEvent: ...);
    /// </code>
    /// </example>
    public static IServiceCollection AddFrameFlowSdlVideoSink(
        this IServiceCollection services,
        Sdl sdl,
        string windowTitle,
        int width,
        int height,
        out SdlVideoSink sink,
        ILogger<SdlVideoSink>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sdl);
        ArgumentException.ThrowIfNullOrEmpty(windowTitle);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        var framePool = new CpuFramePool(NullLogger<CpuFramePool>.Instance);
        sink = new SdlVideoSink(sdl, framePool, windowTitle, width, height, logger);
        return AddFrameFlowSdlVideoSink(services, sink);
    }
}
