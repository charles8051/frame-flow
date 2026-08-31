// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Audio;

/// <summary>
/// Port of <c>FrameFlow.Audio.AudioPipelineExtensions</c> to the new
/// primitive-set substrate. Mirrors <c>FrameFlow.Video.VideoOperators</c>
/// shape: each operator is a factory that builds an
/// <see cref="OperatorNode{TIn, TOut}"/> wrapping the underlying
/// <see cref="IAudioResampler"/> primitive.
/// </summary>
public static class AudioOperators
{
    /// <summary>
    /// Builds a 1→1 operator node that resamples each upstream
    /// <see cref="PcmAudioBufferRef"/> to <paramref name="targetSampleRate"/>
    /// Hz / <paramref name="targetChannels"/> channels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>End-of-stream flush.</b> The 1→1 shape doesn't flush the
    /// resampler's trailing buffered samples on EOS — same caveat as
    /// the original. Consumers needing lossless conversion should
    /// call <see cref="IAudioResampler"/> directly and invoke
    /// <see cref="IAudioResampler.Flush"/> on EOS. For real-time /
    /// streaming use (ASR, monitoring) the trailing samples are
    /// inconsequential.
    /// </para>
    /// <para>
    /// <b>Resampler lifetime.</b> Captured by the operator closure,
    /// outlives the graph run until GC reclaims it. The
    /// <c>SwrContextHandle</c> is a <c>SafeHandle</c>, so native
    /// cleanup is guaranteed by the finalizer.
    /// </para>
    /// </remarks>
    public static OperatorNode<PcmAudioBufferRef, PcmAudioBufferRef> Resample(
        string id,
        int targetSampleRate,
        int targetChannels
    )
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetChannels);

#pragma warning disable CA2000
        var resampler = AudioResampler.Create(targetSampleRate, targetChannels);
#pragma warning restore CA2000

        return new OperatorNode<PcmAudioBufferRef, PcmAudioBufferRef>(
            id,
            (input, ct) =>
            {
                var output = resampler.Process(input.Buffer);
                return ValueTask.FromResult<PcmAudioBufferRef?>(new PcmAudioBufferRef(output));
            }
        );
    }
}
