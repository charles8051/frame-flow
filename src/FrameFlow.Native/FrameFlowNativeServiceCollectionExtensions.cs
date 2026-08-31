// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow;
using FrameFlow.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FrameFlow.Native;

/// <summary>
/// Provides <see cref="IServiceCollection"/> and <see cref="IFrameFlowBuilder"/> extension
/// methods for registering FrameFlow native bootstrap services.
/// </summary>
public static class FrameFlowNativeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the FrameFlow native bootstrap services into the service collection.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">
    /// An optional delegate to configure <see cref="FrameFlowNativeOptions"/>. When omitted,
    /// defaults are used (bundled binaries, system library probe enabled).
    /// The same options can also be bound from configuration via
    /// <c>services.Configure&lt;FrameFlowNativeOptions&gt;(config.GetSection("FrameFlow:Native"))</c>.
    /// </param>
    /// <returns>The <paramref name="services"/> instance for continued chaining.</returns>
    /// <remarks>
    /// This method registers <see cref="IFrameFlowBootstrapper"/> as a singleton. It does
    /// not trigger eager bootstrap at registration time. To bootstrap at application startup,
    /// call <see cref="AddHostedBootstrap(IFrameFlowBuilder)"/> after
    /// <see cref="FrameFlowServiceCollectionExtensions.AddFrameFlow"/>.
    /// </remarks>
    public static IServiceCollection AddFrameFlowNative(
        this IServiceCollection services,
        Action<FrameFlowNativeOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<FrameFlowNativeOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.TryAddSingleton<IFrameFlowBootstrapper>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<FrameFlowNativeOptions>>().Value;
            // ILoggerFactory is optional — falls back to NullLoggerFactory when logging
            // infrastructure is not registered (e.g., minimal test scenarios).
            var loggerFactory = sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            return new FrameFlowBootstrapper(opts, loggerFactory);
        });

        // ADR-0033: expose the hardware-decode capability set as a singleton
        // service. Resolution forces bootstrap to run (Initialize is idempotent
        // and thread-safe), so consumers can inject HardwareDecodeCapabilities
        // without retaining the bootstrap result.
        services.TryAddSingleton<HardwareDecodeCapabilities>(sp =>
        {
            var bootstrapper = sp.GetRequiredService<IFrameFlowBootstrapper>();
            var bootstrapResult = bootstrapper.Initialize();
            return bootstrapResult.Capabilities;
        });

        return services;
    }

    /// <summary>
    /// Registers an <see cref="IHostedService"/> that eagerly initializes the FrameFlow
    /// native bootstrap (FFmpeg binary loading and codec probing) at application startup,
    /// rather than lazily on the first session creation.
    /// </summary>
    /// <param name="builder">
    /// The <see cref="IFrameFlowBuilder"/> returned from
    /// <see cref="FrameFlowServiceCollectionExtensions.AddFrameFlow"/>.
    /// </param>
    /// <returns>The <paramref name="builder"/> instance for continued chaining.</returns>
    /// <remarks>
    /// <para>
    /// Eager bootstrap surfaces FFmpeg availability failures at application startup rather
    /// than during the first media open attempt, making misconfiguration easier to diagnose.
    /// </para>
    /// <para>
    /// This extension method also calls <see cref="AddFrameFlowNative"/> with default options
    /// if native services have not already been registered. Pass a configure delegate to
    /// <see cref="AddFrameFlowNative"/> explicitly if you need non-default native options.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// services
    ///     .AddFrameFlow(options => { options.Playback.InitialRepeatMode = RepeatMode.One; })
    ///     .AddHostedBootstrap();
    /// </code>
    ///
    /// With explicit native options:
    /// <code>
    /// services
    ///     .AddFrameFlowNative(native => native.CustomFfmpegPath = "/opt/ffmpeg")
    ///     .AddFrameFlow()
    ///     .AddHostedBootstrap();
    /// </code>
    /// </example>
    public static IFrameFlowBuilder AddHostedBootstrap(this IFrameFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Ensure native services are registered (idempotent via TryAdd semantics).
        builder.Services.AddFrameFlowNative();

        builder.Services.AddHostedService<FrameFlowHostedService>();

        return builder;
    }
}
