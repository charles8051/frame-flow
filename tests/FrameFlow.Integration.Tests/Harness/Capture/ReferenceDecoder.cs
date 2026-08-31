using FrameFlow.Decoding;
using FrameFlow.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Integration.Tests.Harness.Capture;

/// <summary>
/// Drives the FrameFlow demuxer + audio/video decoders directly — no
/// playback runtime, no clock, no AV sync, no sink-owned pool — to
/// produce the deterministic ground-truth decode of a corpus file.
/// </summary>
/// <remarks>
/// <para>
/// The output shape matches <see cref="CapturingAudioSink.Captures"/>
/// and <see cref="CapturingVideoSink.Captures"/> so a content
/// assertion can compare reference against capture 1:1.
/// </para>
/// <para>
/// Using FrameFlow's own decoders (rather than a separate FFmpeg
/// subprocess) is intentional: same codec configuration, same
/// resampler settings, same pixel-format conversions. The reference
/// is therefore "what the pipeline would produce if it ran without
/// the playback-runtime concurrency layer." Differences between
/// reference and capture are attributable to the runtime, not to
/// codec-version drift between two FFmpeg builds.
/// </para>
/// </remarks>
internal static class ReferenceDecoder
{
    /// <summary>
    /// Decodes <paramref name="corpusFilename"/> end-to-end and returns
    /// the reference audio + video captures.
    /// </summary>
    public static async Task<ReferenceCapture> DecodeAsync(
        string corpusFilename,
        CancellationToken ct = default
    )
    {
        var path = PlaybackHarness.ResolveCorpusPath(corpusFilename);

        var demuxFactory = new DemuxSessionFactory(NullLogger<DemuxSessionFactory>.Instance);
        var demux = await demuxFactory
            .OpenAsync(MediaSource.FromFile(path), ct)
            .ConfigureAwait(false);

        IVideoDecoder? videoDecoder = null;
        IAudioDecoder? audioDecoder = null;
        DecodingPipeline? pipeline = null;

        try
        {
            videoDecoder = DecoderFactories.Video(demux);
            audioDecoder = DecoderFactories.Audio(demux);

            // The DecodingPipeline only supports concrete VideoDecoder /
            // AudioDecoder (not arbitrary IVideoDecoder / IAudioDecoder)
            // because it needs native AVPacket* routing. Cast is safe for
            // the default factory, which always returns the concrete types.
            if (demux is not DemuxSession concreteDemux)
                throw new InvalidOperationException(
                    "ReferenceDecoder requires the concrete DemuxSession; the demux session "
                        + $"factory returned {demux.GetType().FullName}."
                );

            pipeline = new DecodingPipeline(
                concreteDemux,
                videoDecoder as VideoDecoder,
                audioDecoder as AudioDecoder,
                NullLogger.Instance
            );

            var audioCaptures = new List<AudioCapture>();
            var videoCaptures = new List<VideoCapture>();

            // The decoder enumerations run concurrently with the demux pump:
            // pump produces packets, enumerations consume frames. Pattern
            // mirrors PipelineController.RunAudioDecodeWriteWorkerAsync /
            // RunVideoSinkWorkerAsync but without the sync delay / sink /
            // gate plumbing, because reference decode has no real-time
            // obligation.
            var audioTask = audioDecoder is not null
                ? Task.Run(() => CollectAudioAsync(audioDecoder, audioCaptures, ct), ct)
                : Task.CompletedTask;

            var videoTask = videoDecoder is not null
                ? Task.Run(() => CollectVideoAsync(videoDecoder, videoCaptures, ct), ct)
                : Task.CompletedTask;

            await pipeline.RunDemuxPumpAsync(ct).ConfigureAwait(false);
            await pipeline.FinalizeDecodersAsync(ct).ConfigureAwait(false);

            await Task.WhenAll(audioTask, videoTask).ConfigureAwait(false);

            return new ReferenceCapture(audioCaptures, videoCaptures);
        }
        finally
        {
            if (pipeline is not null)
                await pipeline.DisposeAsync().ConfigureAwait(false);
            if (videoDecoder is not null)
                await videoDecoder.DisposeAsync().ConfigureAwait(false);
            if (audioDecoder is not null)
                await audioDecoder.DisposeAsync().ConfigureAwait(false);
            await demux.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task CollectAudioAsync(
        IAudioDecoder decoder,
        List<AudioCapture> sink,
        CancellationToken ct
    )
    {
        await foreach (var block in decoder.DecodeAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var samples = block.Samples.Span;
                var copy = new short[samples.Length];
                samples.CopyTo(copy);
                sink.Add(
                    new AudioCapture(
                        Pts: block.PresentationTime,
                        InterleavedSamples: copy,
                        SampleRate: block.SampleRate,
                        Channels: block.Channels
                    )
                );
            }
            finally
            {
                block.Dispose();
            }
        }
    }

    private static async Task CollectVideoAsync(
        IVideoDecoder decoder,
        List<VideoCapture> sink,
        CancellationToken ct
    )
    {
        await foreach (var frame in decoder.DecodeAsync(ct).ConfigureAwait(false))
        {
            try
            {
                sink.Add(FramePacker.Pack(frame));
            }
            finally
            {
                frame.Dispose();
            }
        }
    }
}

/// <summary>
/// The reference decode output. Same shape as
/// <see cref="PlaybackCaptureResult"/> but without playback-runtime
/// metadata (no final state, no controller handle).
/// </summary>
internal sealed record ReferenceCapture(
    IReadOnlyList<AudioCapture> Audio,
    IReadOnlyList<VideoCapture> Video
);
