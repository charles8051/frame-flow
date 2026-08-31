using FrameFlow.Media;

namespace FrameFlow.Decoding.Tests.Doubles;

/// <summary>
/// A controllable fake implementation of <see cref="IDemuxSession"/> for use in tests
/// that need to exercise the demux contract without requiring real FFmpeg binaries.
/// </summary>
/// <remarks>
/// This double allows tests to:
/// <list type="bullet">
///   <item>Pre-load a sequence of packets to be returned by <see cref="ReadPacketAsync"/>.</item>
///   <item>Observe seek calls via <see cref="SeekHistory"/>.</item>
///   <item>Verify disposal via <see cref="IsDisposed"/>.</item>
///   <item>Simulate EOF by exhausting the packet queue (returns <see langword="null"/>).</item>
/// </list>
/// </remarks>
internal sealed class FakeDemuxSession : IDemuxSession
{
    private readonly Queue<DemuxPacket> _packets;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="FakeDemuxSession"/> with the given
    /// <paramref name="mediaInfo"/> and an optional sequence of packets to return.
    /// </summary>
    public FakeDemuxSession(MediaInfo mediaInfo, IEnumerable<DemuxPacket>? packets = null)
    {
        MediaInfo = mediaInfo ?? throw new ArgumentNullException(nameof(mediaInfo));
        _packets = packets is null ? new Queue<DemuxPacket>() : new Queue<DemuxPacket>(packets);
    }

    /// <inheritdoc/>
    public MediaInfo MediaInfo { get; }

    /// <summary>
    /// Records the positions passed to each <see cref="SeekAsync"/> call in order.
    /// </summary>
    public List<TimeSpan> SeekHistory { get; } = [];

    /// <summary>
    /// <see langword="true"/> after <see cref="DisposeAsync"/> has been called.
    /// </summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Returns the next packet from the pre-loaded queue, or <see langword="null"/>
    /// when the queue is empty (simulating EOF).
    /// </summary>
    public ValueTask<DemuxPacket?> ReadPacketAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        DemuxPacket? packet = _packets.Count > 0 ? _packets.Dequeue() : null;
        return ValueTask.FromResult(packet);
    }

    /// <inheritdoc/>
    public ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        SeekHistory.Add(position);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
