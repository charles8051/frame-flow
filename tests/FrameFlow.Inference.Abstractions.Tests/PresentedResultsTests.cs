using FrameFlow.Inference.Core;
using FrameFlow.Media;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// Lining results up with the frame on screen: the latest result at or before the presented
/// timestamp, one event per change, and a timeline that goes back dropping what was waiting.
/// The presenter is a fake that raises <see cref="IFramePresentedSource.FramePresented"/> inline.
/// </summary>
public sealed class PresentedResultsTests
{
    [Theory]
    [InlineData(-1, -1)]
    [InlineData(0, 0)]
    [InlineData(39, 0)]
    [InlineData(40, 1)]
    [InlineData(1000, 2)]
    public void TheMatch_IsTheLatestAtOrBeforeThePresentedFrame(int presentedMs, int expected) =>
        Assert.Equal(
            expected,
            PresentedMatch.LatestAtOrBefore([Ms(0), Ms(40), Ms(80)], Ms(presentedMs)));

    [Fact]
    public void AFrameWithoutAResultOfItsOwn_ShowsTheOneBefore()
    {
        var presenter = new FakePresenter();
        using var onScreen = new PresentedResults<string>(presenter);
        onScreen.Post(Result("a", 0));
        onScreen.Post(Result("b", 80));

        presenter.Present(40);

        Assert.Equal("a", onScreen.Current?.Result);
    }

    [Fact]
    public void Presented_IsRaisedOncePerChange()
    {
        var presenter = new FakePresenter();
        using var onScreen = new PresentedResults<string>(presenter);
        var seen = new List<string>();
        onScreen.Presented += (_, result) => seen.Add(result.Result);
        onScreen.Post(Result("a", 0));
        onScreen.Post(Result("b", 80));

        presenter.Present(0);
        presenter.Present(40);
        presenter.Present(80);
        presenter.Present(120);

        Assert.Equal(["a", "b"], seen);
    }

    [Fact]
    public void AFrameBeforeEveryResult_ShowsNothing()
    {
        var presenter = new FakePresenter();
        using var onScreen = new PresentedResults<string>(presenter);
        onScreen.Post(Result("a", 80));

        presenter.Present(40);

        Assert.Null(onScreen.Current);
    }

    [Fact]
    public void AResultEarlierThanTheLast_DropsWhatWasWaiting()
    {
        var presenter = new FakePresenter();
        using var onScreen = new PresentedResults<string>(presenter);
        onScreen.Post(Result("first pass", 0));
        onScreen.Post(Result("first pass, late", 120));

        // A loop: the timeline starts again. At 100 ms the latest result at or before is the new
        // pass's, not the one the pass before left at 0 ms.
        onScreen.Post(Result("second pass", 0));
        presenter.Present(100);

        Assert.Equal("second pass", onScreen.Current?.Result);
    }

    [Fact]
    public void APictureThatGoesBackPastTheResultShowing_ClearsIt()
    {
        var presenter = new FakePresenter();
        using var onScreen = new PresentedResults<string>(presenter);
        int cleared = 0;
        onScreen.Cleared += (_, _) => cleared++;
        onScreen.Post(Result("a", 0));
        onScreen.Post(Result("b", 80));
        presenter.Present(80);

        // A seek back to 20 ms: "b" is from later in the stream, and nothing is waiting for 20 ms.
        presenter.Present(20);

        Assert.Null(onScreen.Current);
        Assert.Equal(1, cleared);

        // The branch catches up with the new position.
        onScreen.Post(Result("c", 40));
        presenter.Present(40);
        Assert.Equal("c", onScreen.Current?.Result);
        Assert.Equal(1, cleared);
    }

    [Fact]
    public void AClearedResult_DoesNotShowAgainWhenThePictureReturnsToIt()
    {
        var presenter = new FakePresenter();
        using var onScreen = new PresentedResults<string>(presenter);
        onScreen.Post(Result("a", 0));
        onScreen.Post(Result("b", 80));
        presenter.Present(80);
        presenter.Present(20);

        // No result has arrived for the new position; "b" belongs to the timeline that was cleared.
        presenter.Present(80);

        Assert.Null(onScreen.Current);
    }

    [Fact]
    public void AClear_KeepsWhatTheBranchPostedForTheNewPosition()
    {
        var presenter = new FakePresenter();
        using var onScreen = new PresentedResults<string>(presenter);
        onScreen.Post(Result("b", 80));
        presenter.Present(80);

        // The branch runs ahead: its first result after a seek back arrives before the picture does.
        onScreen.Post(Result("after the seek", 40));
        presenter.Present(20);
        Assert.Null(onScreen.Current);

        presenter.Present(40);
        Assert.Equal("after the seek", onScreen.Current?.Result);
    }

    [Fact]
    public void AFrameBeforeEveryResultWithNothingShowing_ClearsNothing()
    {
        var presenter = new FakePresenter();
        using var onScreen = new PresentedResults<string>(presenter);
        int cleared = 0;
        onScreen.Cleared += (_, _) => cleared++;
        onScreen.Post(Result("a", 80));

        presenter.Present(40);

        Assert.Equal(0, cleared);
    }

    [Fact]
    public void BeyondCapacity_TheOldestResultGoesFirst()
    {
        var presenter = new FakePresenter();
        using var onScreen = new PresentedResults<string>(presenter, capacity: 2);
        onScreen.Post(Result("a", 0));
        onScreen.Post(Result("b", 40));
        onScreen.Post(Result("c", 80));

        presenter.Present(20);

        Assert.Null(onScreen.Current);
    }

    [Fact]
    public void Dispose_StopsFollowingThePresenter()
    {
        var presenter = new FakePresenter();
        var onScreen = new PresentedResults<string>(presenter);
        onScreen.Post(Result("a", 0));

        onScreen.Dispose();
        presenter.Present(0);

        Assert.Null(onScreen.Current);
        Assert.False(presenter.HasHandlers);
    }

    private static InferenceResult<string> Result(string value, int ms) =>
        new(value, Ms(ms), 4, 4, InferencePath.Host);

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private sealed class FakePresenter : IFramePresentedSource
    {
        public event EventHandler<FramePresentedInfo>? FramePresented;

        public bool HasHandlers => FramePresented is not null;

        public void Present(int ms) => FramePresented?.Invoke(this, new FramePresentedInfo(Ms(ms), PresentedAtUtc: default));
    }
}
