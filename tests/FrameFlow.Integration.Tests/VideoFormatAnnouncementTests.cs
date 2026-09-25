using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Player;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// A queue whose items are not the same size has to tell the sink so (#287).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IVideoSink.OnFormatChangedAsync"/> is documented as the call the playback
/// pipeline makes when the stream format changes, and nothing in the tree made it. A sink that
/// sizes its surface from the announcement therefore kept the geometry it had, and an item of
/// a different size showed the previous item's picture: for a clip until its next frame
/// arrived, and for a still paced by the image demuxer for as long as the still lasted.
/// </para>
/// <para>
/// A still is what exposed it rather than what caused it, so the fixtures here are a 1920x1080
/// clip and a 320x240 one as well as the still.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class VideoFormatAnnouncementTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Hd = "test-1080p-h264-aac.mp4";
    private const string Small = "test-subsecond.mp4";
    private const string Still = "test-still.png";

    private static MediaSource File(string name)
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(name);
        Assert.True(path is not null, $"Corpus file {name} not found.");
        return MediaSource.FromFile(path!);
    }

    private static IMediaSource PacedStill(int seconds)
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(Still);
        Assert.True(path is not null, $"Corpus file {Still} not found.");
        return MediaSource.FromStill(path!, TimeSpan.FromSeconds(seconds));
    }

    private static async Task<FormatRecordingSink> PlayAsync(params IMediaSource[] queue)
    {
        var sink = new FormatRecordingSink();

        await using var player = await FrameFlowPlayer
            .Create()
            .WithMedia(queue)
            .WithVideoSink(sink)
            .WithAudioSink(new HarnessAudioSink())
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildPlayerAsync();

        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = player.StateChanged.Subscribe(
            new ActionObserver<PlaybackState>(s =>
            {
                if (s is PlaybackState.Ended or PlaybackState.Error)
                    ended.TrySetResult();
            })
        );

        Assert.True((await player.PlayAsync()).IsSuccess);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await ended.Task.WaitAsync(cts.Token);

        Assert.Equal(PlaybackState.Ended, player.State);
        return sink;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task EachItemsSizeIsAnnouncedBeforeItsFirstFrame()
    {
        var sink = await PlayAsync(File(Hd), PacedStill(2));

        // The announcement has to precede the frame it describes, or a sink has already been
        // asked to draw at the wrong geometry by the time it is told.
        Assert.Equal(
            ["format 1920x1080", "frame 1920x1080", "format 320x240", "frame 320x240"],
            sink.FirstOfEachKind()
        );
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task TheStillsOwnFrameIsDelivered()
    {
        // The symptom that started #287, asserted on the sink rather than on the screen: the
        // still's one frame reaches the sink, and it is not the previous item's size.
        var sink = await PlayAsync(File(Hd), PacedStill(2));

        Assert.Equal(1, sink.FramesOf(320, 240));
        Assert.True(sink.FramesOf(1920, 1080) > 1, "the clip delivered no frames");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ASameSizeBoundaryIsNotAnnouncedTwice()
    {
        // The sink is warm across the queue, so a sink that rebuilds a surface on
        // announcement must not rebuild it per item for a queue that never changes shape.
        var sink = await PlayAsync(File(Small), File(Small));

        Assert.Equal([new VideoFormatInfo(320, 240, PixelFormat.Bgra32)], sink.Announcements);
    }

    /// <summary>
    /// Records announcements and frames in one ordered log, so a test can assert that the
    /// announcement came first rather than only that both happened.
    /// </summary>
    private sealed class FormatRecordingSink : IVideoSink
    {
        private readonly Lock _gate = new();
        private readonly List<string> _log = [];
        private readonly List<VideoFormatInfo> _announcements = [];

        public IReadOnlyList<VideoFormatInfo> Announcements
        {
            get
            {
                lock (_gate)
                    return _announcements.ToArray();
            }
        }

        public int FramesOf(int width, int height)
        {
            lock (_gate)
                return _log.Count(e => e == $"frame {width}x{height}");
        }

        /// <summary>The log with consecutive duplicates collapsed.</summary>
        public string[] FirstOfEachKind()
        {
            lock (_gate)
            {
                var distinct = new List<string>();
                foreach (var entry in _log)
                {
                    if (distinct.Count == 0 || distinct[^1] != entry)
                        distinct.Add(entry);
                }

                return [.. distinct];
            }
        }

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            lock (_gate)
                _log.Add($"frame {frame.Width}x{frame.Height}");

            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct)
        {
            lock (_gate)
            {
                _log.Add($"format {format.Width}x{format.Height}");
                _announcements.Add(format);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
