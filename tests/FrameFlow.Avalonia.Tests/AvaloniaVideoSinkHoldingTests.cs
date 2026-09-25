using Avalonia.Headless.XUnit;

namespace FrameFlow.Avalonia.Tests;

/// <summary>What the Avalonia sink declares it keeps (ADR-0081).</summary>
public sealed class AvaloniaVideoSinkHoldingTests
{
    [AvaloniaFact]
    public async Task MaxHeldFrames_IsTheFrameSlot()
    {
        await using var sink = new AvaloniaVideoSink();

        Assert.Equal(1, sink.MaxHeldFrames);
    }
}
