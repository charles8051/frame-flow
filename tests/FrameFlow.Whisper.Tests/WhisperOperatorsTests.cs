namespace FrameFlow.Whisper.Tests;

/// <summary>
/// Factory + argument-validation pinning tests for
/// <see cref="WhisperOperators.TranscribeWithWhisper"/>. Inference itself
/// requires a Whisper ggml model file (~150 MB+); end-to-end coverage
/// lives in the integration tests (deferred to Phase 4's test
/// migration pass — same shape as the other *NextTests.cs siblings).
/// These tests pin the build-time contract: arg validation, lazy
/// model load, returned-node identity.
/// </summary>
public sealed class WhisperOperatorsTests
{
    private const string ArbitraryModelPath = "no-such-model.bin";

    // ─── Argument validation ─────────────────────────────────────────

    [Fact]
    public void TranscribeWithWhisper_NullId_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => WhisperOperators.TranscribeWithWhisper(null!, ArbitraryModelPath)
        );

    [Fact]
    public void TranscribeWithWhisper_NullModelPath_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => WhisperOperators.TranscribeWithWhisper("tx", null!)
        );

    [Fact]
    public void TranscribeWithWhisper_EmptyModelPath_Throws() =>
        Assert.Throws<ArgumentException>(
            () => WhisperOperators.TranscribeWithWhisper("tx", string.Empty)
        );

    [Fact]
    public void TranscribeWithWhisper_WhitespaceModelPath_Throws() =>
        Assert.Throws<ArgumentException>(
            () => WhisperOperators.TranscribeWithWhisper("tx", "   ")
        );

    [Fact]
    public void TranscribeWithWhisper_ZeroSampleRate_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                WhisperOperators.TranscribeWithWhisper(
                    "tx",
                    ArbitraryModelPath,
                    new WhisperOptions(InputSampleRate: 0)
                )
        );

    [Fact]
    public void TranscribeWithWhisper_NegativeSampleRate_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                WhisperOperators.TranscribeWithWhisper(
                    "tx",
                    ArbitraryModelPath,
                    new WhisperOptions(InputSampleRate: -1)
                )
        );

    [Fact]
    public void TranscribeWithWhisper_ZeroChannels_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                WhisperOperators.TranscribeWithWhisper(
                    "tx",
                    ArbitraryModelPath,
                    new WhisperOptions(InputChannels: 0)
                )
        );

    // ─── Factory behaviour ───────────────────────────────────────────

    [Fact]
    public void TranscribeWithWhisper_BuildsNode_WithoutTouchingModelFile()
    {
        // The model path doesn't exist — but construction must not throw.
        // Lazy load happens only when the first PcmAudioBufferRef arrives
        // through the substrate's pump. This pins the "construction is
        // cheap, model load is per-graph-run" contract that the old
        // version established.
        var node = WhisperOperators.TranscribeWithWhisper("tx", ArbitraryModelPath);

        Assert.NotNull(node);
        Assert.Equal("tx", node.Id);
    }

    [Fact]
    public void TranscribeWithWhisper_DefaultOptions_BuildsNode()
    {
        var node = WhisperOperators.TranscribeWithWhisper("tx", ArbitraryModelPath);
        Assert.NotNull(node.Body);
        Assert.NotNull(node.Input);
        Assert.NotNull(node.Output);
    }

    [Fact]
    public void TranscribeWithWhisper_CustomOptions_BuildsNode()
    {
        var node = WhisperOperators.TranscribeWithWhisper(
            "tx",
            ArbitraryModelPath,
            new WhisperOptions(
                Language: "fr",
                WindowSize: TimeSpan.FromSeconds(2.5),
                InputSampleRate: 16_000,
                InputChannels: 1
            )
        );

        Assert.NotNull(node);
        Assert.Equal("tx", node.Id);
    }
}
