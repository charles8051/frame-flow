using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Argument validation and builder state on the pass, without FFmpeg. The runtime behaviour —
/// that a pass never reads a clock — is in <c>MediaPassIntegrationTests</c>, because it needs a
/// real file to run through.
/// </summary>
public sealed class PassBuilderTests
{
    [Fact]
    public void Create_NullPath_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => FrameFlowPass.Create((string)null!));
    }

    [Fact]
    public void Create_EmptyPath_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => FrameFlowPass.Create(""));
    }

    [Fact]
    public void Create_NullSource_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FrameFlowPass.Create((IMediaSource)null!));
    }

    [Fact]
    public void WithVideoSink_Null_Throws()
    {
        var builder = FrameFlowPass.Create("any.mp4");
        Assert.Throws<ArgumentNullException>(() => builder.WithVideoSink(null!));
    }

    [Fact]
    public void WithAudioSink_Null_Throws()
    {
        var builder = FrameFlowPass.Create("any.mp4");
        Assert.Throws<ArgumentNullException>(() => builder.WithAudioSink(null!));
    }

    [Fact]
    public void ConfigureVideo_Null_Throws()
    {
        var builder = FrameFlowPass.Create("any.mp4");
        Assert.Throws<ArgumentNullException>(() =>
            builder.ConfigureVideo(
                (Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>)null!
            )
        );
    }

    [Fact]
    public void ConfigureAudio_Null_Throws()
    {
        var builder = FrameFlowPass.Create("any.mp4");
        Assert.Throws<ArgumentNullException>(() =>
            builder.ConfigureAudio(
                (Func<GraphChain<PcmAudioBuffer>, GraphChain<PcmAudioBuffer>>)null!
            )
        );
    }

    [Fact]
    public void Builder_FluentChain_ReturnsSelf()
    {
        var builder = FrameFlowPass.Create("any.mp4");
        var same = builder
            .WithVideoSink(new PlayerBuilderTests.NullVideoSink())
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .WithLogger(null);

        Assert.Same(builder, same);
    }

    [Fact]
    public async Task BuildAsync_WithNoSink_IsRefusedBeforeTheFileIsOpened()
    {
        // A run with no sink decodes the whole source and drops every frame, which is never what
        // was meant. The path does not exist, so a check that opened the file first would report
        // the wrong problem.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => FrameFlowPass.Create("does-not-exist.mp4").BuildAsync()
        );

        Assert.Contains("No sinks attached", error.Message);
        Assert.Contains("HeadlessVideoSink", error.Message);
    }

    [Fact]
    public void ThePassBuilderHasNoTransportOptions()
    {
        // Repeat, a clock, hardware frames and audio activation belong to the player. A pass runs
        // its source through once and waits on no presentation time, so none of them mean
        // anything here — and the type says so rather than dropping them silently.
        var members = typeof(IPassBuilder).GetMethods().Select(m => m.Name).ToHashSet();

        Assert.DoesNotContain("WithRepeatMode", members);
        Assert.DoesNotContain("WithClock", members);
        Assert.DoesNotContain("WithHardwareFrames", members);
        Assert.DoesNotContain("WithAudioActivation", members);
        Assert.DoesNotContain("WithMedia", members);
        Assert.DoesNotContain("BuildPlayerAsync", members);
    }
}
