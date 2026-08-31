// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using FrameFlow.Graph;
using FrameFlow.Media;
using Whisper.net;

namespace FrameFlow.Whisper;

/// <summary>
/// Substrate operator form of <see cref="FrameFlow.Whisper.WhisperPipelineExtensions.TranscribeWithWhisper"/>.
/// Wraps Whisper.net inference as a 1→N <see cref="MultiOperatorNode{TIn, TOut}"/>:
/// each upstream <see cref="PcmAudioBufferRef"/> contributes samples to a
/// rolling window; each completed window runs one Whisper inference call
/// and yields a <see cref="CaptionRef"/> per non-empty segment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Diff from old.</b> The original
/// <see cref="FrameFlow.Whisper.WhisperPipelineExtensions.TranscribeWithWhisper"/>
/// wraps the many-to-many shape in a private bridge: an
/// <c>Observe</c>-driven background task accumulates samples and writes
/// captions to an unbounded <see cref="System.Threading.Channels.Channel{T}"/>;
/// the outer <c>IAsyncEnumerable</c> drains the channel. ~80 lines of
/// plumbing wrap the same windowing + inference loop.
/// </para>
/// <para>
/// The new shape skips the bridge entirely. <see cref="MultiOperatorNode{TIn, TOut}"/>'s
/// body is an <c>IAsyncEnumerable</c>-shaped delegate: window state lives
/// in the closure across calls, each call yields the captions emitted for
/// any windows that the new input closed. The substrate's pump handles
/// cancellation, exception propagation, and ref ownership. ~40 lines of
/// substrate-side glue, same domain logic.
/// </para>
/// <para>
/// <b>Trailing samples.</b> Whisper's old version drained the trailing
/// sub-window slice at EOS — the audio after the last full window
/// boundary still got a caption. The substrate's
/// <see cref="MultiOperatorNode{TIn, TOut}"/> has no EOS-cleanup hook
/// today, so the trailing samples are dropped. Same caveat applies to
/// <see cref="AudioOperators.Resample"/>'s post-EOS resampler buffer.
/// For streaming captioning (the LiveCaptioning use case) the trailing
/// loss is inconsequential; for offline transcription of finite files
/// the last &lt; <see cref="WhisperOptions.EffectiveWindowSize"/> of
/// audio gets silently dropped. Tracked in docs/DEFERRED_WORK.md as "MultiOperatorNode
/// needs Cleanup hook" — fix lives in Crossbar, not here.
/// </para>
/// <para>
/// <b>Native resource lifetime.</b> <see cref="WhisperFactory"/> and
/// <see cref="WhisperProcessor"/> are allocated lazily on the first
/// body call and captured in the operator's closure. They live until
/// the closure is GC'd — typically when the graph itself becomes
/// unreachable. Whisper.net's types implement <see cref="IDisposable"/>;
/// the underlying native handles are released via finalizers. Same
/// "outlives graph run, lean on GC" pattern as
/// <see cref="AudioOperators.Resample"/>. Consumers with tight cycle
/// requirements should hold the operator separately and dispose
/// explicitly via a follow-on API (not yet exposed).
/// </para>
/// </remarks>
public static class WhisperOperators
{
    /// <summary>
    /// Builds a 1→N operator node that runs Whisper inference on a
    /// rolling window of upstream PCM audio.
    /// </summary>
    /// <param name="id">Node id for graph diagnostics.</param>
    /// <param name="modelPath">
    /// Path to a Whisper ggml model file (e.g. <c>ggml-base.en.bin</c>).
    /// Loaded on first body invocation and held for the operator's
    /// closure lifetime.
    /// </param>
    /// <param name="options">
    /// Optional configuration. Defaults: 16 kHz mono input,
    /// English language, 5-second windows.
    /// </param>
    /// <exception cref="ArgumentNullException">Required arg is null.</exception>
    /// <exception cref="ArgumentException">Model path is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Sample rate / channel count is non-positive.</exception>
    public static MultiOperatorNode<PcmAudioBufferRef, CaptionRef> TranscribeWithWhisper(
        string id,
        string modelPath,
        WhisperOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        var opts = options ?? new WhisperOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(opts.InputSampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(opts.InputChannels);

        // State captured by the operator closure: persists across body
        // calls for the lifetime of the graph. The Whisper resources are
        // lazy-allocated on first input so building the operator (during
        // graph construction) doesn't touch the model file.
        WhisperFactory? factory = null;
        WhisperProcessor? processor = null;
        var windowSamples = (int)(opts.InputSampleRate * opts.EffectiveWindowSize.TotalSeconds);
        var windowBuffer = new List<float>(windowSamples + 4096);
        TimeSpan? windowStartPts = null;

        return new MultiOperatorNode<PcmAudioBufferRef, CaptionRef>(id, TranscribeImpl);

        async IAsyncEnumerable<CaptionRef> TranscribeImpl(
            PcmAudioBufferRef input,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            // Lazy init on first call — matches the old version's behaviour
            // of "construction is cheap; the model file is only touched
            // when the first packet arrives".
            if (processor is null)
            {
#pragma warning disable CA2000 // Factory + processor held by closure for operator lifetime; finalized by GC.
                factory = WhisperFactory.FromPath(modelPath);
                processor = factory.CreateBuilder().WithLanguage(opts.Language).Build();
#pragma warning restore CA2000
            }

            var buffer = input.Buffer;
            if (buffer.SampleCount == 0)
                yield break;

            if (
                buffer.SampleRate != opts.InputSampleRate
                || buffer.Channels != opts.InputChannels
            )
            {
                throw new InvalidOperationException(
                    $"TranscribeWithWhisper expected {opts.InputChannels}ch/{opts.InputSampleRate}Hz "
                        + $"but got {buffer.Channels}ch/{buffer.SampleRate}Hz. Compose a "
                        + $".Resample({opts.InputSampleRate}, {opts.InputChannels}) operator upstream."
                );
            }

            windowStartPts ??= buffer.PresentationTime;

            AppendSamplesAsFloat(windowBuffer, buffer);

            var localStart = windowStartPts.Value;
            var windowSize = opts.EffectiveWindowSize;

            while (windowBuffer.Count >= windowSamples)
            {
                var slice = new float[windowSamples];
                windowBuffer.CopyTo(0, slice, 0, windowSamples);
                windowBuffer.RemoveRange(0, windowSamples);

                await foreach (
                    var caption in InferCaptionsAsync(processor!, slice, localStart, ct)
                        .ConfigureAwait(false)
                )
                {
                    yield return caption;
                }

                localStart += windowSize;
            }

            // Thread the advanced start PTS back into the closure for
            // the next call's windowing math.
            windowStartPts = localStart;
        }
    }

    /// <summary>
    /// Wraps one <see cref="WhisperProcessor.ProcessAsync"/> call. Each
    /// non-empty segment becomes one <see cref="CaptionRef"/> with PTS
    /// anchored to <paramref name="windowStartPts"/>.
    /// </summary>
    private static async IAsyncEnumerable<CaptionRef> InferCaptionsAsync(
        WhisperProcessor processor,
        float[] samples,
        TimeSpan windowStartPts,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
        {
            var text = segment.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;

            yield return new CaptionRef(
                new Caption(
                    From: windowStartPts + segment.Start,
                    To: windowStartPts + segment.End,
                    Text: text
                )
            );
        }
    }

    /// <summary>
    /// Converts S16 PCM samples to float32 in <c>[-1, 1]</c> and appends
    /// to <paramref name="target"/>. Same conversion as the old version.
    /// </summary>
    private static void AppendSamplesAsFloat(List<float> target, PcmAudioBuffer buffer)
    {
        const float int16Normalize = 1f / 32768f;
        var span = buffer.Samples.Span[..buffer.SampleCount];
        for (int i = 0; i < span.Length; i++)
        {
            target.Add(span[i] * int16Normalize);
        }
    }
}
