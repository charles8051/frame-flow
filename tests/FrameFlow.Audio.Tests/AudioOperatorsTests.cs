using FrameFlow.Audio;
using FrameFlow.Graph;
using FrameFlow.Media;

using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// <see cref="AudioOperators.Resample"/> against real swresample. Needs FFmpeg loaded.
/// </summary>
public sealed class AudioOperatorsTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public AudioOperatorsTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegFact]
    public async Task Resample_StartsEachRunFromAnEmptyResampler()
    {
        // A loop re-runs the same graph. swresample holds back samples from each call, so a
        // resampler kept across runs would hand the first run's tail to the second: the second
        // run's first output would be longer than the first run's. One made per run is not.
        var counts = new List<int>();
        var graph = new GraphRunner();
        graph
            .Pipeline(OneSecondOfSine())
            .Then(AudioOperators.Resample("resample", targetSampleRate: 16_000, targetChannels: 1))
            .To(
                new SinkNode<PcmAudioBuffer>(
                    "sink",
                    (buffer, _) =>
                    {
                        Assert.Equal(16_000, buffer.SampleRate);
                        Assert.Equal(1, buffer.Channels);
                        counts.Add(buffer.SampleCount);
                        return ValueTask.CompletedTask;
                    }
                )
            );

        await graph.RunAsync();
        await graph.RunAsync();

        Assert.Equal(2, counts.Count);
        Assert.InRange(counts[0], 1, 16_000);
        Assert.Equal(counts[0], counts[1]);
    }

    // One buffer of 48 kHz stereo per run: the source's cleanup rewinds it.
    private static SourceNode<PcmAudioBuffer> OneSecondOfSine()
    {
        bool sent = false;
        return new SourceNode<PcmAudioBuffer>(
            "source",
            _ =>
            {
                if (sent)
                    return ValueTask.FromResult<PcmAudioBuffer?>(null);
                sent = true;
                return ValueTask.FromResult<PcmAudioBuffer?>(Sine(48_000, 2, 440.0));
            },
            cleanup: () =>
            {
                sent = false;
                return ValueTask.CompletedTask;
            }
        );
    }

    private static PcmAudioBuffer Sine(int sampleRate, int channels, double frequencyHz) =>
        PcmAudioBuffer.Create(
            sampleRate * channels,
            sampleRate,
            channels,
            TimeSpan.Zero,
            (Rate: sampleRate, Channels: channels, Frequency: frequencyHz),
            static (span, p) =>
            {
                int frames = span.Length / p.Channels;
                for (int frame = 0; frame < frames; frame++)
                {
                    short s = (short)(Math.Sin(2 * Math.PI * p.Frequency * frame / p.Rate) * 16_000);
                    for (int c = 0; c < p.Channels; c++)
                        span[frame * p.Channels + c] = s;
                }
                return span.Length;
            }
        );
}
