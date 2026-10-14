// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding;
using FrameFlow.Media;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Pins #713: a pass whose graph faults ends, and throws the fault, even when the demux pump is
/// waiting for room in a decoder queue that the faulted graph will never drain again. The run
/// cancels the pump at the graph's fault rather than after both have ended (ADR-0086, decision 1).
/// </summary>
/// <remarks>
/// The video decoder's queue holds one packet, and the sink holds the first frame until the test
/// lets it throw, so the pump is parked on a full queue when the graph faults: what parks it is the
/// pass's own seam, not a duration. The bound only limits a failure.
/// </remarks>
public sealed class MediaPassFaultTests
{
    private const string VideoFile = "test-video-h264-yuv420p.mp4";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [RequiresFfmpegAndCorpusFact]
    public async Task AGraphThatFaultsWhileThePumpIsParkedOnAFullQueue_EndsTheRun_WithTheFault()
    {
        var sink = new FailOnCueVideoSink();
        await using var pass = await ((PassBuilder)FrameFlowPass.Create(Corpus(VideoFile)))
            .WithDecoderOptions(video: new VideoDecoderOptions { PacketQueueCapacity = 1 })
            .WithVideoSink(sink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildAsync();

        var run = pass.RunToCompletionAsync();
        var parked = pass.WaitUntilPumpParkedAsync();
        Assert.Same(parked, await Task.WhenAny(parked, run).WaitAsync(Bound));

        // The held present throws. The graph faults and stops pulling from the decoder, so its
        // queue stays full and the pump stays parked unless the run cancels it.
        sink.Fail();

        var fault = await Assert.ThrowsAsync<GraphFaultException>(() => run.WaitAsync(Bound));
        Assert.IsType<InvalidOperationException>(fault.InnerException);
        Assert.Equal(FailOnCueVideoSink.Failure, fault.InnerException!.Message);
    }

    private static string Corpus(string name) =>
        TestEnvironment.GetCorpusFile(name) ?? throw new InvalidOperationException($"{name} is missing.");

    /// <summary>
    /// A video sink that holds its first frame until <see cref="Fail"/>, then throws from that
    /// present. It observes its token while it holds.
    /// </summary>
    private sealed class FailOnCueVideoSink : IVideoSink
    {
        public const string Failure = "The sink failed on cue.";

        private readonly TaskCompletionSource _cue = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Fail() => _cue.TrySetResult();

        public async ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            await _cue.Task.WaitAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(Failure);
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
