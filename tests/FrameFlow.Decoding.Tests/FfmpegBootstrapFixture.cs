using FrameFlow.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// XUnit collection fixture that bootstraps FFmpeg before integration tests run.
/// Resolves the FFmpeg library directory and initializes the native bindings so
/// that <c>avformat</c>, <c>avutil</c>, and other libraries are loadable via P/Invoke.
/// </summary>
/// <remarks>
/// This fixture is shared across all test classes in the
/// <see cref="FfmpegIntegrationCollection"/> xUnit collection. Bootstrap happens
/// once per test run rather than once per test, matching the native library
/// singleton semantics.
/// </remarks>
public sealed class FfmpegBootstrapFixture : IDisposable
{
    // Process-singleton bootstrap. See FrameFlow.Audio.Tests'
    // FfmpegBootstrapFixture for the rationale (HardwareDecodeProbe
    // calls av_hwdevice_ctx_create which is not thread-safe).
    private static readonly object _gate = new();
    private static bool? _cachedIsBootstrapped;

    /// <summary>
    /// <see langword="true"/> when FFmpeg was successfully bootstrapped.
    /// Integration tests should skip when this is <see langword="false"/>.
    /// </summary>
    public bool IsBootstrapped { get; }

    public FfmpegBootstrapFixture()
    {
        lock (_gate)
        {
            _cachedIsBootstrapped ??= TryBootstrap();
            IsBootstrapped = _cachedIsBootstrapped.Value;
        }
    }

    private static bool TryBootstrap()
    {
        var libraryDir = TestEnvironment.FindFfmpegLibraryDirectory();
        if (libraryDir is null)
            return false;

        try
        {
            var options = new FrameFlowNativeOptions { CustomFfmpegPath = libraryDir };
            var bootstrapper = new FrameFlowBootstrapper(options, NullLoggerFactory.Instance);
            return bootstrapper.Initialize().IsSuccess;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() { }
}
