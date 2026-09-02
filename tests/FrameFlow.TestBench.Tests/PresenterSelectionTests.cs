using FrameFlow.TestBench;
using Xunit;

namespace FrameFlow.TestBench.Tests;

/// <summary>
/// Tests for which presenter the bench builds, and for reporting it honestly.
/// </summary>
/// <remarks>
/// The rule is small and the reason it matters is not: <c>--presenter gpu</c> falls back
/// to the CPU surface off Windows and the flag still reads <c>gpu</c>. A bench that
/// reported the request would produce a transcript claiming a pipeline the run did not
/// measure. Keeping both values, and printing the resolved one, is what is left of the
/// deleted grammar's <c>require</c> guarantee.
/// </remarks>
public sealed class PresenterSelectionTests
{
    // The expected kind is named rather than passed: PresenterKind is internal, and an
    // InlineData carrying it would make the test method's signature less accessible
    // than the method itself.
    [Theory]
    [InlineData("headless", nameof(PresenterKind.Headless))]
    [InlineData("none", nameof(PresenterKind.Headless))]
    [InlineData("cpu", nameof(PresenterKind.Cpu))]
    [InlineData("gpu", nameof(PresenterKind.Gpu))]
    [InlineData("GPU", nameof(PresenterKind.Gpu))]
    [InlineData("  gpu  ", nameof(PresenterKind.Gpu))]
    public void KindsParse(string text, string expected) =>
        Assert.Equal(expected, PresenterSelection.ParseKind(text)?.ToString());

    [Theory]
    [InlineData("vulkan")]
    [InlineData("")]
    [InlineData("gpu2")]
    public void UnknownKindsDoNotParse(string text) =>
        Assert.Null(PresenterSelection.ParseKind(text));

    [Fact]
    public void HeadlessAndCpuResolveToThemselvesEverywhere()
    {
        foreach (var kind in new[] { PresenterKind.Headless, PresenterKind.Cpu })
        {
            var selection = PresenterSelection.Resolve(kind);
            Assert.Equal(kind, selection.Resolved);
            Assert.False(selection.FellBack);
            Assert.Null(selection.Reason);
        }
    }

    [Fact]
    public void GpuResolvesToItselfOnWindowsAndFallsBackElsewhere()
    {
        var selection = PresenterSelection.Resolve(PresenterKind.Gpu);

        // One assertion per platform rather than a skip: the fallback is the behaviour
        // under test, and it is only observable on the platforms that take it.
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(PresenterKind.Gpu, selection.Resolved);
            Assert.False(selection.FellBack);
            Assert.Null(selection.Reason);
        }
        else
        {
            Assert.Equal(PresenterKind.Cpu, selection.Resolved);
            Assert.True(selection.FellBack);
            Assert.Contains("Windows-only", selection.Reason);
        }
    }

    [Fact]
    public void TheRequestIsKeptAlongsideTheOutcome()
    {
        // Both halves, always. Discarding the request would lose the fact that a
        // fallback happened; discarding the outcome is the bug the whole type exists
        // to prevent.
        var selection = PresenterSelection.Resolve(PresenterKind.Gpu);
        Assert.Equal(PresenterKind.Gpu, selection.Requested);
    }

    [Fact]
    public void OnlyWindowedPresentersNeedAWindow()
    {
        Assert.False(PresenterSelection.Resolve(PresenterKind.Headless).NeedsWindow);
        Assert.True(PresenterSelection.Resolve(PresenterKind.Cpu).NeedsWindow);

        // Gpu resolves to Cpu off Windows, which still needs a window.
        Assert.True(PresenterSelection.Resolve(PresenterKind.Gpu).NeedsWindow);
    }

    [Fact]
    public void AFallbackSaysSoInItsOwnDescription()
    {
        var fellBack = new PresenterSelection(PresenterKind.Gpu, PresenterKind.Cpu, "no D3D");
        var text = DiagnosticsRenderer.Presenter(fellBack);

        Assert.Contains("cpu", text);
        Assert.Contains("requested gpu", text);
        Assert.Contains("no D3D", text);
    }

    [Fact]
    public void AResolvedPresenterDoesNotMentionARequest()
    {
        // The common case stays a single word. A line that always said "requested X"
        // would train a reader to skip it, which is the one thing it must not do.
        var text = DiagnosticsRenderer.Presenter(PresenterSelection.Resolve(PresenterKind.Cpu));

        Assert.Equal("presenter cpu", text);
        Assert.DoesNotContain("requested", text);
    }
}
