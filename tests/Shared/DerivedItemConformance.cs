using FrameFlow.Graph;
using FrameFlow.Media;
using Xunit;

namespace FrameFlow.Tests.Shared;

/// <summary>
/// Checks that a node building a new item from an input carries the input's lineage over
/// (ADR-0084, decision 3): its timeline, and for video its pixel shape and rotation. Linked into
/// every test project that owns such a producer; each runs it over its own.
/// </summary>
/// <remarks>
/// <para>
/// A copy that is missed turns the timeline into <see cref="TimelineId.None"/> downstream, which a
/// consumer reads as continuous and nothing warns about. These checks are the guard, so a new
/// producer adds a case to its project's conformance test, and a line to the list below.
/// </para>
/// <para>
/// The producers that build a new item from an input, and where each is checked:
/// </para>
/// <list type="bullet">
/// <item><c>VideoFrameExtensions.CloneCpu</c>: Media.Tests, <c>TimelineCarryTests</c>.</item>
/// <item><c>SwScaleVideoConverter</c>, the convert node and resize: Video.Tests, <c>DerivedItemConformanceTests</c>.</item>
/// <item><c>FfmpegAudioResampler</c> (resampled, empty and flushed buffers) and the resample node: Audio.Tests, <c>DerivedItemConformanceTests</c>.</item>
/// <item><c>PcmAudioTrim.Cut</c>: Playback.Tests, <c>PcmAudioTrimTests</c>.</item>
/// <item><c>GpuFrameUpload</c> and <c>GpuFrameReadback</c>: Decoding.Tests, <c>GpuDerivedItemConformanceTests</c>.</item>
/// <item><c>TensorToImage.ToFrame</c>: Inference.Abstractions.Tests, <c>TensorToImageTests</c>.</item>
/// <item><c>InferenceResult&lt;T&gt;</c> from the inference node: Inference.Abstractions.Tests, <c>InferenceOperatorTests</c>.</item>
/// <item><c>DetectedVideoFrameRef</c>: Yolo.Tests, <c>DetectedVideoFrameRefTimelineTests</c>.</item>
/// <item><c>DetectedFaceFrameRef</c>: Face.Tests, <c>DetectedFaceFrameRefTimelineTests</c>.</item>
/// <item><c>CaptionRef</c> from the transcription window, <c>SplitOnPunctuation</c> and <c>AnimatedReveal</c>: Whisper.Tests, <c>WindowTimelineTests</c> and <c>CaptionOperatorsTests</c>.</item>
/// <item>The Multicast example's <c>ColorFilters.Apply</c>: no test project covers the examples.</item>
/// </list>
/// <para>
/// Sources stay on <see cref="TimelineId.None"/> by design: <c>SyntheticSceneSource</c> and the
/// camera frames (ADR-0084, open question 1). The decoders stamp rather than copy, and are checked
/// in Decoding.Tests, <c>DecoderTimelineStampTests</c>.
/// </para>
/// </remarks>
internal static class DerivedItemConformance
{
    /// <summary>A pixel shape no producer gives a frame by default.</summary>
    public static readonly SampleAspectRatio Shape = new(4, 3);

    /// <summary>A rotation no producer gives a frame by default.</summary>
    public const VideoRotation Turn = VideoRotation.Clockwise90;

    /// <summary>
    /// A CPU frame on a timeline of its own, with <see cref="Shape"/> and <see cref="Turn"/>, so a
    /// derived frame that drops any of them shows it.
    /// </summary>
    public static CpuVideoFrame VideoInput(PixelFormat format = PixelFormat.Bgra32, int width = 16, int height = 8) =>
        CpuVideoFrame.Create(
            format,
            width,
            height,
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(40),
            TimelineId.Next(),
            0,
            static (planes, _) =>
            {
                planes.Y.Fill(0x80);
                planes.U.Fill(0x80);
                planes.V.Fill(0x80);
            },
            sampleAspectRatio: Shape,
            rotation: Turn
        );

    /// <summary>A buffer of <paramref name="frames"/> frames of a tone, on a timeline of its own.</summary>
    public static PcmAudioBuffer AudioInput(int sampleRate = 48_000, int channels = 2, int frames = 960) =>
        PcmAudioBuffer.Create(
            frames * channels,
            sampleRate,
            channels,
            TimeSpan.FromMilliseconds(20),
            TimelineId.Next(),
            (Frames: frames, Channels: channels),
            static (samples, s) =>
            {
                for (int i = 0; i < s.Frames * s.Channels; i++)
                    samples[i] = (short)(1000 * Math.Sin(i / (double)s.Channels * 0.1));
                return s.Frames * s.Channels;
            }
        );

    /// <summary>
    /// Asserts <paramref name="derived"/>, made from <paramref name="input"/>, has its timeline,
    /// pixel shape and rotation.
    /// </summary>
    public static void AssertCarriesLineage(IVideoFrame input, IVideoFrame derived)
    {
        // The input names a timeline, so a copy that fell back to None cannot pass by accident.
        Assert.NotEqual(TimelineId.None, input.Timeline);
        Assert.Equal(input.Timeline, derived.Timeline);
        Assert.Equal(input.SampleAspectRatio, derived.SampleAspectRatio);
        Assert.Equal(input.Rotation, derived.Rotation);
    }

    /// <summary>Asserts <paramref name="derived"/>, made from <paramref name="input"/>, is on its timeline.</summary>
    public static void AssertCarriesTimeline(IAudioBuffer input, IAudioBuffer derived)
    {
        Assert.NotEqual(TimelineId.None, input.Timeline);
        Assert.Equal(input.Timeline, derived.Timeline);
    }
}
