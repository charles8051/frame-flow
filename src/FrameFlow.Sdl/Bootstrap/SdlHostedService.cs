// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrameFlow.SDL.Bootstrap;

/// <summary>
/// Hosted service that initializes <see cref="ISdlBootstrapper"/> at application startup,
/// ensuring SDL2 is resolved before any consumer requests a <see cref="Silk.NET.SDL.Sdl"/> instance.
/// </summary>
/// <remarks>
/// Register via <c>AddHostedSdlBootstrap()</c>. Without this service, consumers must call
/// <see cref="ISdlBootstrapper.Initialize"/> explicitly before invoking
/// <see cref="ISdlBootstrapper.CreateSdlApi"/>.
/// </remarks>
public sealed class SdlHostedService : IHostedService
{
    private readonly ISdlBootstrapper _bootstrapper;
    private readonly ILogger<SdlHostedService> _logger;

    /// <param name="bootstrapper">The SDL bootstrapper singleton to initialize.</param>
    /// <param name="logger">Logger for startup diagnostics.</param>
    public SdlHostedService(ISdlBootstrapper bootstrapper, ILogger<SdlHostedService> logger)
    {
        _bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("SdlHostedService: initializing SDL bootstrapper.");
        var result = _bootstrapper.Initialize();

        if (!result.IsSuccess)
        {
            _logger.LogError("SDL bootstrap failed at startup: {Message}", result.Message);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
