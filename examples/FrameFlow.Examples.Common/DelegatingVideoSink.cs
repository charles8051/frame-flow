// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Examples.Common;

/// <summary>
/// An <see cref="IVideoSink"/> built from a delegate, for an example whose terminal is a few
/// lines of its own rather than a presenter.
/// </summary>
/// <remarks>
/// <para>
/// Register it with <c>WithVideoSink</c> and return the configurator's chain open: the builder
/// ends the chain at this sink, and a player hands it each frame at the frame's presentation
/// time, as it would any other sink.
/// </para>
/// <para>
/// The delegate takes ownership of each frame, as <see cref="IVideoSink.PresentAsync"/> does:
/// it disposes the frame, or hands it to something that will.
/// </para>
/// </remarks>
public sealed class DelegatingVideoSink(
    Func<IVideoFrame, CancellationToken, ValueTask> present,
    int? maxHeldFrames = null,
    FrameMemoryDomains acceptedDomains = FrameMemoryDomains.Cpu
) : IVideoSink
{
    private readonly Func<IVideoFrame, CancellationToken, ValueTask> _present =
        present ?? throw new ArgumentNullException(nameof(present));

    /// <summary>
    /// How many frames the delegate keeps after it returns, as the caller declares it.
    /// <see langword="null"/> when the caller does not say, which is read as unbounded.
    /// </summary>
    public int? MaxHeldFrames => maxHeldFrames;

    /// <summary>
    /// The memory domains of the frames the delegate takes, as the caller declares them.
    /// CPU when the caller does not say.
    /// </summary>
    public FrameMemoryDomains AcceptedDomains => acceptedDomains;

    /// <inheritdoc />
    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct) => _present(frame, ct);

    /// <inheritdoc />
    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) => default;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => default;
}
