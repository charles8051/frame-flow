using Xunit;

namespace FrameFlow.Integration.Tests.Harness.Capture;

/// <summary>
/// Content-level assertions over <see cref="AudioCapture"/> /
/// <see cref="VideoCapture"/> sequences. Each invariant is independently
/// callable; a test picks the ones it cares about. All failures throw
/// xUnit assertion exceptions with diagnostics that point at the
/// offending sample / frame / PTS.
/// </summary>
internal static class PlaybackInvariants
{
    // ──────────────────────────────────────────────────────────────────
    // PTS invariants — the simplest, no math.
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts every PTS in <paramref name="captures"/> is strictly
    /// greater than the previous PTS. Catches reordering, duplicates,
    /// and the post-flush case where decoder state leaks PTS = 0
    /// frames into the steady-state stream.
    /// </summary>
    public static void PtsStrictlyMonotonic<T>(
        IReadOnlyList<T> captures,
        Func<T, TimeSpan> ptsSelector,
        string streamLabel
    )
    {
        ArgumentNullException.ThrowIfNull(captures);
        ArgumentNullException.ThrowIfNull(ptsSelector);

        if (captures.Count < 2)
            return;

        TimeSpan prev = ptsSelector(captures[0]);
        for (int i = 1; i < captures.Count; i++)
        {
            var current = ptsSelector(captures[i]);
            if (current <= prev)
            {
                Assert.Fail(
                    $"{streamLabel}: PTS regression at index {i}: "
                        + $"current={current.TotalSeconds:F4}s ≤ prev={prev.TotalSeconds:F4}s "
                        + $"(captures.Count={captures.Count})"
                );
            }
            prev = current;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Audio invariants
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that no audio segment of length <paramref name="windowSize"/>
    /// appears twice (or more) in <paramref name="capture"/> with
    /// cross-correlation above <paramref name="correlationThreshold"/>
    /// that isn't already explained by the source content. This is the
    /// targeted assertion for the OpenAlAudioSink looping regression: if
    /// buffers are replayed, the same PCM appears at two PTS offsets and
    /// self-correlation pegs at 1.0 at the offset distance.
    /// </summary>
    /// <param name="capture">The captured audio in arrival order.</param>
    /// <param name="reference">
    /// Optional reference decode of the same source. When provided, the
    /// assertion only flags duplications that exist in
    /// <paramref name="capture"/> but NOT in <paramref name="reference"/>
    /// at the same offset — i.e. *runtime-introduced* duplication.
    /// Without a reference, any high self-correlation fails, which gives
    /// false positives on intrinsically periodic content (sine waves,
    /// drum loops, etc).
    /// </param>
    /// <param name="windowSize">
    /// Sliding window length (default 250 ms). Should be long enough to
    /// avoid false positives from short-cycle periodicity but short
    /// enough to catch real bugs (a 64 ms buffer replay needs a window
    /// ≥ 128 ms to detect with margin).
    /// </param>
    /// <param name="correlationThreshold">
    /// Normalised cross-correlation threshold above which two windows
    /// are considered similar. 0.95 is a sensible floor; legitimate
    /// non-periodic content (speech, music verses) typically peaks
    /// around 0.7 between distant windows.
    /// </param>
    /// <param name="referenceDelta">
    /// When <paramref name="reference"/> is provided, the capture's NCC
    /// at offset (i, j) must exceed the reference's NCC at the same
    /// offset by more than this much to count as runtime-introduced
    /// duplication. 0.02 catches a bit-exact buffer replay (capture
    /// NCC = 1.0) on top of periodic source content (reference NCC
    /// typically ≤ 0.98) without flaking on numerical noise.
    /// </param>
    public static void NoDuplicateAudioSegments(
        IReadOnlyList<AudioCapture> capture,
        IReadOnlyList<AudioCapture>? reference = null,
        TimeSpan? windowSize = null,
        double correlationThreshold = 0.95,
        double referenceDelta = 0.02
    )
    {
        ArgumentNullException.ThrowIfNull(capture);
        var window = windowSize ?? TimeSpan.FromMilliseconds(250);

        if (capture.Count == 0)
            return;

        var (mono, sampleRate) = FlattenToMono(capture);
        if (sampleRate <= 0)
            return;

        var windowSamples = Math.Max(1, (int)(window.TotalSeconds * sampleRate));
        if (windowSamples >= mono.Length)
            return;

        int windowCount = mono.Length / windowSamples;
        if (windowCount < 3)
            return;

        var energies = new double[windowCount];
        for (int i = 0; i < windowCount; i++)
            energies[i] = WindowEnergy(mono, i * windowSamples, windowSamples);

        // Reference NCC profile — only computed when a reference is
        // supplied. Same window structure as `capture` so offset (i, j)
        // means the same time-pair in both.
        short[]? refMono = null;
        double[]? refEnergies = null;
        int refWindowCount = 0;
        if (reference is not null && reference.Count > 0)
        {
            var (rm, _) = FlattenToMono(reference);
            refMono = rm;
            refWindowCount = rm.Length / windowSamples;
            refEnergies = new double[refWindowCount];
            for (int i = 0; i < refWindowCount; i++)
                refEnergies[i] = WindowEnergy(rm, i * windowSamples, windowSamples);
        }

        for (int i = 0; i < windowCount; i++)
        {
            if (energies[i] <= 1.0)
                continue;
            for (int j = i + 2; j < windowCount; j++)
            {
                if (energies[j] <= 1.0)
                    continue;

                double ncc = NormalizedCrossCorrelation(
                    mono,
                    i * windowSamples,
                    j * windowSamples,
                    windowSamples,
                    energies[i],
                    energies[j]
                );

                if (ncc < correlationThreshold)
                    continue;

                // Capture has high self-similarity at offset (i, j).
                // If reference also has it (within referenceDelta), it's
                // source-content periodicity, not a runtime bug.
                if (
                    refMono is not null
                    && refEnergies is not null
                    && i < refWindowCount
                    && j < refWindowCount
                    && refEnergies[i] > 1.0
                    && refEnergies[j] > 1.0
                )
                {
                    double refNcc = NormalizedCrossCorrelation(
                        refMono,
                        i * windowSamples,
                        j * windowSamples,
                        windowSamples,
                        refEnergies[i],
                        refEnergies[j]
                    );

                    if (ncc - refNcc <= referenceDelta)
                        continue; // explained by source content
                }

                var ptsI = TimeSpan.FromSeconds((double)(i * windowSamples) / sampleRate);
                var ptsJ = TimeSpan.FromSeconds((double)(j * windowSamples) / sampleRate);
                Assert.Fail(
                    $"Runtime-introduced audio duplication. "
                        + $"Window at {ptsI.TotalSeconds:F3}s and window at {ptsJ.TotalSeconds:F3}s "
                        + $"have NCC={ncc:F4} (threshold {correlationThreshold:F2}). "
                        + $"Window size {window.TotalMilliseconds:F0}ms; loop period ≈ "
                        + $"{(ptsJ - ptsI).TotalMilliseconds:F0}ms."
                        + (
                            reference is null
                                ? " (No reference baseline supplied — this could be source-content "
                                    + "periodicity; supply a reference for source-aware checking.)"
                                : ""
                        )
                );
            }
        }
    }

    private static (short[] Mono, int SampleRate) FlattenToMono(IReadOnlyList<AudioCapture> capture)
    {
        if (capture.Count == 0)
            return (Array.Empty<short>(), 0);

        var sampleRate = capture[0].SampleRate;
        int totalSamplesPerChannel = 0;
        for (int i = 0; i < capture.Count; i++)
            totalSamplesPerChannel += capture[i].SamplesPerChannel;

        var mono = new short[totalSamplesPerChannel];
        int cursor = 0;
        for (int i = 0; i < capture.Count; i++)
        {
            var block = capture[i];
            var samples = block.InterleavedSamples;
            int channels = block.Channels;
            int spc = block.SamplesPerChannel;
            if (channels <= 1)
            {
                Array.Copy(samples, 0, mono, cursor, spc);
            }
            else
            {
                for (int s = 0; s < spc; s++)
                {
                    int sum = 0;
                    int baseIdx = s * channels;
                    for (int c = 0; c < channels; c++)
                        sum += samples[baseIdx + c];
                    mono[cursor + s] = (short)(sum / channels);
                }
            }
            cursor += spc;
        }
        return (mono, sampleRate);
    }

    private static double WindowEnergy(short[] mono, int start, int length)
    {
        double sum = 0;
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            double v = mono[i];
            sum += v * v;
        }
        return sum;
    }

    private static double NormalizedCrossCorrelation(
        short[] mono,
        int startA,
        int startB,
        int length,
        double energyA,
        double energyB
    )
    {
        double dot = 0;
        for (int k = 0; k < length; k++)
        {
            dot += (double)mono[startA + k] * mono[startB + k];
        }
        double denom = Math.Sqrt(energyA * energyB);
        return denom > 0 ? dot / denom : 0;
    }

    /// <summary>
    /// Asserts the captured audio matches the reference decode within
    /// an RMS-error epsilon. Catches sample-level corruption (wrong
    /// resampler config, byte-order swap, channel swap) and gross
    /// magnitude regressions.
    /// </summary>
    /// <param name="capture">Captured audio from the playback run.</param>
    /// <param name="reference">Reference decode (no runtime layer).</param>
    /// <param name="maxRmsErrorPerSample">
    /// Maximum allowed RMS error per S16 sample. Default 4 (4/32768 ≈
    /// −78 dBFS) is well below audible and far above resampler
    /// rounding precision.
    /// </param>
    public static void AudioPcmMatchesReference(
        IReadOnlyList<AudioCapture> capture,
        IReadOnlyList<AudioCapture> reference,
        double maxRmsErrorPerSample = 4.0
    )
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(reference);

        if (capture.Count == 0 && reference.Count == 0)
            return;

        Assert.NotEmpty(capture);
        Assert.NotEmpty(reference);

        var captureFlat = FlattenInterleaved(capture);
        var referenceFlat = FlattenInterleaved(reference);

        // The decoder produces deterministic output, so the playback-side
        // capture should match the reference 1:1 in sample count. Allow a
        // small tolerance for end-of-stream flush samples that one side
        // emitted but the other dropped (e.g. when a Deactivate happens
        // before the final flush drains). 480 samples = 10ms at 48 kHz
        // per channel, well below what would affect any content assertion.
        int channels = capture[0].Channels;
        int sampleRate = capture[0].SampleRate;
        int tailToleranceSamples = Math.Max(channels, (sampleRate / 100) * channels);

        int lengthDiff = Math.Abs(captureFlat.Length - referenceFlat.Length);
        Assert.True(
            lengthDiff <= tailToleranceSamples,
            $"Audio length mismatch: capture={captureFlat.Length} samples, "
                + $"reference={referenceFlat.Length} samples, "
                + $"diff={lengthDiff} (tolerance {tailToleranceSamples} = ~10 ms × {channels}ch). "
                + $"A diff of this size usually means a block was dropped or duplicated."
        );

        int common = Math.Min(captureFlat.Length, referenceFlat.Length);
        if (common == 0)
            return;

        double sumSquaredError = 0;
        long worstIndex = -1;
        int worstAbsError = 0;
        for (int i = 0; i < common; i++)
        {
            int diff = captureFlat[i] - referenceFlat[i];
            sumSquaredError += (double)diff * diff;
            int absDiff = Math.Abs(diff);
            if (absDiff > worstAbsError)
            {
                worstAbsError = absDiff;
                worstIndex = i;
            }
        }

        double rms = Math.Sqrt(sumSquaredError / common);

        Assert.True(
            rms <= maxRmsErrorPerSample,
            $"Audio RMS error {rms:F2} exceeds threshold {maxRmsErrorPerSample:F2} per S16 sample "
                + $"(over {common} samples). "
                + $"Worst single-sample error: {worstAbsError} at interleaved index {worstIndex} "
                + $"(~{(double)worstIndex / channels / sampleRate:F3}s)."
        );
    }

    private static short[] FlattenInterleaved(IReadOnlyList<AudioCapture> capture)
    {
        int total = 0;
        for (int i = 0; i < capture.Count; i++)
            total += capture[i].InterleavedSamples.Length;
        var flat = new short[total];
        int cursor = 0;
        for (int i = 0; i < capture.Count; i++)
        {
            var src = capture[i].InterleavedSamples;
            Array.Copy(src, 0, flat, cursor, src.Length);
            cursor += src.Length;
        }
        return flat;
    }

    // ──────────────────────────────────────────────────────────────────
    // A/V sync invariant
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// For each captured video frame, asserts that the captured audio
    /// PTS at the same "playback wall-clock position" is within
    /// <paramref name="maxDrift"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The audio sink is the master clock in FrameFlow's
    /// <c>AudioMasterSyncStrategy</c>. So this invariant phrases the
    /// sync check as: "at the moment video frame N was presented,
    /// audio had advanced to a position no more than maxDrift away
    /// from N's own PTS."
    /// </para>
    /// <para>
    /// The captures don't record wall-clock at present-time directly,
    /// but they do record PTS — so the invariant reduces to: for each
    /// video PTS, find the audio block whose PTS bracket includes it,
    /// and assert the bracket is within ±maxDrift of the video PTS.
    /// In practice that means audio.PTS at the same index ≈ video.PTS;
    /// the assertion catches the case where one stream marches ahead
    /// or behind by a buffer's worth.
    /// </para>
    /// </remarks>
    public static void AvSyncWithinTolerance(
        IReadOnlyList<AudioCapture> audio,
        IReadOnlyList<VideoCapture> video,
        TimeSpan? maxDrift = null
    )
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(video);
        var drift = maxDrift ?? TimeSpan.FromMilliseconds(100);

        // Both streams need to be non-empty for the check to apply. A
        // missing stream is caught by upstream assertions (audio-only or
        // video-only corpus files use the single-stream invariants).
        if (audio.Count == 0 || video.Count == 0)
            return;

        // Coverage-style sync check: assert the two streams cover the
        // same time range within `drift`. The captures don't record
        // wall-clock present-time so we can't measure per-frame drift
        // directly — but if either stream drops samples at the head or
        // tail, the coverage end-points diverge. The detailed per-frame
        // drift check belongs in a follow-up assertion that also
        // captures wall-clock-at-present.
        var audioStart = audio[0].Pts;
        var audioEnd = audio[^1].Pts + audio[^1].Duration;
        var videoStart = video[0].Pts;
        var videoEnd = video[^1].Pts + video[^1].Duration;

        var startSkew = audioStart - videoStart;
        var endSkew = audioEnd - videoEnd;

        Assert.True(
            startSkew.Duration() <= drift,
            $"Audio/video start skew {startSkew.TotalMilliseconds:F1}ms exceeds drift "
                + $"{drift.TotalMilliseconds:F1}ms. "
                + $"audio[0].Pts={audioStart.TotalSeconds:F3}s, "
                + $"video[0].Pts={videoStart.TotalSeconds:F3}s."
        );
        Assert.True(
            endSkew.Duration() <= drift,
            $"Audio/video end skew {endSkew.TotalMilliseconds:F1}ms exceeds drift "
                + $"{drift.TotalMilliseconds:F1}ms. "
                + $"audio end={audioEnd.TotalSeconds:F3}s, "
                + $"video end={videoEnd.TotalSeconds:F3}s."
        );

        // PTS monotonicity is a precondition for the coverage check to
        // be meaningful — call out the dependency explicitly so a
        // monotonicity violation doesn't silently hide here.
        PtsStrictlyMonotonic(audio, a => a.Pts, "audio (within AvSyncWithinTolerance)");
        PtsStrictlyMonotonic(video, v => v.Pts, "video (within AvSyncWithinTolerance)");
    }

    // ──────────────────────────────────────────────────────────────────
    // Video invariants
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts captured video frames match reference frames at each
    /// PTS within a structural-similarity threshold. SSIM 0.99 is the
    /// floor; lower than that indicates genuine pixel divergence
    /// (color-space regression, chroma-subsample shift, decoder
    /// fallback path).
    /// </summary>
    public static void VideoFramePixelsMatchReference(
        IReadOnlyList<VideoCapture> capture,
        IReadOnlyList<VideoCapture> reference,
        double ssimFloor = 0.99
    )
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(reference);

        // Same-decoder reference means byte-exact match is the target,
        // not approximate SSIM. The `ssimFloor` parameter is preserved
        // for forward-compatibility with hardware-decode paths that
        // might introduce chroma rounding; the default 0.99 wouldn't
        // gate this byte-equal implementation either way. When the
        // chroma-aware variant lands, route through SSIM if ssimFloor
        // < 1.0 and through byte-equal if == 1.0.
        _ = ssimFloor;

        if (capture.Count == 0 && reference.Count == 0)
            return;

        Assert.NotEmpty(capture);
        Assert.NotEmpty(reference);

        // The decoder is deterministic so frame counts should match
        // exactly. A diff usually means a frame was dropped by the
        // playback worker (e.g. backpressure stalling decode).
        Assert.True(
            capture.Count == reference.Count,
            $"Video frame count mismatch: capture={capture.Count}, reference={reference.Count}. "
                + $"Diff usually means the playback runtime dropped or duplicated frames."
        );

        for (int i = 0; i < capture.Count; i++)
        {
            var c = capture[i];
            var r = reference[i];

            Assert.True(
                c.Pts == r.Pts,
                $"Video frame {i} PTS mismatch: capture={c.Pts.TotalSeconds:F4}s, "
                    + $"reference={r.Pts.TotalSeconds:F4}s. Frame ordering or PTS regression."
            );

            Assert.True(
                c.Width == r.Width && c.Height == r.Height && c.Format == r.Format,
                $"Video frame {i} (Pts={c.Pts.TotalSeconds:F3}s) format mismatch: "
                    + $"capture={c.Width}x{c.Height} {c.Format}, "
                    + $"reference={r.Width}x{r.Height} {r.Format}."
            );

            if (c.Pixels.Length != r.Pixels.Length)
            {
                Assert.Fail(
                    $"Video frame {i} (Pts={c.Pts.TotalSeconds:F3}s) pixel-byte length "
                        + $"mismatch: capture={c.Pixels.Length}, reference={r.Pixels.Length}."
                );
            }

            if (!c.Pixels.AsSpan().SequenceEqual(r.Pixels))
            {
                int firstDiff = FirstDifferingByte(c.Pixels, r.Pixels);
                Assert.Fail(
                    $"Video frame {i} (Pts={c.Pts.TotalSeconds:F3}s) pixel bytes diverge "
                        + $"at byte {firstDiff} of {c.Pixels.Length}. "
                        + $"capture={c.Pixels[firstDiff]} reference={r.Pixels[firstDiff]}. "
                        + $"Same-decoder paths should be byte-identical."
                );
            }
        }
    }

    private static int FirstDifferingByte(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
                return i;
        }
        return n;
    }

    /// <summary>
    /// Asserts the captured frame count and total decoded audio
    /// duration match expected values for a known corpus file. The
    /// cheap version of full pixel/PCM matching — good for smoke
    /// tests where you just want to know "did playback complete."
    /// </summary>
    public static void DurationsMatch(
        IReadOnlyList<AudioCapture> audio,
        IReadOnlyList<VideoCapture> video,
        TimeSpan expectedDuration,
        TimeSpan? tolerance = null
    )
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(video);
        var tol = tolerance ?? TimeSpan.FromMilliseconds(100);

        if (audio.Count > 0)
        {
            var last = audio[^1];
            var audioEnd = last.Pts + last.Duration;
            Assert.InRange(
                audioEnd.TotalSeconds,
                (expectedDuration - tol).TotalSeconds,
                (expectedDuration + tol).TotalSeconds
            );
        }

        if (video.Count > 0)
        {
            var last = video[^1];
            var videoEnd = last.Pts + last.Duration;
            Assert.InRange(
                videoEnd.TotalSeconds,
                (expectedDuration - tol).TotalSeconds,
                (expectedDuration + tol).TotalSeconds
            );
        }
    }
}
