// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.DependencyInjection;

namespace FrameFlow;

/// <summary>
/// A builder returned from <see cref="FrameFlowServiceCollectionExtensions.AddFrameFlow"/>
/// that allows optional FrameFlow adapters to register themselves into the same
/// <see cref="IServiceCollection"/>.
/// </summary>
/// <remarks>
/// This interface supports the fluent adapter registration pattern:
/// <code>
/// services
///     .AddFrameFlow(options => { ... })
///     .AddFrameFlowOpenAlAudio()
///     .AddFrameFlowAvaloniaVideoSink();
/// </code>
///
/// Adapter projects extend this interface with their own extension methods
/// rather than defining separate top-level extension methods on <see cref="IServiceCollection"/>.
/// This ensures adapter registrations are always chained from a FrameFlow-aware
/// starting point, reducing the chance of misconfigured service graphs.
/// </remarks>
public interface IFrameFlowBuilder
{
    /// <summary>
    /// Gets the underlying <see cref="IServiceCollection"/> that FrameFlow services
    /// are being registered into.
    /// </summary>
    IServiceCollection Services { get; }
}
