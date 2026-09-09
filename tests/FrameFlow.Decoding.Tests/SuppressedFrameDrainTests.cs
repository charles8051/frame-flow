using FrameFlow.Decoding.Internal;
using Xunit;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Pins the contract the readback skip relies on: a codec may report
/// <see cref="CodecReturn.Ok"/> and then build nothing, and the driver skips that
/// frame and keeps draining.
/// </summary>
/// <remarks>
/// <see cref="IDecodeCodec{TFrame}.BuildFrame"/> has always said so — "may return
/// null when conversion yields nothing for this frame (the driver skips it)" —
/// and the conversion-failure path has always used it. The readback skip is a
/// second caller of the same path, and a review read it as a violation twice, so
/// the behaviour is worth a test rather than a paragraph.
/// </remarks>
public sealed class SuppressedFrameDrainTests
{
    [Fact]
    public async Task SuppressedFramesAreSkippedAndTheRestStillDrain()
    {
        // Every third frame suppressed, exactly as ReadbackEveryN = 3 does it.
        var codec = new SuppressingCodec(frameCount: 9, keepEveryN: 3);

        var ids = new List<int>();
        await foreach (var frame in DecodeDriver.RunAsync(codec, CancellationToken.None))
            ids.Add(frame.Id);

        // The kept ones, in order, and nothing else. A suppressed frame must not
        // surface as a null, a stale repeat of its predecessor, or a gap that stops
        // the loop early.
        Assert.Equal([3, 6, 9], ids);
        Assert.Equal(9, codec.FramesReceived);
    }

    [Fact]
    public async Task SuppressingEveryFrameYieldsNothingAndStillCompletes()
    {
        // The degenerate end of the same path: the loop has to terminate on the
        // codec's end-of-stream rather than on having produced something.
        var codec = new SuppressingCodec(frameCount: 5, keepEveryN: int.MaxValue);

        var ids = new List<int>();
        await foreach (var frame in DecodeDriver.RunAsync(codec, CancellationToken.None))
            ids.Add(frame.Id);

        Assert.Empty(ids);
        Assert.Equal(5, codec.FramesReceived);
    }

    private sealed record Built(int Id);

    /// <summary>
    /// Produces <c>frameCount</c> frames one per receive, building only every
    /// <c>keepEveryN</c>th and returning null for the rest — the shape
    /// <c>VideoDecoder</c> takes when the readback cadence skips a copy.
    /// </summary>
    private sealed class SuppressingCodec(int frameCount, int keepEveryN)
        : IDecodeCodec<Built>
    {
        private int _received;
        private Built? _built;
        private bool _inputDone;

        public int FramesReceived => _received;

        public ValueTask<bool> TryBeginNextInputAsync(CancellationToken cancellationToken)
        {
            // One input, then end of stream: every frame comes out on the flush.
            if (_inputDone)
                return ValueTask.FromResult(false);
            _inputDone = true;
            return ValueTask.FromResult(true);
        }

        public CodecReturn SendCurrentInput() => CodecReturn.Ok;

        public CodecReturn ReceiveFrame()
        {
            if (_received >= frameCount)
                return CodecReturn.EndOfStream;

            _received++;

            // Ok either way. The frame was received from the codec; whether a managed
            // frame gets built from it is the next question, and null is its answer.
            _built = _received % keepEveryN == 0 ? new Built(_received) : null;
            return CodecReturn.Ok;
        }

        public Built? BuildFrame()
        {
            var frame = _built;
            _built = null;
            return frame;
        }
    }
}
