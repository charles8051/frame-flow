using FrameFlow.Avalonia.Core;
using FrameFlow.Media;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins #631's mapping between the repeat mode and the transport bar's Loop toggle: what it shows,
/// what a click asks for, and what <c>LoopByDefault</c> asks a newly bound transport for.
/// </summary>
/// <remarks>
/// Values in, values out. <see cref="FrameFlowTransportBarLoopToggleTests"/> drives the same rules
/// through the bar.
/// </remarks>
public sealed class LoopToggleTests
{
    [Theory]
    [InlineData(RepeatMode.Off, false, "Loop")]
    [InlineData(RepeatMode.One, true, "Loop one")]
    [InlineData(RepeatMode.All, true, "Loop all")]
    public void Face_ShowsEachMode(RepeatMode mode, bool isChecked, string label) =>
        Assert.Equal(new LoopToggleFace(isChecked, label), LoopToggle.Face(mode));

    [Fact]
    public void Face_TellsTheTwoRepeatingModesApart() =>
        Assert.NotEqual(LoopToggle.Face(RepeatMode.One), LoopToggle.Face(RepeatMode.All));

    [Fact]
    public void Face_ShowsAValueOutsideTheEnum_AsOff() =>
        Assert.Equal(LoopToggle.Face(RepeatMode.Off), LoopToggle.Face((RepeatMode)42));

    [Theory]
    [InlineData(RepeatMode.Off, RepeatMode.One)]
    [InlineData(RepeatMode.One, RepeatMode.All)]
    [InlineData(RepeatMode.All, RepeatMode.Off)]
    [InlineData((RepeatMode)42, RepeatMode.Off)]
    public void Next_StepsOffOneAllOff(RepeatMode mode, RepeatMode next) =>
        Assert.Equal(next, LoopToggle.Next(mode));

    [Fact]
    public void Next_VisitsEveryMode_AndComesBack()
    {
        var visited = new List<RepeatMode> { RepeatMode.Off };
        for (var i = 0; i < 3; i++)
            visited.Add(LoopToggle.Next(visited[^1]));

        Assert.Equal(
            new[] { RepeatMode.Off, RepeatMode.One, RepeatMode.All, RepeatMode.Off },
            visited
        );
    }

    [Theory]
    [InlineData(RepeatMode.Off, false, null)]
    [InlineData(RepeatMode.One, false, null)]
    [InlineData(RepeatMode.All, false, null)]
    [InlineData(RepeatMode.Off, true, RepeatMode.One)]
    [InlineData(RepeatMode.One, true, null)]
    [InlineData(RepeatMode.All, true, null)]
    public void OnBind_AsksForOne_OnlyOfATransportThatIsNotRepeating(
        RepeatMode mode,
        bool loopByDefault,
        RepeatMode? requested
    ) => Assert.Equal(requested, LoopToggle.OnBind(mode, loopByDefault));
}
