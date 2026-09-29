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
    /// <see cref="PcmAudioBuffer"/> to <paramref name="targetSampleRate"/>
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
    /// <b>Resampler lifetime.</b> One resampler per run: made on the run's first
    /// buffer and disposed by the node's cleanup when its pump exits (#47). A
    /// re-run of the graph, such as a loop, starts from an empty resampler, so no
    /// samples buffered before the rewind come out after it.
    /// </para>
    /// </remarks>
    public static OperatorNode<PcmAudioBuffer, PcmAudioBuffer> Resample(
        string id,
        int targetSampleRate,
        int targetChannels
    )
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetChannels);

        // Touched only by this node's pump: the body and then, after its last call, the cleanup.
        IAudioResampler? resampler = null;

        return new OperatorNode<PcmAudioBuffer, PcmAudioBuffer>(
            id,
            (input, ct) =>
            {
                resampler ??= AudioResampler.Create(targetSampleRate, targetChannels);
                return ValueTask.FromResult<PcmAudioBuffer?>(resampler.Process(input));
            },
            cleanup: () =>
            {
                resampler?.Dispose();
                resampler = null;
                return ValueTask.CompletedTask;
            }
        );
    }
}
