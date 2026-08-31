// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FrameFlow.Native;

/// <summary>
/// An <see cref="IHostedService"/> that eagerly initializes the FrameFlow native bootstrap
/// (FFmpeg binary loading and codec probing) at application startup.
/// </summary>
/// <remarks>
/// Register this service by calling
/// <c>services.AddFrameFlow().AddHostedBootstrap()</c> rather than registering
/// <see cref="FrameFlowHostedService"/> directly.
/// </remarks>
public sealed class FrameFlowHostedService : IHostedService
{
    private readonly IFrameFlowBootstrapper _bootstrapper;
    private readonly ILogger<FrameFlowHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="FrameFlowHostedService"/>.
    /// </summary>
    /// <param name="bootstrapper">The native bootstrapper to initialize at startup.</param>
    /// <param name="logger">Logger for recording bootstrap results and diagnostics.</param>
    public FrameFlowHostedService(
        IFrameFlowBootstrapper bootstrapper,
        ILogger<FrameFlowHostedService> logger
    )
    {
        ArgumentNullException.ThrowIfNull(bootstrapper);
        ArgumentNullException.ThrowIfNull(logger);

        _bootstrapper = bootstrapper;
        _logger = logger;
    }

    /// <summary>
    /// Runs the native bootstrap at application startup.
    /// </summary>
    /// <param name="cancellationToken">
    /// Propagated from the host's startup sequence. Bootstrap is expected to complete
    /// synchronously — this token is not passed into <see cref="IFrameFlowBootstrapper.Initialize"/>
    /// because initialization does not currently support cancellation.
    /// </param>
    /// <returns>A completed task if bootstrap succeeds.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the bootstrap reports failure, surfacing the failure at startup rather
    /// than lazily on the first session creation attempt.
    /// </exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("FrameFlow: starting native bootstrap.");

        var result = _bootstrapper.Initialize();

        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "FrameFlow: native bootstrap succeeded. Source={BinarySource}, Path={ResolvedPath}",
                result.BinarySource,
                result.ResolvedPath ?? "(default)"
            );
        }
        else
        {
            _logger.LogError(
                "FrameFlow: native bootstrap failed. Message={Message}",
                result.Message
            );

            throw new InvalidOperationException(
                $"FrameFlow native bootstrap failed: {result.Message}"
            );
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// No-op on host shutdown — native bootstrap is not reversible within a process lifetime.
    /// </summary>
    /// <param name="cancellationToken">Ignored.</param>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
