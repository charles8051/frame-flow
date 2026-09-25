// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Fills the samples of a new <see cref="PcmAudioBuffer"/>. Called once, synchronously, by
/// <see cref="PcmAudioBuffer.Create{TState}"/> before the buffer is published.
/// </summary>
/// <typeparam name="TState">The state the caller passes through, so the callback can be static.</typeparam>
/// <param name="samples">
/// Room for the requested capacity of interleaved samples. The span exists only for the call.
/// </param>
/// <param name="state">The state passed to <see cref="PcmAudioBuffer.Create{TState}"/>.</param>
/// <returns>How many samples the callback wrote, from the start of <paramref name="samples"/>.</returns>
public delegate int PcmAudioFill<in TState>(Span<short> samples, TState state);
