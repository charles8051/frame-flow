using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Covers the audio-sink activation contract on the <see cref="MediaPass"/>
/// path (issue #60).
/// </summary>
/// <remarks>
/// An <see cref="IAudioSink"/> is inert until activated — buffers presented to a
/// dormant sink are accepted and dropped. <c>SubstrateSession</c> and
/// <c>FrameFlowPlayer.Create()</c> both activate the sink they are given;
/// <see cref="MediaPass"/> did not, which left the sink silent unless the
/// caller activated it out of band (issue #60).
/// </remarks>
public sealed class MediaPassAudioActivationTests
{
    [RequiresFfmpegAndCorpusFact]
    public async Task RunToCompletionAsync_ActivatesTheAudioSink()
    {
        var path = TestEnvironment.GetCorpusFile("test-audio-aac.m4a");
        Assert.NotNull(path);

        var sink = new RecordingAudioSink();

        await using var session = await FrameFlowPass
            .Create(path!)
            .WithAudioSink(sink)
            .BuildAsync();

        Assert.Equal(0, sink.ActivateCount); // not activated by BuildAsync alone

        await session.RunToCompletionAsync();

        Assert.True(
            sink.ActivateCount >= 1,
            "MediaPass must activate the audio sink before pumping PCM into it."
        );
        Assert.True(sink.PresentCount > 0, "Expected decoded audio to reach the sink.");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task RunToCompletionAsync_ActivatesExactlyOnce()
    {
        var path = TestEnvironment.GetCorpusFile("test-audio-aac.m4a");
        Assert.NotNull(path);

        var sink = new RecordingAudioSink();

        await using var session = await FrameFlowPass
            .Create(path!)
            .WithAudioSink(sink)
            .BuildAsync();

        await session.RunToCompletionAsync();

        // Exactly once, not "at least once". Re-activation is a reset — it
        // rebases the sample counter — so on a sink that also implements
        // IClockSource a redundant activate would rewind the master clock.
        // That is why the contract on IAudioSink.ActivateAsync puts
        // activation solely in the session's hands.
        Assert.Equal(1, sink.ActivateCount);
        Assert.True(sink.PresentCount > 0);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task RunToCompletionAsync_SinkReusedAcrossSessions_DoesNotFault()
    {
        var path = TestEnvironment.GetCorpusFile("test-audio-aac.m4a");
        Assert.NotNull(path);

        var sink = new RecordingAudioSink();

        for (var i = 0; i < 2; i++)
        {
            await using var session = await FrameFlowPass
                .Create(path!)
                .WithAudioSink(sink)
                .BuildAsync();

            await session.RunToCompletionAsync();
        }

        // A sink outliving one session (the HostedServicePlayer shape, where
        // the container owns an IAudioSink singleton) gets activated once per
        // session. The second activation must reset rather than throw.
        Assert.Equal(2, sink.ActivateCount);
    }

    private sealed class RecordingAudioSink : IAudioSink
    {
        private int _activateCount;
        private int _presentCount;

        public int ActivateCount => Volatile.Read(ref _activateCount);
        public int PresentCount => Volatile.Read(ref _presentCount);

        public bool Muted { get; set; }

        public ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct)
        {
            Interlocked.Increment(ref _presentCount);
            buffer?.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _activateCount);
            return ValueTask.CompletedTask;
        }

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
