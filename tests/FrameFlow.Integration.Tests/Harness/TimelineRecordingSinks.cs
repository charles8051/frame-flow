using System.Collections.Concurrent;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Integration.Tests.Harness;

/// <summary>An item a sink was handed: its presentation time and its timeline.</summary>
internal readonly record struct Stamped(TimeSpan Pts, TimelineId Timeline);

/// <summary>
/// Items a sink was handed, in order, with a wait for the first that matches. Shared by the video
/// and audio recording sinks.
/// </summary>
internal sealed class StampRecorder
{
    private readonly ConcurrentQueue<Stamped> _items = new();
    private readonly Lock _gate = new();
    private readonly List<(Func<Stamped, bool> Match, TaskCompletionSource Signal)> _waiters = [];

    /// <summary>Every item, in delivery order.</summary>
    public IReadOnlyList<Stamped> Items => _items.ToArray();

    /// <summary>How many items have been delivered.</summary>
    public int Count => _items.Count;

    /// <summary>
    /// Completes once an item that <paramref name="match"/> accepts has been delivered, ignoring the
    /// first <paramref name="skip"/>, whether it came before this call or comes after it.
    /// </summary>
    public Task WhenDelivered(Func<Stamped, bool> match, int skip = 0)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_items.Skip(skip).Any(match))
            {
                signal.TrySetResult();
                return signal.Task;
            }
            _waiters.Add((match, signal));
        }
        return signal.Task;
    }

    public void Record(Stamped item)
    {
        // Enqueued first, so a waiter registered after this point finds the item in the queue and
        // one registered before it is found here.
        _items.Enqueue(item);
        lock (_gate)
        {
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Match(item))
                {
                    _waiters[i].Signal.TrySetResult();
                    _waiters.RemoveAt(i);
                }
            }
        }
    }
}

/// <summary>Records the timestamp and timeline of every frame it is handed, and releases it.</summary>
internal sealed class TimelineRecordingVideoSink : IVideoSink
{
    public StampRecorder Recorder { get; } = new();

    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        var stamped = new Stamped(frame.Pts, frame.Timeline);
        frame.Dispose();
        Recorder.Record(stamped);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Records the timestamp and timeline of every buffer it is handed, and releases it. It has no
/// device, so the player paces against a wallclock.
/// </summary>
internal sealed class TimelineRecordingAudioSink : IAudioSink
{
    // Ticks of the position the sink stops taking audio at, or -1 for none.
    private long _holdFrom = -1;

    public StampRecorder Recorder { get; } = new();

    /// <summary>
    /// Takes no buffer after the first at or past <paramref name="position"/> until the run that
    /// delivered it stops, as a device whose buffer is full at that position would. The audio
    /// decoder's packet queue then holds the packets read after it. One run only: the next run's
    /// buffers are taken as usual.
    /// </summary>
    public void HoldFrom(TimeSpan position) => Interlocked.Exchange(ref _holdFrom, position.Ticks);

    public async ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct)
    {
        var stamped = new Stamped(buffer.Timestamp, buffer.Timeline);
        buffer.Dispose();
        Recorder.Record(stamped);

        var holdFrom = Interlocked.Read(ref _holdFrom);
        if (holdFrom < 0 || stamped.Pts.Ticks < holdFrom)
            return;

        // Completes only when the run is cancelled, which is how a seek or a dispose stops it.
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (ct.Register(() => stopped.TrySetResult()))
            await stopped.Task.ConfigureAwait(false);
        Interlocked.Exchange(ref _holdFrom, -1);
        ct.ThrowIfCancellationRequested();
    }

    public ValueTask ActivateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask PauseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask DeactivateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// The runs a player's sessions launched, in order, read from the session's log line for each
/// launch (<see cref="SubstrateSession.RunLaunchedMessage"/>): the session's run number and the
/// timeline it minted.
/// </summary>
internal sealed class LaunchLog : ILoggerFactory, ILogger
{
    private readonly ConcurrentQueue<(int RunNumber, TimelineId Timeline)> _launches = new();

    public IReadOnlyList<(int RunNumber, TimelineId Timeline)> Launches => _launches.ToArray();

    public ILogger CreateLogger(string categoryName) => this;

    public void AddProvider(ILoggerProvider provider) { }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
            return;

        // The message's arguments come first, in order, and its template last.
        if (
            values.Count == 3
            && values[2].Value is string format
            && format == SubstrateSession.RunLaunchedMessage
            && values[0].Value is int run
            && values[1].Value is TimelineId timeline
        )
        {
            _launches.Enqueue((run, timeline));
        }
    }

    public void Dispose() { }
}

/// <summary>Reads a sink's record as the timelines it went through.</summary>
internal static class TimelineRecord
{
    /// <summary>A stretch of consecutive items on one timeline.</summary>
    internal sealed record Segment(TimelineId Timeline, IReadOnlyList<TimeSpan> Pts);

    /// <summary>
    /// Splits <paramref name="items"/> into stretches on one timeline, and asserts that every item
    /// names a timeline and that each stretch is on one no earlier stretch was on: a break starts a
    /// new timeline, and the items between two breaks share one.
    /// </summary>
    public static IReadOnlyList<Segment> AssertEachBreakStartsANewTimeline(IReadOnlyList<Stamped> items, string stream)
    {
        Assert.NotEmpty(items);
        Assert.DoesNotContain(items, item => item.Timeline == TimelineId.None);

        var segments = new List<Segment>();
        foreach (var item in items)
        {
            if (segments.Count > 0 && segments[^1].Timeline == item.Timeline)
                ((List<TimeSpan>)segments[^1].Pts).Add(item.Pts);
            else
                segments.Add(new Segment(item.Timeline, new List<TimeSpan> { item.Pts }));
        }

        var ids = segments.Select(s => s.Timeline).ToList();
        Assert.True(
            ids.Distinct().Count() == ids.Count,
            $"{stream}: a timeline came back after another took over: {string.Join(", ", segments.Select(Describe))}"
        );
        return segments;
    }

    /// <summary>
    /// Asserts video and audio went through the same timelines in the same order, a run's video and
    /// audio sharing its id.
    /// </summary>
    /// <remarks>
    /// Both start on the run that was played first, and every timeline both went through is in the
    /// same order in each. Two allowances, both for a run one stream reached and the other did not.
    /// One stream may have reached the next timeline when the other had not yet, so one of them
    /// may end with one timeline the other lacks. And a run a break cut short may have reached only
    /// one stream: the audio sink takes buffers as fast as they decode and video is presented when
    /// it is due, so a run cut before its first frame was due has audio and no video, and how often
    /// that happens depends on how the runner was scheduled. Such a run sits between timelines both
    /// streams went through.
    /// </remarks>
    public static void AssertSameTimelines(IReadOnlyList<Segment> video, IReadOnlyList<Segment> audio)
    {
        var v = video.Select(s => s.Timeline).ToList();
        var a = audio.Select(s => s.Timeline).ToList();
        var shown = $"video {string.Join(", ", v)}; audio {string.Join(", ", a)}";
        Assert.True(v[0] == a[0], $"video and audio started on different timelines: {shown}");

        var shared = v.Intersect(a).ToHashSet();
        Assert.Equal(v.Where(shared.Contains), a.Where(shared.Contains));

        var videoLead = v.Count - 1 - v.FindLastIndex(shared.Contains);
        var audioLead = a.Count - 1 - a.FindLastIndex(shared.Contains);
        Assert.True(
            videoLead + audioLead <= 1,
            $"after the last timeline both went through, video went through {videoLead} more and "
                + $"audio {audioLead}: {shown}"
        );
    }

    public static string Describe(Segment segment) =>
        $"{segment.Timeline} [{segment.Pts[0].TotalMilliseconds:F0} .. {segment.Pts[^1].TotalMilliseconds:F0} ms, {segment.Pts.Count}]";
}
