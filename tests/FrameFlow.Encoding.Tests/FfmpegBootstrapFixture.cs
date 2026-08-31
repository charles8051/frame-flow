using FrameFlow.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Encoding.Tests;

/// <summary>
/// XUnit class fixture that bootstraps FFmpeg once before the encoder
/// round-trip tests run, so <c>avcodec</c> / <c>avformat</c> / <c>avutil</c> /
/// <c>swscale</c> P/Invoke calls resolve the shared libraries. Mirrors the
/// fixture in <c>FrameFlow.Decoding.Tests</c>.
/// </summary>
public sealed class FfmpegBootstrapFixture
{
    private static readonly object _gate = new();
    private static bool? _cachedIsBootstrapped;

    /// <summary><see langword="true"/> when FFmpeg was successfully bootstrapped.</summary>
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
}
