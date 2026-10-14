// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding;
using FrameFlow.Playback;
using FrameFlow.Playback.Core;

namespace FrameFlow.Player.Tests;

/// <summary>
/// A video sink that throws, behind a real player and pass (#703). Under a player, one failure
/// costs a frame and is raised on <see cref="IMediaTransport.ErrorOccurred"/> while the player
/// plays on; a sink that keeps failing, counted across the items it presents, fails the item
/// playing, as a fault in its decoding does. A pass stops at the first failure and its run throws
/// it.
/// </summary>
/// <remarks>
/// Each test waits for the player to end, or the run to throw, before it asserts what did not
/// happen. The player tears the item down before it reports the end, and an error is raised on
/// the same queue as the end, ahead of it.
/// </remarks>
public sealed class VideoSinkFailureTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    // Half a second at 30 fps: fifteen frames.
    private const string ShortClip = "test-subsecond.mp4";

    // Three seconds at 24 fps: seventy-two frames, far more than a sink that always throws is
    // called before it faults, and long enough that the fault comes well inside half its length,
    // so it does not count as progress and counts toward the playlist's limit.
    private const string LongerClip = "test-video-h264-yuv420p.mp4";

    // A quarter of a second of LongerClip: six frames, fewer than the run of failures that faults.
    private static readonly TimeSpan ShortItem = TimeSpan.FromMilliseconds(250);

    private static string Corpus(string name) =>
        TestEnvironment.GetCorpusFile(name) ?? throw new InvalidOperationException($"{name} is missing.");

    [RequiresFfmpegAndCorpusFact]
    public async Task ASinkThatThrowsOnce_LeavesThePlayerPlaying_AndRaisesOneError()
    {
        var sink = new ThrowingVideoSink(call => call == 2);
        await using var player = await BuildAsync(Corpus(ShortClip), sink);
        var errors = new List<(PlaybackError Error, PlaybackState State)>();
        var states = new List<PlaybackState>();
        using var onError = player.ErrorOccurred.Subscribe(e =>
        {
            lock (errors)
                errors.Add((e, player.State));
        });
        using var onState = player.StateChanged.Subscribe(s =>
        {
            lock (states)
                states.Add(s);
        });

        var ended = player.WaitForEndAsync();
        Assert.True((await player.PlayAsync()).IsSuccess);
        var result = await ended.WaitAsync(Bound);

        Assert.True(result.IsSuccess, result.Error?.Message);
        (PlaybackError Error, PlaybackState State)[] raised;
        lock (errors)
            raised = [.. errors];
        var (error, stateThen) = Assert.Single(raised);
        Assert.Same(sink.Thrown[0], error.Inner);
        Assert.Equal(PlaybackState.Playing, stateThen);
        lock (states)
            Assert.DoesNotContain(PlaybackState.Error, states);
        Assert.Equal(PlaybackState.Ended, player.State);
        Assert.True(sink.Taken > 1, $"The sink took {sink.Taken} frame(s): delivery did not go on after the failure.");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ASinkThatAlwaysThrows_FailsTheItem_AndAQueueOfOneEnds()
    {
        var sink = new ThrowingVideoSink(_ => true);
        await using var player = await BuildAsync(Corpus(LongerClip), sink);
        var errors = new List<PlaybackError>();
        var failed = new List<PlaylistItemFailed>();
        using var onError = player.ErrorOccurred.Subscribe(e =>
        {
            lock (errors)
                errors.Add(e);
        });
        using var onItemFailed = player.ItemFailed.Subscribe(f =>
        {
            lock (failed)
                failed.Add(f);
        });

        var ended = player.WaitForEndAsync();
        Assert.True((await player.PlayAsync()).IsSuccess);
        var result = await ended.WaitAsync(Bound);

        // A last item that faults while it plays ends the queue, as any fault does under
        // RepeatMode.Off: the item failed, and the player is not left playing a frozen picture.
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(PlaybackState.Ended, player.State);
        PlaylistItemFailed[] failures;
        PlaybackError[] raised;
        lock (failed)
            failures = [.. failed];
        lock (errors)
            raised = [.. errors];
        var failure = Assert.Single(failures);
        Assert.Equal(PlaylistItemFailure.FaultedDuringPlayback, failure.Failure);
        var fault = Assert.IsType<InvalidOperationException>(failure.Error.Inner);
        Assert.Same(sink.Thrown[^1], fault.InnerException);
        // The first failure is reported, the rest fall inside its interval, and then the item's.
        Assert.Equal(2, raised.Length);
        Assert.Same(sink.Thrown[0], raised[0].Inner);
        Assert.Same(failure.Error, raised[1]);
        Assert.Equal(SinkFailureRun.MaxConsecutiveFailures + 1, sink.Calls);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ASinkThatAlwaysThrows_FailsItemsShorterThanTheLimit_ByCountingAcrossThem()
    {
        // Each item has fewer frames than the run of failures that faults. Counted per item, no
        // item would ever fault, and a broken sink would play the queue through to its end.
        var sink = new ThrowingVideoSink(_ => true);
        var path = Corpus(LongerClip);
        await using var player = await BuildAsync(new MediaClip(MediaSource.FromFile(path), end: ShortItem), sink);
        var second = await player.EnqueueAsync(new MediaClip(MediaSource.FromFile(path), end: ShortItem));
        var third = await player.EnqueueAsync(new MediaClip(MediaSource.FromFile(path), end: ShortItem));
        var failed = new List<PlaylistItemFailed>();
        using var onItemFailed = player.ItemFailed.Subscribe(f =>
        {
            lock (failed)
                failed.Add(f);
        });

        var ended = player.WaitForEndAsync();
        Assert.True((await player.PlayAsync()).IsSuccess);
        var result = await ended.WaitAsync(Bound);

        Assert.True(result.IsSuccess, result.Error?.Message);
        PlaylistItemFailed[] failures;
        lock (failed)
            failures = [.. failed];
        // The run reaches the limit in the second item, or in the third when frames were dropped
        // late, and once the sink has faulted, the item after it faults at its first frame.
        Assert.NotEmpty(failures);
        Assert.All(failures, f => Assert.Equal(PlaylistItemFailure.FaultedDuringPlayback, f.Failure));
        Assert.Contains(failures, f => f.Item == third);
        Assert.All(failures, f => Assert.True(f.Item == second || f.Item == third));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ASinkThatAlwaysThrows_PutsARepeatingPlayerInError_OnceTheItemKeepsFailing()
    {
        var sink = new ThrowingVideoSink(_ => true);
        await using var player = await BuildAsync(Corpus(LongerClip), sink, RepeatMode.One);
        var failed = 0;
        using var onItemFailed = player.ItemFailed.Subscribe(_ => Interlocked.Increment(ref failed));

        var ended = player.WaitForEndAsync();
        Assert.True((await player.PlayAsync()).IsSuccess);
        var result = await ended.WaitAsync(Bound);

        // The item plays again after each fault, and faults again at its first frame, until the
        // playlist gives up.
        Assert.False(result.IsSuccess);
        Assert.Equal(PlaybackState.Error, player.State);
        Assert.Equal(PlaylistFailureGuard.MaxConsecutiveFailures + 1, Volatile.Read(ref failed));
        Assert.Equal(
            SinkFailureRun.MaxConsecutiveFailures + PlaylistFailureGuard.MaxConsecutiveFailures + 1,
            sink.Calls
        );
        Assert.Same(sink.Thrown[^1], Innermost(result.Error!.Inner));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APassWhoseVideoSinkThrows_StopsThere_AndItsRunThrowsTheFailure()
    {
        // A pass has no pacer and no error event to report on: its run returns or throws.
        var sink = new ThrowingVideoSink(_ => true);
        await using var pass = await FrameFlowPass
            .Create(Corpus(ShortClip))
            .WithVideoSink(sink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildAsync();

        var fault = await Assert.ThrowsAsync<GraphFaultException>(() =>
            pass.RunToCompletionAsync().WaitAsync(Bound)
        );

        Assert.Equal("video-sink", fault.NodeId);
        Assert.Same(sink.Thrown[0], fault.InnerException);
        Assert.Equal(1, sink.Calls);
    }

    private static Task<IMediaPlayer> BuildAsync(string path, IVideoSink sink, RepeatMode repeat = RepeatMode.Off) =>
        Builder(sink, repeat).WithMedia(path).BuildPlayerAsync();

    private static Task<IMediaPlayer> BuildAsync(MediaClip clip, IVideoSink sink) =>
        Builder(sink, RepeatMode.Off).WithMedia(clip).BuildPlayerAsync();

    private static IPlayerBuilder Builder(IVideoSink sink, RepeatMode repeat) =>
        FrameFlowPlayer
            .Create()
            .WithVideoSink(sink)
            .WithRepeatMode(repeat)
            .WithHardwareDecode(HardwareDecodeMode.Disabled);

    private static Exception? Innermost(Exception? error)
    {
        while (error?.InnerException is { } inner)
            error = inner;
        return error;
    }

    /// <summary>
    /// A sink whose presents throw as the test says, counted from 1. It owns every frame it is
    /// given and releases each one, whether the call throws or not.
    /// </summary>
    private sealed class ThrowingVideoSink(Func<int, bool> fails) : IVideoSink
    {
        private readonly object _lock = new();
        private readonly List<Exception> _thrown = [];
        private int _calls;
        private int _taken;

        public int Calls
        {
            get
            {
                lock (_lock)
                    return _calls;
            }
        }

        public int Taken
        {
            get
            {
                lock (_lock)
                    return _taken;
            }
        }

        public IReadOnlyList<Exception> Thrown
        {
            get
            {
                lock (_lock)
                    return [.. _thrown];
            }
        }

        public int? MaxHeldFrames => 0;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            lock (_lock)
            {
                _calls++;
                if (!fails(_calls))
                {
                    _taken++;
                    return ValueTask.CompletedTask;
                }
                var failure = new InvalidOperationException($"present {_calls} failed");
                _thrown.Add(failure);
                return ValueTask.FromException(failure);
            }
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
