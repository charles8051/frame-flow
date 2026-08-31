using FrameFlow.Audio;
using FrameFlow.Media;
using FrameFlow.Video;
using FrameFlow.Graph;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Argument-validation + builder-state tests that don't need FFmpeg.
/// </summary>
public sealed class PlayerBuilderTests
{
    [Fact]
    public void Open_NullPath_Throws()
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace throws
        // ArgumentNullException for null and ArgumentException for empty.
        // We accept either since both are valid signals from the API
        // surface.
        Assert.ThrowsAny<ArgumentException>(() => FrameFlowPlayer.Open((string)null!));
    }

    [Fact]
    public void Open_EmptyPath_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => FrameFlowPlayer.Open(""));
    }

    [Fact]
    public void Open_NullSource_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            FrameFlowPlayer.Open((IMediaSource)null!)
        );
    }

    [Fact]
    public void WithVideoSink_Null_Throws()
    {
        var builder = FrameFlowPlayer.Open("any.mp4");
        Assert.Throws<ArgumentNullException>(() => builder.WithVideoSink(null!));
    }

    [Fact]
    public void WithAudioSink_Null_Throws()
    {
        var builder = FrameFlowPlayer.Open("any.mp4");
        Assert.Throws<ArgumentNullException>(() => builder.WithAudioSink(null!));
    }

    [Fact]
    public void ConfigureVideo_Null_Throws()
    {
        var builder = FrameFlowPlayer.Open("any.mp4");
        Assert.Throws<ArgumentNullException>(() =>
            builder.ConfigureVideo(
                (Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>)null!
            )
        );
    }

    [Fact]
    public void ConfigureAudio_Null_Throws()
    {
        var builder = FrameFlowPlayer.Open("any.mp4");
        Assert.Throws<ArgumentNullException>(() =>
            builder.ConfigureAudio(
                (Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>)null!
            )
        );
    }

    [Fact]
    public void WithLogger_Null_Throws()
    {
        var builder = FrameFlowPlayer.Open("any.mp4");
        Assert.Throws<ArgumentNullException>(() => builder.WithLogger(null!));
    }

    [Fact]
    public void Builder_FluentChain_ReturnsSelf()
    {
        var builder = FrameFlowPlayer.Open("any.mp4");
        var sink = new NullVideoSink();
        var same = builder.WithVideoSink(sink).WithHardwareDecode(HardwareDecodeMode.Disabled);
        Assert.Same(builder, same);
    }

    private sealed class NullVideoSink : IVideoSink
    {
        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public IFramePool FramePool => null!;

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
