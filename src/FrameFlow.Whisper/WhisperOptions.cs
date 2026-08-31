// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Whisper;

/// <summary>
/// Configuration for the
/// <see cref="WhisperPipelineExtensions.TranscribeWithWhisper"/>
/// pipeline operator.
/// </summary>
/// <param name="Language">
/// ISO-639-1 language code passed to Whisper. Defaults to <c>"en"</c>.
/// Whisper auto-detect is available via <c>"auto"</c> at the cost of
/// some extra latency on the first window.
/// </param>
/// <param name="WindowSize">
/// How much audio to buffer before running a Whisper inference call.
/// Larger windows give better context (fewer hallucinations, fewer
/// boundary truncations) at the cost of higher latency to first
/// caption. Defaults to 5 seconds when <see cref="TimeSpan.Zero"/>.
/// </param>
/// <param name="InputSampleRate">
/// Expected sample rate of the upstream <c>PcmAudioBuffer</c> stream.
/// Defaults to 16,000 Hz — Whisper's native rate. The operator does
/// <i>not</i> resample; the caller is expected to compose a
/// <c>.Resample(InputSampleRate, InputChannels)</c> operator
/// upstream. Mismatched input throws at the first <c>Process</c>
/// call.
/// </param>
/// <param name="InputChannels">
/// Expected channel count of the upstream <c>PcmAudioBuffer</c>
/// stream. Defaults to 1 (mono). Same composability story as
/// <see cref="InputSampleRate"/> — compose a <c>Resample</c>
/// upstream if your source is stereo.
/// </param>
public sealed record WhisperOptions(
    string Language = "en",
    TimeSpan WindowSize = default,
    int InputSampleRate = 16_000,
    int InputChannels = 1
)
{
    /// <summary>
    /// Returns <see cref="WindowSize"/> if set, otherwise the default
    /// 5-second window.
    /// </summary>
    public TimeSpan EffectiveWindowSize =>
        WindowSize > TimeSpan.Zero ? WindowSize : TimeSpan.FromSeconds(5);
}
