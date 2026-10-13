// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Whisper.Core;
using Whisper.net;

namespace FrameFlow.Whisper;

/// <summary>
/// Substrate operator form of the old <c>WhisperPipelineExtensions.TranscribeWithWhisper</c>.
/// Wraps Whisper.net inference as a 1→N <see cref="MultiOperatorNode{TIn, TOut}"/>:
/// each upstream <see cref="PcmAudioBuffer"/> contributes samples to a
/// rolling window; each completed window runs one Whisper inference call
/// and yields a <see cref="CaptionRef"/> per non-empty segment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Diff from old.</b> The original
/// <c>WhisperPipelineExtensions.TranscribeWithWhisper</c>
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
/// <c>AudioOperators.Resample</c>'s post-EOS resampler buffer.
/// For streaming captioning (the LiveCaptioning use case) the trailing
/// loss is inconsequential; for offline transcription of finite files
/// the last &lt; <see cref="WhisperOptions.EffectiveWindowSize"/> of
/// audio gets silently dropped. Tracked in docs/DEFERRED_WORK.md as "MultiOperatorNode
/// needs Cleanup hook" — fix lives in the graph substrate, not here.
/// </para>
/// <para>
/// <b>Native resource lifetime.</b> <see cref="WhisperFactory"/> and
/// <see cref="WhisperProcessor"/> are allocated lazily on the first
/// body call and captured in the operator's closure. They live until
/// the closure is GC'd — typically when the graph itself becomes
/// unreachable. Whisper.net's types implement <see cref="IDisposable"/>;
/// the underlying native handles are released via finalizers. Same
/// "outlives graph run, lean on GC" pattern as
/// <c>AudioOperators.Resample</c>. Consumers with tight cycle
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
    /// <remarks>
    /// <para>
    /// The window follows its input stream's timeline (ADR-0084). A buffer of another timeline
    /// than the held audio's (a seek, a loop, the next item) drops the partial window and starts a
    /// new one at its own time, so captions are never built from audio of two places and their
    /// times match the media position. Within one timeline the window does not look at the time,
    /// so a gap where buffers were dropped joins the audio on both sides. A buffer on
    /// <see cref="TimelineId.None"/>, or any buffer before the stream has named a timeline, is
    /// judged by its <see cref="PcmAudioBuffer.PresentationTime"/> instead: more than 50 ms from
    /// where the held audio ends, it starts a new window. A node kept across seeks, so the model
    /// loads once, therefore needs no reset. Each caption carries the timeline of the audio its
    /// window holds.
    /// </para>
    /// <para>
    /// The stream must keep the player's order: the player's own audio chain, or one first-in,
    /// first-out queue fed from it. Multi-channel input is mixed to mono, which is what Whisper
    /// reads.
    /// </para>
    /// </remarks>
    public static MultiOperatorNode<PcmAudioBuffer, CaptionRef> TranscribeWithWhisper(
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
        WindowClock? clock = null;

        return new MultiOperatorNode<PcmAudioBuffer, CaptionRef>(id, TranscribeImpl);

        async IAsyncEnumerable<CaptionRef> TranscribeImpl(
            PcmAudioBuffer input,
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

            var buffer = input;
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

            var admission = WindowTimeline.Admit(
                clock,
                buffer.PresentationTime,
                buffer.FrameCount,
                opts.InputSampleRate,
                buffer.Timeline
            );
            if (admission.Discard)
                windowBuffer.Clear();

            WhisperSamples.AppendMono(windowBuffer, buffer.Samples.Span, buffer.Channels);
            var current = admission.Clock;
            clock = current;

            while (WindowTimeline.CloseOne(current, windowSamples) is { } closed)
            {
                // Take the window out and advance the clock before inference runs, so a run
                // cancelled during it leaves the held samples and the clock in agreement.
                var slice = new float[windowSamples];
                windowBuffer.CopyTo(0, slice, 0, windowSamples);
                windowBuffer.RemoveRange(0, windowSamples);
                current = closed.Remainder;
                clock = current;

                await foreach (
                    var caption in InferCaptionsAsync(processor!, slice, closed.Start, closed.Timeline, ct)
                        .ConfigureAwait(false)
                )
                {
                    yield return caption;
                }
            }
        }
    }

    /// <summary>
    /// Wraps one <c>WhisperProcessor.ProcessAsync</c> call. Each
    /// non-empty segment becomes one <see cref="CaptionRef"/> with PTS
    /// anchored to <paramref name="windowStartPts"/>, on the window's
    /// <paramref name="timeline"/>.
    /// </summary>
    private static async IAsyncEnumerable<CaptionRef> InferCaptionsAsync(
        WhisperProcessor processor,
        float[] samples,
        TimeSpan windowStartPts,
        TimelineId timeline,
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
                ),
                timeline
            );
        }
    }
}
