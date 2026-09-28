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
/// A configurator returns its chain open and the builder terminates it at the registered sink,
/// so an example that used to end its chain with a <c>SinkNode</c> body registers that body
/// here instead. The frames then arrive through the clock-select pacer like any other sink's,
/// rather than at decode rate.
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
    /// What the delegate's presenters keep after it returns, as the caller declares it
    /// (ADR-0081). <see langword="null"/> when the caller does not say.
    /// </summary>
    public int? MaxHeldFrames => maxHeldFrames;

    /// <summary>
    /// The memory domains the delegate's presenters take, as the caller declares them (#435).
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
