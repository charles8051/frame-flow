// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Player;

/// <summary>
/// Entry point for a <see cref="MediaPass"/>: one traversal of a source, at decode speed.
/// <code>
/// await using var sink = new HeadlessVideoSink();
/// await using var pass = await FrameFlowPass
///     .Create(path)
///     .WithVideoSink(sink)
///     .ConfigureVideo(chain =&gt; chain.Then(detect))
///     .BuildAsync();
///
/// await pass.RunToCompletionAsync(ct);
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// <b>Which entry point.</b> A pass waits on no presentation time, so video through it runs as
/// fast as the sink accepts. That is what an analysis or inference run over a file wants, and it
/// is not what a viewer wants. Use <see cref="FrameFlowPlayer"/> when a human is watching: it
/// honours a clock and carries pause, resume, seek, repeat and the queue.
/// </para>
/// <para>
/// An audio sink paces itself by what its device consumes, so an audio-only pass plays at the
/// rate the device takes it. That is the device's doing, not the pass's.
/// </para>
/// <para>
/// <b>The source is named here.</b> A pass with no source has nothing to do, so it is an argument
/// rather than an option, and there is no chain that can reach <see cref="IPassBuilder.BuildAsync"/>
/// with nothing to open. One source, and no queue: the thing worth reusing across files is the
/// sink, and the caller owns that already.
/// </para>
/// <para>Nothing is opened here. The demuxer runs in <see cref="IPassBuilder.BuildAsync"/>.</para>
/// </remarks>
public static class FrameFlowPass
{
    /// <summary>Begins a pass over the media at <paramref name="path"/>.</summary>
    public static IPassBuilder Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new PassBuilder(MediaSource.FromFile(path));
    }

    /// <summary>Begins a pass over an arbitrary <see cref="IMediaSource"/>.</summary>
    public static IPassBuilder Create(IMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new PassBuilder(source);
    }
}
