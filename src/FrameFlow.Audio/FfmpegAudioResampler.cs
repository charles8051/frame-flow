// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Audio;

/// <summary>
/// <see cref="IAudioResampler"/> backed by FFmpeg's <c>libswresample</c>.
/// Mirrors the setup pattern <c>FrameFlow.Decoding.AudioDecoder</c> uses
/// internally for its own resample-to-output-format pass; this type
/// makes the same machinery available as a reusable component for
/// any consumer that needs to convert <see cref="PcmAudioBuffer"/>
/// streams between formats.
/// </summary>
internal sealed unsafe class FfmpegAudioResampler : IAudioResampler
{
    /// <summary>The S16 interleaved PCM sample format used throughout
    /// FrameFlow (matches <see cref="PcmAudioBuffer"/>'s payload).</summary>
    private const int AvSampleFmtS16 = 1;

    private SwrContextHandle? _swr;
    private bool _initialized;
    private bool _disposed;
    private int _sourceSampleRate;
    private int _sourceChannels;

    public int TargetSampleRate { get; }
    public int TargetChannels { get; }

    public FfmpegAudioResampler(int targetSampleRate, int targetChannels)
    {
        TargetSampleRate = targetSampleRate;
        TargetChannels = targetChannels;
    }

    public PcmAudioBuffer Process(PcmAudioBuffer input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized(input);

        if (input.SampleCount == 0 || input.Channels == 0)
        {
            // Empty input — still emit an empty output (with the input's PTS)
            // rather than null, so consumers that don't check SampleCount
            // before disposing aren't surprised.
            return CreateEmpty(input.PresentationTime);
        }

        var swrPtr = _swr!.DangerousGetHandle();

        int inputFramesPerChannel = input.SampleCount / input.Channels;

        // Worst-case output sample count: every input sample produces one output
        // sample after rate conversion, plus the resampler's internal buffer
        // delay, plus headroom for filter taps.
        long delay = FFSwResample.swr_get_delay(swrPtr, TargetSampleRate);
        int maxOutputFramesPerChannel =
            (int)((delay + inputFramesPerChannel) * (double)TargetSampleRate / _sourceSampleRate)
            + 256;

        var owner = MemoryPool<short>.Shared.Rent(maxOutputFramesPerChannel * TargetChannels);
        int actualOutputFramesPerChannel;
        try
        {
            actualOutputFramesPerChannel = RunSwrConvert(
                swrPtr,
                input.Samples.Span,
                inputFramesPerChannel,
                owner.Memory.Span,
                maxOutputFramesPerChannel
            );
        }
        catch
        {
            owner.Dispose();
            throw;
        }

        if (actualOutputFramesPerChannel < 0)
        {
            owner.Dispose();
            throw new InvalidOperationException(
                $"swr_convert returned error {actualOutputFramesPerChannel}."
            );
        }

        return new PcmAudioBuffer(
            sampleData: owner,
            sampleCount: actualOutputFramesPerChannel * TargetChannels,
            sampleRate: TargetSampleRate,
            channels: TargetChannels,
            presentationTime: input.PresentationTime
        );
    }

    public PcmAudioBuffer? Flush(TimeSpan finalPresentationTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _swr is null || _swr.IsInvalid)
            return null;

        var swrPtr = _swr.DangerousGetHandle();
        long delay = FFSwResample.swr_get_delay(swrPtr, TargetSampleRate);
        if (delay <= 0)
            return null;

        // Convert delay (in input-rate samples) to output-rate samples, with
        // a small headroom for any final filter contribution.
        int maxOutputFramesPerChannel =
            (int)(delay * (double)TargetSampleRate / _sourceSampleRate) + 64;
        if (maxOutputFramesPerChannel <= 0)
            return null;

        var owner = MemoryPool<short>.Shared.Rent(maxOutputFramesPerChannel * TargetChannels);
        int actualOutputFramesPerChannel;
        try
        {
            // Flush by calling swr_convert with input=null and in_count=0.
            actualOutputFramesPerChannel = RunSwrFlush(
                swrPtr,
                owner.Memory.Span,
                maxOutputFramesPerChannel
            );
        }
        catch
        {
            owner.Dispose();
            throw;
        }

        if (actualOutputFramesPerChannel <= 0)
        {
            owner.Dispose();
            return null;
        }

        return new PcmAudioBuffer(
            sampleData: owner,
            sampleCount: actualOutputFramesPerChannel * TargetChannels,
            sampleRate: TargetSampleRate,
            channels: TargetChannels,
            presentationTime: finalPresentationTime
        );
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _swr is null || _swr.IsInvalid)
            return;

        // swr's documented "drop everything" path: convert with zero input
        // into a single-sample output that we throw away. Internally this
        // marks the resampler as reset; subsequent calls start fresh.
        var swrPtr = _swr.DangerousGetHandle();
        Span<short> sink = stackalloc short[TargetChannels];
        fixed (short* sinkPtr = sink)
        {
            byte* singleSinkPtr = (byte*)sinkPtr;
            nint outBuf = (nint)singleSinkPtr;
            _ = FFSwResample.swr_convert(
                ctx: swrPtr,
                output: ref outBuf,
                out_count: 1,
                input: nint.Zero,
                in_count: 0
            );
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _swr?.Dispose();
        _swr = null;
        _initialized = false;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private void EnsureInitialized(PcmAudioBuffer input)
    {
        if (_initialized)
        {
            if (input.SampleRate != _sourceSampleRate || input.Channels != _sourceChannels)
            {
                throw new InvalidOperationException(
                    $"AudioResampler input format changed mid-stream. "
                        + $"Configured for {_sourceChannels}ch/{_sourceSampleRate}Hz; "
                        + $"got {input.Channels}ch/{input.SampleRate}Hz. "
                        + $"Call Reset() and reconfigure if the source format changes."
                );
            }
            return;
        }

        if (input.SampleRate <= 0 || input.Channels <= 0)
        {
            throw new InvalidOperationException(
                $"Cannot initialize resampler from empty/uninitialized buffer "
                    + $"({input.Channels}ch/{input.SampleRate}Hz)."
            );
        }

        nint swrPtr = FFSwResample.swr_alloc();
        if (swrPtr == nint.Zero)
            throw new InvalidOperationException("swr_alloc returned null.");

        _swr = new SwrContextHandle(swrPtr);
        ConfigureSwr(swrPtr, input.SampleRate, input.Channels);
        _sourceSampleRate = input.SampleRate;
        _sourceChannels = input.Channels;
        _initialized = true;
    }

    private void ConfigureSwr(nint swrPtr, int sourceSampleRate, int sourceChannels)
    {
        // FFmpeg 7.x: use the string-based channel layout API. Numeric mask
        // layouts are deprecated and swr_init rejects them with EINVAL.
        string inLayout = ChannelLayoutName(sourceChannels);
        string outLayout = ChannelLayoutName(TargetChannels);

        FFAvUtil.av_opt_set(swrPtr, "in_chlayout", inLayout, 0);
        FFAvUtil.av_opt_set_int(swrPtr, "in_sample_rate", sourceSampleRate, 0);
        FFAvUtil.av_opt_set_int(swrPtr, "in_sample_fmt", AvSampleFmtS16, 0);

        FFAvUtil.av_opt_set(swrPtr, "out_chlayout", outLayout, 0);
        FFAvUtil.av_opt_set_int(swrPtr, "out_sample_rate", TargetSampleRate, 0);
        FFAvUtil.av_opt_set_int(swrPtr, "out_sample_fmt", AvSampleFmtS16, 0);

        int rc = FFSwResample.swr_init(swrPtr);
        if (rc < 0)
        {
            _swr?.Dispose();
            _swr = null;
            throw new InvalidOperationException(
                $"swr_init failed ({rc}). "
                    + $"Source: {sourceChannels}ch/{sourceSampleRate}Hz/S16; "
                    + $"Target: {TargetChannels}ch/{TargetSampleRate}Hz/S16."
            );
        }
    }

    private static string ChannelLayoutName(int channels) =>
        channels switch
        {
            1 => "mono",
            2 => "stereo",
            6 => "5.1",
            8 => "7.1",
            _ => $"{channels}c",
        };

    private PcmAudioBuffer CreateEmpty(TimeSpan pts)
    {
        // A zero-sample buffer still needs a real (empty) memory owner so
        // Dispose works. Renting 1 slot is the cheapest safe choice.
        var owner = MemoryPool<short>.Shared.Rent(1);
        return new PcmAudioBuffer(
            sampleData: owner,
            sampleCount: 0,
            sampleRate: TargetSampleRate,
            channels: TargetChannels,
            presentationTime: pts
        );
    }

    /// <summary>
    /// Calls <c>swr_convert</c> with interleaved S16 input and output
    /// buffers. Constructs the <c>byte**</c> plane-pointer-array on the
    /// stack (interleaved formats have a single plane, so the array has
    /// one entry).
    /// </summary>
    private static int RunSwrConvert(
        nint swrPtr,
        ReadOnlySpan<short> input,
        int inFramesPerChannel,
        Span<short> output,
        int outFramesPerChannel
    )
    {
        fixed (short* inPtr = input)
        fixed (short* outPtr = output)
        {
            byte* inPlane = (byte*)inPtr;
            byte* outPlane = (byte*)outPtr;
            nint inPlanes = (nint)(&inPlane);
            nint outBuf = (nint)outPlane;
            return FFSwResample.swr_convert(
                ctx: swrPtr,
                output: ref outBuf,
                out_count: outFramesPerChannel,
                input: inPlanes,
                in_count: inFramesPerChannel
            );
        }
    }

    /// <summary>
    /// Calls <c>swr_convert</c> with no input (flush mode) — drains the
    /// resampler's internal buffered samples.
    /// </summary>
    private static int RunSwrFlush(nint swrPtr, Span<short> output, int outFramesPerChannel)
    {
        fixed (short* outPtr = output)
        {
            byte* outPlane = (byte*)outPtr;
            nint outBuf = (nint)outPlane;
            return FFSwResample.swr_convert(
                ctx: swrPtr,
                output: ref outBuf,
                out_count: outFramesPerChannel,
                input: nint.Zero,
                in_count: 0
            );
        }
    }
}
