namespace FrameFlow.Integration.Tests.Harness.Capture;

/// <summary>
/// One <c>PcmAudioBuffer</c>'s worth of audio retained by
/// <see cref="CapturingAudioSink"/>. The sample data is a heap copy
/// owned by the capture record, so it survives the source block's
/// disposal back to the memory pool.
/// </summary>
/// <param name="Pts">Block presentation timestamp.</param>
/// <param name="InterleavedSamples">
/// Interleaved S16 samples; length is <c>SampleCount</c> shorts
/// (i.e. <c>samples-per-channel × Channels</c>).
/// </param>
/// <param name="SampleRate">Sample rate at write time (Hz).</param>
/// <param name="Channels">Channel count at write time.</param>
internal readonly record struct AudioCapture(
    TimeSpan Pts,
    short[] InterleavedSamples,
    int SampleRate,
    int Channels
)
{
    /// <summary>
    /// Number of samples per channel in this block. Convenience accessor;
    /// equivalent to <c>InterleavedSamples.Length / Channels</c>.
    /// </summary>
    public int SamplesPerChannel => Channels > 0 ? InterleavedSamples.Length / Channels : 0;

    /// <summary>Block duration computed from sample count and rate.</summary>
    public TimeSpan Duration =>
        SampleRate > 0
            ? TimeSpan.FromSeconds((double)SamplesPerChannel / SampleRate)
            : TimeSpan.Zero;
}
