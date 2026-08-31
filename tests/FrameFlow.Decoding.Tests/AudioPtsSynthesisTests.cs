using FrameFlow.Decoding.Internal;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Tests for the pure audio-PTS synthesis fold (<see cref="AudioPtsSynthesis"/>) — the
/// ADR-0055 follow-up that turned <c>AudioDecoder</c>'s in-place synthetic-PTS accumulator
/// into a threaded value. These close the ADR-0048 gap that flagged the synthetic path as
/// having no targeted coverage (the corpus carries explicit PTS, so integration never
/// exercises it).
/// </summary>
public sealed class AudioPtsSynthesisTests
{
    private const int Rate = 48_000;
    private const int FrameSamples = 1024;

    [Fact]
    public void Initial_IsZero() => Assert.Equal(0, PtsSynthesisState.Initial.AccumulatedSamples);

    [Fact]
    public void Advance_WithValidPts_ScalesByTimeBase()
    {
        // framePts=90000 at time base 1/90000 → 1.0 s.
        var r = AudioPtsSynthesis.Advance(
            PtsSynthesisState.Initial,
            hasValidPts: true,
            framePts: 90_000,
            timeBaseNum: 1,
            timeBaseDen: 90_000,
            outputSamplesPerChannel: FrameSamples,
            targetSampleRate: Rate
        );

        Assert.False(r.UsedSynthetic);
        Assert.Equal(1.0, r.Pts.TotalSeconds, precision: 6);
        // Accumulator advances even when a real PTS was used.
        Assert.Equal(FrameSamples, r.State.AccumulatedSamples);
    }

    [Fact]
    public void Advance_WithoutPts_SynthesisesFirstFrameAtZero()
    {
        var r = AudioPtsSynthesis.Advance(
            PtsSynthesisState.Initial,
            hasValidPts: false,
            framePts: 0,
            timeBaseNum: 1,
            timeBaseDen: Rate,
            outputSamplesPerChannel: FrameSamples,
            targetSampleRate: Rate
        );

        Assert.True(r.UsedSynthetic);
        Assert.Equal(TimeSpan.Zero, r.Pts); // accumulated-before-add is 0 → t=0
        Assert.Equal(FrameSamples, r.State.AccumulatedSamples);
    }

    [Fact]
    public void Advance_SyntheticStream_IsMonotonicAndDriftFree()
    {
        // The whole point: synthesising from the integer sample count each step (rather
        // than accumulating TimeSpan deltas) must not drift over a long PTS-less stream.
        var state = PtsSynthesisState.Initial;
        var prev = TimeSpan.MinValue;

        for (int i = 0; i < 10_000; i++)
        {
            var r = AudioPtsSynthesis.Advance(
                state,
                hasValidPts: false,
                framePts: 0,
                timeBaseNum: 1,
                timeBaseDen: Rate,
                outputSamplesPerChannel: FrameSamples,
                targetSampleRate: Rate
            );

            double expectedSeconds = (double)((long)i * FrameSamples) / Rate;
            // precision 6 (µs) comfortably exceeds TimeSpan's 100 ns tick quantisation —
            // any real drift would grow to milliseconds over 10 000 frames and fail here.
            Assert.Equal(expectedSeconds, r.Pts.TotalSeconds, precision: 6);
            Assert.True(r.Pts >= prev, $"PTS went backwards at frame {i}");
            Assert.True(r.UsedSynthetic);

            prev = r.Pts;
            state = r.State;
        }

        Assert.Equal(10_000L * FrameSamples, state.AccumulatedSamples);
    }

    [Fact]
    public void Advance_ValidThenSynthetic_ContinuesMonotonicallyFromAccumulator()
    {
        // Real PTS frames advance the accumulator, so when the stream drops to NOPTS the
        // synthesised timestamp continues from where the samples left off rather than
        // restarting at zero.
        var state = PtsSynthesisState.Initial;

        // Three valid-PTS frames (each 1024 samples @ 48 kHz).
        for (int i = 0; i < 3; i++)
        {
            var r = AudioPtsSynthesis.Advance(
                state,
                hasValidPts: true,
                framePts: i,
                timeBaseNum: 1,
                timeBaseDen: Rate,
                outputSamplesPerChannel: FrameSamples,
                targetSampleRate: Rate
            );
            state = r.State;
        }

        Assert.Equal(3L * FrameSamples, state.AccumulatedSamples);

        // Now a PTS-less frame: synthesised from the accumulated 3*1024 samples.
        var synth = AudioPtsSynthesis.Advance(
            state,
            hasValidPts: false,
            framePts: 0,
            timeBaseNum: 1,
            timeBaseDen: Rate,
            outputSamplesPerChannel: FrameSamples,
            targetSampleRate: Rate
        );

        Assert.True(synth.UsedSynthetic);
        Assert.Equal((double)(3 * FrameSamples) / Rate, synth.Pts.TotalSeconds, precision: 6);
        Assert.Equal(4L * FrameSamples, synth.State.AccumulatedSamples);
    }
}
