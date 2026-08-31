// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow;
using FrameFlow.Media;
using FrameFlow.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia;

/// <summary>
/// Provides <see cref="IFrameFlowBuilder"/> extension methods for registering the
/// Avalonia video sink and legacy presenter adapters.
/// </summary>
public static class FrameFlowAvaloniaServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Avalonia video sink as the <see cref="IVideoSink"/>
    /// implementation for the DI-hosted FrameFlow application, along with
    /// <see cref="CpuFramePool"/> as <see cref="IFramePool"/>.
    /// </summary>
    /// <param name="builder">
    /// The <see cref="IFrameFlowBuilder"/> returned from
    /// <see cref="FrameFlowServiceCollectionExtensions.AddFrameFlow"/>.
    /// </param>
    /// <returns>The <paramref name="builder"/> instance for continued chaining.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="IVideoSink"/> is registered as a singleton. Avalonia sinks
    /// are bound to a specific UI surface. If per-session or per-window sink isolation
    /// is required in a future release, this lifetime decision should be revisited.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// services
    ///     .AddFrameFlow()
    ///     .AddFrameFlowAvaloniaVideoSink();
    /// </code>
    /// </example>
    public static IFrameFlowBuilder AddFrameFlowAvaloniaVideoSink(this IFrameFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Register the frame pool the sink needs.
        builder.Services.TryAddSingleton<IFramePool, CpuFramePool>();

        // Registered as singleton — bound to a specific Avalonia visual surface.
        // TryAdd ensures a consumer can substitute a different IVideoSink.
        builder.Services.TryAddSingleton<IVideoSink, AvaloniaVideoSink>();

        return builder;
    }

    /// <summary>
    /// Registers an <see cref="AvaloniaVideoSink"/> wired to the supplied
    /// <see cref="FrameFlowVideoView"/> so decoded frames pushed via
    /// <see cref="IVideoSink.PresentAsync"/> flow through to the view's
    /// render pass. This is the standard path for UI applications — the
    /// parameterless overload leaves the view disconnected and is intended
    /// for headless or test scenarios.
    /// </summary>
    /// <param name="builder">
    /// The <see cref="IFrameFlowBuilder"/> returned from
    /// <see cref="FrameFlowServiceCollectionExtensions.AddFrameFlow"/>.
    /// </param>
    /// <param name="view">The Avalonia control that will render frames.</param>
    /// <param name="logger">Optional logger for sink diagnostics.</param>
    /// <returns>The <paramref name="builder"/> instance for continued chaining.</returns>
    /// <remarks>
    /// The sink and its <see cref="CpuFramePool"/> are constructed eagerly and
    /// registered as singletons against their DI interfaces. The view's
    /// <see cref="FrameFlowVideoView.Sink"/> property is set before the method
    /// returns, so frames presented via the playback pipeline reach the UI from
    /// the first play onward.
    /// </remarks>
    /// <example>
    /// <code>
    /// services
    ///     .AddFrameFlow()
    ///     .AddFrameFlowDecoding()
    ///     .AddFrameFlowPlayback()
    ///     .AddFrameFlowAvaloniaVideoSink(VideoView)
    ///     .AddFrameFlowOpenAlAudio();
    /// </code>
    /// </example>
    public static IFrameFlowBuilder AddFrameFlowAvaloniaVideoSink(
        this IFrameFlowBuilder builder,
        FrameFlowVideoView view,
        ILogger<AvaloniaVideoSink>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(view);

        var framePool = new CpuFramePool(NullLogger<CpuFramePool>.Instance);
        var sink = new AvaloniaVideoSink(framePool, logger);
        view.Sink = sink;

        builder.Services.TryAddSingleton<IFramePool>(framePool);
        builder.Services.TryAddSingleton<IVideoSink>(sink);

        return builder;
    }
}
