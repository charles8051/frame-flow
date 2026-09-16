using FrameFlow.Media;

namespace FrameFlow.Player.Tests;

/// <summary>
/// One configurator contract: it returns its chain open, and the builder terminates it at the
/// registered sink. Decision 4 of <c>docs/adr/ADR-0078-graph-chain-forks-joins-and-termination.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// The mode this replaces let a configurator wire its own terminal and register no sink. A
/// consumer that needs extra sinks now wires them on <c>Branch</c> edges inside the configurator
/// and returns its trunk open, which keeps every configured graph single-sink and therefore
/// paced by the sink decorator rather than by an in-graph operator.
/// </para>
/// <para>
/// The refusal happens at build, before FFmpeg is touched, which is what these assert: no
/// source is opened and no corpus file is needed.
/// </para>
/// </remarks>
public sealed class ConfiguratorContractTests
{
    [Fact]
    public async Task AVideoConfiguratorWithNoSink_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                FrameFlowPlayer
                    .Open("does-not-need-to-exist.mp4")
                    .ConfigureVideo(chain => chain)
                    .BuildPlayerAsync(CancellationToken.None)
        );

        Assert.Contains("ConfigureVideo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithVideoSink", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAudioConfiguratorWithNoSink_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                FrameFlowPlayer
                    .Open("does-not-need-to-exist.mp4")
                    .ConfigureAudio(chain => chain)
                    .BuildPlayerAsync(CancellationToken.None)
        );

        Assert.Contains("ConfigureAudio", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithAudioSink", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameRefusalAppliesToTheSessionTerminal()
    {
        // BuildAsync is the other terminal, and it took the same configurator.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                FrameFlowPlayer
                    .Open("does-not-need-to-exist.mp4")
                    .ConfigureVideo(chain => chain)
                    .BuildAsync(CancellationToken.None)
        );

        Assert.Contains("WithVideoSink", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConfiguratorWithItsSink_GetsPastTheContractCheck()
    {
        // It fails on the missing file instead, which is the point: the contract check is not
        // what stops it.
        var ex = await Record.ExceptionAsync(
            () =>
                FrameFlowPlayer
                    .Open("does-not-exist-either.mp4")
                    .WithVideoSink(new NullVideoSink())
                    .ConfigureVideo(chain => chain)
                    .BuildPlayerAsync(CancellationToken.None)
        );

        Assert.NotNull(ex);
        Assert.DoesNotContain("WithVideoSink", ex!.Message, StringComparison.Ordinal);
    }
}
