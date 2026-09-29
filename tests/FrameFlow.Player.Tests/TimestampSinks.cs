using System.Collections.Concurrent;
using FrameFlow.Graph;

namespace FrameFlow.Player.Tests;

/// <summary>Records each frame's timestamp.</summary>
internal sealed class TimestampSink : IVideoSink
{
    public ConcurrentQueue<TimeSpan> Timestamps { get; } = new();

    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        Timestamps.Enqueue(frame.Pts);
        frame.Dispose();
        return ValueTask.CompletedTask;
    }

    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Records each audio buffer's timestamp and duration.</summary>
internal sealed class TimestampAudioSink : IAudioSink
{
    public ConcurrentQueue<(TimeSpan Start, TimeSpan Duration)> Buffers { get; } = new();

    public ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct)
    {
        Buffers.Enqueue((buffer.Timestamp, buffer.Duration));
        buffer.Dispose();
        return ValueTask.CompletedTask;
    }

    public ValueTask ActivateAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask PauseAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask DeactivateAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
