using FrameFlow.Media;

namespace FrameFlow.Decoding.Tests.Doubles;

/// <summary>
/// A controllable fake <see cref="IDemuxSessionFactory"/> that returns pre-configured
/// <see cref="FakeDemuxSession"/> instances without requiring real FFmpeg binaries.
/// </summary>
internal sealed class FakeDemuxSessionFactory : IDemuxSessionFactory
{
    private readonly Queue<IDemuxSession> _sessions = new();
    private bool _shouldThrow;
    private string _throwMessage = "Fake factory error";

    /// <summary>
    /// Enqueues a session to be returned by the next <see cref="OpenAsync"/> call.
    /// </summary>
    public void EnqueueSession(IDemuxSession session) => _sessions.Enqueue(session);

    /// <summary>
    /// Configures the factory to throw an <see cref="InvalidOperationException"/> on
    /// the next <see cref="OpenAsync"/> call.
    /// </summary>
    public void SetThrowOnOpen(string message = "Fake factory error")
    {
        _shouldThrow = true;
        _throwMessage = message;
    }

    /// <summary>Records each source passed to <see cref="OpenAsync"/>.</summary>
    public List<IMediaSource> OpenHistory { get; } = [];

    /// <inheritdoc/>
    public ValueTask<IDemuxSession> OpenAsync(
        IMediaSource source,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        OpenHistory.Add(source);

        if (_shouldThrow)
        {
            _shouldThrow = false;
            throw new InvalidOperationException(_throwMessage);
        }

        if (_sessions.Count == 0)
            throw new InvalidOperationException("FakeDemuxSessionFactory: no session queued.");

        return ValueTask.FromResult(_sessions.Dequeue());
    }
}
