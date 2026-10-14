// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// A video sink that hands each frame to a delegate: for a sink that ends in your own code, such
/// as one that writes frames to disk (ADR-0085, decision 8).
/// </summary>
/// <remarks>
/// <para>
/// Register it with <c>WithVideoSink</c>, or end a graph at it, and a player hands it each frame
/// at the frame's presentation time, as it would any other sink. The delegate takes ownership of
/// each frame, as <see cref="IVideoSink.PresentAsync"/> does: it disposes the frame, or hands it to
/// something that will.
/// </para>
/// <para>
/// <b>Declare what the delegate does.</b> <see cref="MaxHeldFrames"/> says how many frames it keeps
/// after it returns, and a decoder's pool of hardware frames is sized from it: left unset, it reads
/// as unbounded, and a path that hands the delegate hardware frames is refused (ADR-0081).
/// <see cref="AcceptedDomains"/> says which frames it reads, CPU unless set, and
/// <see cref="PreferredBackends"/> which hardware decoders it handles best.
/// </para>
/// <para>
/// <b>Do not present to another sink from it.</b> It cannot see what its delegate calls, so it
/// claims nothing (ADR-0085, decision 6): a player built against it holds no sink the delegate
/// presents to, and a second player can be built against that sink while the first still feeds
/// it. To show each frame in several sinks, use <see cref="FanOutVideoSink"/>, which passes the
/// claim on; to show it in one, register that sink itself.
/// </para>
/// <para>
/// <b>Disposal.</b> It owns nothing, and its delegate is not told. Once it is disposed it
/// disposes each frame it is given without calling the delegate (ADR-0085, decision 4), and a
/// second dispose does nothing.
/// </para>
/// </remarks>
public sealed class DelegatingVideoSink : IVideoSink
{
    private readonly Func<IVideoFrame, CancellationToken, ValueTask> _present;
    private readonly int? _maxHeldFrames;
    private readonly IReadOnlyList<HardwareDecodeBackendKind> _preferredBackends = [];
    private int _disposed;

    /// <summary>Creates a sink that hands each frame to <paramref name="present"/>.</summary>
    /// <param name="present">
    /// Called once per frame with the frame, which it then owns, and the present's cancellation
    /// token.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="present"/> is null.</exception>
    public DelegatingVideoSink(Func<IVideoFrame, CancellationToken, ValueTask> present)
    {
        ArgumentNullException.ThrowIfNull(present);
        _present = present;
    }

    /// <inheritdoc />
    /// <remarks>
    /// How many frames the delegate keeps after it returns, as you declare it.
    /// <see langword="null"/>, the default, reads as unbounded.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Set to a negative count.</exception>
    public int? MaxHeldFrames
    {
        get => _maxHeldFrames;
        init
        {
            if (value is { } count)
                ArgumentOutOfRangeException.ThrowIfNegative(count, nameof(MaxHeldFrames));
            _maxHeldFrames = value;
        }
    }

    /// <inheritdoc />
    /// <remarks>The memory domains of the frames the delegate reads, as you declare them. CPU by default.</remarks>
    public FrameMemoryDomains AcceptedDomains { get; init; } = FrameMemoryDomains.Cpu;

    /// <inheritdoc />
    /// <remarks>The hardware decode backends the delegate handles best, as you declare them. None by default.</remarks>
    /// <exception cref="ArgumentNullException">Set to null.</exception>
    public IReadOnlyList<HardwareDecodeBackendKind> PreferredBackends
    {
        get => _preferredBackends;
        init => _preferredBackends = value ?? throw new ArgumentNullException(nameof(PreferredBackends));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Hands the frame to the delegate. After <see cref="DisposeAsync"/> it disposes the frame
    /// instead.
    /// </remarks>
    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (Volatile.Read(ref _disposed) != 0)
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        return _present(frame, ct);
    }

    /// <inheritdoc />
    /// <remarks>Does nothing: each frame carries its own format.</remarks>
    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(format);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>A second call does nothing.</remarks>
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }
}
