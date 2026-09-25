using Avalonia.Controls;
using FrameFlow.Graph;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// The declaration type is reachable by its plain name inside an Avalonia control, where a
/// window's code-behind builds its configurators. It used to be <c>Holding</c>, which a control
/// resolves to its <c>InputElement.Holding</c> event (#409). This file compiling is the test.
/// </summary>
public sealed class FrameHoldingNameTests
{
    [Fact]
    public void FrameHolding_ResolvesInsideAControl()
    {
        Assert.Same(FrameHolding.InFlight, InAControl.Declared);
    }

    private sealed class InAControl : Control
    {
        public static FrameHolding Declared => FrameHolding.InFlight;
    }
}
