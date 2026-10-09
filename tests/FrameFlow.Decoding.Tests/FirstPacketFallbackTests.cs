using FrameFlow.Decoding.Core;
using FrameFlow.Decoding.Internal;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// When a hardware decoder that refused the first packet is replaced by the software decoder
/// (#572). Pure: the table is every send result, with and without a fallback, flushing or not.
/// </summary>
/// <remarks>
/// Theory parameters are typed as object because xUnit test methods must be public, and a public
/// signature cannot expose the internal <see cref="CodecReturn"/> and
/// <see cref="FirstPacketAction"/> (CS0051), as in <c>DecodeProtocolTests</c>.
/// </remarks>
public sealed class FirstPacketFallbackTests
{
    [Theory]
    [InlineData(CodecReturn.Fault, FirstPacketAction.ReopenOnSoftware)]
    [InlineData(CodecReturn.Ok, FirstPacketAction.ReleaseFallback)]
    [InlineData(CodecReturn.Again, FirstPacketAction.Continue)]
    [InlineData(CodecReturn.EndOfStream, FirstPacketAction.Continue)]
    public void WithAFallbackPreparedTheSendResultDecidesIt(object send, object expected) =>
        Assert.Equal(
            (FirstPacketAction)expected,
            FirstPacketFallback.After(flushing: false, hasPreparedFallback: true, (CodecReturn)send)
        );

    [Theory]
    [InlineData(CodecReturn.Ok)]
    [InlineData(CodecReturn.Again)]
    [InlineData(CodecReturn.EndOfStream)]
    [InlineData(CodecReturn.Fault)]
    public void WithNoFallbackNothingHappensWhateverWasSent(object send) =>
        Assert.Equal(
            FirstPacketAction.Continue,
            FirstPacketFallback.After(flushing: false, hasPreparedFallback: false, (CodecReturn)send)
        );

    [Theory]
    [InlineData(CodecReturn.Ok)]
    [InlineData(CodecReturn.Again)]
    [InlineData(CodecReturn.EndOfStream)]
    [InlineData(CodecReturn.Fault)]
    public void AFlushIsNotAnInputSoItNeverTouchesTheFallback(object send) =>
        Assert.Equal(
            FirstPacketAction.Continue,
            FirstPacketFallback.After(flushing: true, hasPreparedFallback: true, (CodecReturn)send)
        );

    [Fact]
    public void ARefusalAfterTheFirstAcceptedPacketIsAFault()
    {
        // The first accepted packet releases the fallback, so the next refusal finds none.
        var first = FirstPacketFallback.After(flushing: false, hasPreparedFallback: true, CodecReturn.Ok);
        Assert.Equal(FirstPacketAction.ReleaseFallback, first);

        var later = FirstPacketFallback.After(flushing: false, hasPreparedFallback: false, CodecReturn.Fault);
        Assert.Equal(FirstPacketAction.Continue, later);
    }

    [Fact]
    public void EverySendResultHasAnAnswerForEveryState()
    {
        foreach (var send in Enum.GetValues<CodecReturn>())
        {
            foreach (bool flushing in new[] { false, true })
            {
                foreach (bool prepared in new[] { false, true })
                    Assert.True(Enum.IsDefined(FirstPacketFallback.After(flushing, prepared, send)));
            }
        }
    }
}
