// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media.Core;

namespace FrameFlow.Media;

/// <summary>
/// One sink a <see cref="FanOutVideoSink"/> presents to, and the transform, if any, that makes
/// the frame it is given (ADR-0085, decision 8).
/// </summary>
/// <remarks>
/// <para>
/// A target made with <see cref="Of"/> is given each frame as the fan-out is. One made with
/// <see cref="Transform"/> is given a frame derived from it, such as a filtered copy, in the same
/// present as the other targets.
/// </para>
/// <para>
/// A fan-out over plain sinks takes them directly: <c>new FanOutVideoSink(first, second)</c>.
/// Name each target with this type when any of them has a transform.
/// </para>
/// </remarks>
public sealed class FanOutTarget
{
    private readonly Func<IVideoFrame, IVideoFrame>? _transform;
    private readonly FrameMemoryDomains _sinkDomains;

    private FanOutTarget(
        IVideoSink sink,
        Func<IVideoFrame, IVideoFrame>? transform,
        FrameMemoryDomains acceptedDomains
    )
    {
        Sink = sink;
        _transform = transform;
        _sinkDomains = sink.AcceptedDomains;
        AcceptedDomains = acceptedDomains;
    }

    /// <summary>A target that is given each frame as the fan-out is.</summary>
    /// <param name="sink">The sink to present to. The fan-out does not own it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is null.</exception>
    public static FanOutTarget Of(IVideoSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return new(sink, transform: null, sink.AcceptedDomains);
    }

    /// <summary>
    /// A target that is given what <paramref name="transform"/> makes from each frame.
    /// </summary>
    /// <param name="sink">The sink to present to. The fan-out does not own it.</param>
    /// <param name="transform">
    /// Makes the target's frame. It borrows the frame it is passed, which it must not dispose,
    /// and returns a frame the target then owns: a new frame, or another reference to the one it
    /// was passed (<see cref="IVideoFrame.AddRef"/>). Once it returns it keeps no reference to the
    /// frame it was passed, other than one it returned: <see cref="FanOutVideoSink.MaxHeldFrames"/>
    /// cannot count one it keeps. The frame it returns keeps the dimensions, pixel format, pixel
    /// shape and rotation of the one it was passed, so the format the target was told stays true,
    /// and is in memory the sink takes. One that does not is disposed, and that target's call
    /// fails. A change of format belongs in the graph, as a node before the sink.
    /// </param>
    /// <param name="acceptedDomains">
    /// The memory domains of the frames <paramref name="transform"/> takes, which stand in for the
    /// sink's own in <see cref="FanOutVideoSink.AcceptedDomains"/>. CPU when not given.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> or <paramref name="transform"/> is null.</exception>
    public static FanOutTarget Transform(
        IVideoSink sink,
        Func<IVideoFrame, IVideoFrame> transform,
        FrameMemoryDomains acceptedDomains = FrameMemoryDomains.Cpu
    )
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(transform);
        return new(sink, transform, acceptedDomains);
    }

    /// <summary>The sink this target presents to.</summary>
    public IVideoSink Sink { get; }

    /// <summary>
    /// The domains of the frames this target takes: its transform's when it has one, otherwise
    /// its sink's, read when the target was made.
    /// </summary>
    internal FrameMemoryDomains AcceptedDomains { get; }

    /// <summary>Whether the target's frame comes from a transform.</summary>
    internal bool HasTransform => _transform is not null;

    /// <summary>
    /// Presents <paramref name="frame"/>, which the caller keeps its own reference to, to this
    /// target: a reference taken just before the call, or the transform's output. The sink owns
    /// what it is given, even when its call throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The transform returned nothing, or a frame of another format or in memory the sink does not
    /// take, which is disposed.
    /// </exception>
    internal ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        if (_transform is null)
            return Sink.PresentAsync(frame.AddRef(), ct);

        var output =
            _transform(frame)
            ?? throw new InvalidOperationException(
                $"The transform for {Sink.GetType().Name} returned no frame. It returns a frame the target owns."
            );
        if (!FanOutRules.KeepsFormat(frame, output))
        {
            var made = Describe(output);
            output.Dispose();
            throw new InvalidOperationException(
                $"The transform for {Sink.GetType().Name} made a {made} frame from a {Describe(frame)} "
                    + "one. A transform keeps the frame's dimensions, pixel format, pixel shape and "
                    + "rotation; change the format in the graph, before the sink."
            );
        }
        if (!FanOutRules.Takes(_sinkDomains, output.MemoryDomain))
        {
            var domain = output.MemoryDomain;
            output.Dispose();
            throw new InvalidOperationException(
                $"The transform for {Sink.GetType().Name} made a frame in {domain} memory, and the "
                    + $"sink takes {_sinkDomains} frames."
            );
        }

        return Sink.PresentAsync(output, ct);
    }

    private static string Describe(IVideoFrame frame) =>
        $"{frame.Width}x{frame.Height} {frame.Format} (pixel shape {frame.SampleAspectRatio.Numerator}:"
        + $"{frame.SampleAspectRatio.Denominator}, rotation {frame.Rotation})";
}
