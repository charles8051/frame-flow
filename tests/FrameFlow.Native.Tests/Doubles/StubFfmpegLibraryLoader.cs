using FrameFlow.Media;
using FrameFlow.Native;

namespace FrameFlow.Native.Tests.Doubles;

/// <summary>
/// A test double for <see cref="IFfmpegLibraryLoader"/> that always reports success without
/// performing any native loading. Used in unit tests that exercise routing and priority logic
/// without requiring real FFmpeg binaries.
/// </summary>
internal sealed class StubFfmpegLibraryLoader : IFfmpegLibraryLoader
{
    /// <summary>
    /// The packed <c>avutil</c> version integer to report on success.
    /// Defaults to a plausible FFmpeg 7.x value (59.x.x → 59 &lt;&lt; 16).
    /// </summary>
    public uint AvutilVersion { get; set; } = (59u << 16) | (8u << 8) | 100u;

    /// <summary>
    /// When <see langword="true"/> (the default), <see cref="TryLoad"/> returns a success result.
    /// Set to <see langword="false"/> to simulate a missing-library failure.
    /// </summary>
    public bool SimulateSuccess { get; set; } = true;

    /// <summary>
    /// The error message to include in the failure result when <see cref="SimulateSuccess"/>
    /// is <see langword="false"/>.
    /// </summary>
    public string FailureMessage { get; set; } = "Stub: FFmpeg not available.";

    /// <summary>Tracks how many times <see cref="TryLoad"/> was invoked.</summary>
    public int CallCount { get; private set; }

    /// <summary>The last <paramref name="searchPath"/> passed to <see cref="TryLoad"/>.</summary>
    public string? LastSearchPath { get; private set; }

    /// <summary>The last <paramref name="source"/> passed to <see cref="TryLoad"/>.</summary>
    public FfmpegBinarySource LastSource { get; private set; }

    /// <inheritdoc />
    public FfmpegLoadResult TryLoad(string? searchPath, FfmpegBinarySource source)
    {
        CallCount++;
        LastSearchPath = searchPath;
        LastSource = source;

        return SimulateSuccess
            ? FfmpegLoadResult.Success(AvutilVersion)
            : FfmpegLoadResult.Failure(FailureMessage);
    }
}
