// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// A no-op <see cref="IVideoSink"/> that immediately disposes every frame
/// presented to it. Useful for headless benchmarks, testing, and pipelines
/// where video output is not needed.
/// </summary>
/// <remarks>
/// Moved from <c>FrameFlow.Playback</c> to <c>FrameFlow.Media</c> during
/// the substrate migration. Sinks (Avalonia / SDL) and examples
/// consume it — anchoring it in the substrate-neutral Media assembly keeps
/// them from transitively pulling <c>FrameFlow.Playback</c>.
/// </remarks>
public sealed class NullVideoSink : IVideoSink
{
    /// <inheritdoc />
    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        frame.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
