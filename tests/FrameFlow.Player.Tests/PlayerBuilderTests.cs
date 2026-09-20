using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Argument-validation + builder-state tests that don't need FFmpeg.
/// </summary>
public sealed class PlayerBuilderTests
{
    [Fact]
    public void WithMedia_NullPath_Throws()
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace throws
        // ArgumentNullException for null and ArgumentException for empty.
        // We accept either since both are valid signals from the API
        // surface.
        Assert.ThrowsAny<ArgumentException>(() =>
            FrameFlowPlayer.Create().WithMedia((string)null!)
        );
    }

    [Fact]
    public void WithMedia_EmptyPath_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => FrameFlowPlayer.Create().WithMedia(""));
    }

    [Fact]
    public void WithMedia_NullSource_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            FrameFlowPlayer.Create().WithMedia((IMediaSource)null!)
        );
    }

    [Fact]
    public void WithTimeProvider_Null_Throws()
    {
        var builder = FrameFlowPlayer.Create().WithMedia("any.mp4");
        Assert.Throws<ArgumentNullException>(() => builder.WithTimeProvider(null!));
    }

    [Fact]
    public void WithTimeProvider_ReturnsSameBuilderForChaining()
    {
        var builder = FrameFlowPlayer.Create().WithMedia("any.mp4");

        Assert.Same(builder, builder.WithTimeProvider(TimeProvider.System));
    }

    [Fact]
    public void WithLatenessRecovery_Null_Throws()
    {
        var builder = FrameFlowPlayer.Create().WithMedia("any.mp4");
        Assert.Throws<ArgumentNullException>(() => builder.WithLatenessRecovery(null!));
    }

    [Fact]
    public void WithLatenessRecovery_InvertedHysteresis_ThrowsAtTheCallThatSetIt()
    {
        // Validated in the builder rather than at BuildPlayerAsync, so the exception names the
        // call that wrote the contradiction instead of surfacing from the build.
        var inverted = new LatenessRecoveryOptions
        {
            EscalateAbove = TimeSpan.FromMilliseconds(100),
            RelaxBelow = TimeSpan.FromMilliseconds(400),
        };

        var builder = FrameFlowPlayer.Create().WithMedia("any.mp4");
        var ex = Assert.Throws<ArgumentException>(() => builder.WithLatenessRecovery(inverted));

        Assert.Equal("RelaxBelow", ex.ParamName);
    }

    [Fact]
    public void WithLatenessRecovery_Valid_ReturnsSameBuilderForChaining()
    {
        var builder = FrameFlowPlayer.Create().WithMedia("any.mp4");

        Assert.Same(builder, builder.WithLatenessRecovery(new LatenessRecoveryOptions()));
    }

    [Fact]
    public void WithVideoSink_Null_Throws()
    {
        var builder = FrameFlowPlayer.Create().WithMedia("any.mp4");
        Assert.Throws<ArgumentNullException>(() => builder.WithVideoSink(null!));
    }

    [Fact]
    public void WithAudioSink_Null_Throws()
    {
        var builder = FrameFlowPlayer.Create().WithMedia("any.mp4");
        Assert.Throws<ArgumentNullException>(() => builder.WithAudioSink(null!));
    }

    [Fact]
    public void ConfigureVideo_Null_Throws()
    {
        var builder = FrameFlowPlayer.Create().WithMedia("any.mp4");
        Assert.Throws<ArgumentNullException>(() =>
            builder.ConfigureVideo(
                (Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>)null!
            )
        );
    }

    [Fact]
    public void ConfigureAudio_Null_Throws()
    {
        var builder = FrameFlowPlayer.Create().WithMedia("any.mp4");
        Assert.Throws<ArgumentNullException>(() =>
            builder.ConfigureAudio(
                (Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>)null!
            )
        );
    }

    [Fact]
    public void WithLogger_Null_IsNoOpAndKeepsTheChain()
    {
        // Null is deliberately not a throw: a conditional logging step
        // has to stay inside the chain rather than forcing the caller
        // out to a local. See issue #99.
        var builder = FrameFlowPlayer.Create().WithMedia("any.mp4");
        var same = builder.WithLogger(null).WithHardwareDecode(HardwareDecodeMode.Disabled);
        Assert.Same(builder, same);
    }

    [Fact]
    public void WithClock_Null_Throws()
    {
        var builder = FrameFlowPlayer.Create().WithMedia("any.mp4");
        Assert.Throws<ArgumentNullException>(() => builder.WithClock(null!));
    }

    [Fact]
    public void EveryOption_KeepsTheOneBuilder()
    {
        // The narrowing is gone. The player's builder has one terminal and every option means
        // something to it, so there is nothing to refuse. The unpaced runtime is FrameFlowPass,
        // a separate entry, so no chain can reach a terminal that would ignore what it was told.
        var builder = FrameFlowPlayer.Create();
        var sink = new NullVideoSink();

        IPlayerBuilder same = builder
            .WithMedia("any.mp4")
            .WithRepeatMode(RepeatMode.All)
            .WithHardwareFrames()
            .WithAudioActivation(false)
            .WithClock(new PlaybackClock())
            .WithVideoSink(sink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .WithLogger(null);

        Assert.Same(builder, same);
    }

    [Fact]
    public void Builder_FluentChain_ReturnsSelf()
    {
        var builder = FrameFlowPlayer.Create().WithMedia("any.mp4");
        var sink = new NullVideoSink();
        var same = builder.WithVideoSink(sink).WithHardwareDecode(HardwareDecodeMode.Disabled);
        Assert.Same(builder, same);
    }

    internal sealed class NullVideoSink : IVideoSink
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
