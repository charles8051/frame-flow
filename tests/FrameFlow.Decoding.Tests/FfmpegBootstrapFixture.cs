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
    private static HardwareDecodeCapabilities _cachedCapabilities = HardwareDecodeCapabilities.Empty;

    /// <summary>
    /// <see langword="true"/> when FFmpeg was successfully bootstrapped.
    /// Integration tests should skip when this is <see langword="false"/>.
    /// </summary>
    public bool IsBootstrapped { get; }

    /// <summary>
    /// The hardware-decode backends the bootstrap's probe found, or
    /// <see cref="HardwareDecodeCapabilities.Empty"/> when it did not run.
    /// </summary>
    public HardwareDecodeCapabilities Capabilities => ReadCapabilities();

    public FfmpegBootstrapFixture()
    {
        lock (_gate)
        {
            _cachedIsBootstrapped ??= TryBootstrap();
            IsBootstrapped = _cachedIsBootstrapped.Value;
        }
    }

    /// <summary>
    /// Bootstraps if nothing has yet, and returns what the probe found. Shared by the fixture
    /// and by <see cref="RequiresHardwareDecodeFactAttribute"/>, so the two cannot race the
    /// probe against each other.
    /// </summary>
    internal static HardwareDecodeCapabilities ReadCapabilities()
    {
        lock (_gate)
        {
            _cachedIsBootstrapped ??= TryBootstrap();
            return _cachedCapabilities;
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
            var result = bootstrapper.Initialize();
            if (result.IsSuccess)
                _cachedCapabilities = result.Capabilities;
            return result.IsSuccess;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() { }
}
