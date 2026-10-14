// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media.Core;
using FrameFlow.Media.Tests.Doubles;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Pins ADR-0085 decision 8 on <see cref="FanOutVideoSink"/>, apart from its claims: the targets
/// it accepts, how it delivers a frame and a format to each and who owns which reference, what it
/// throws, what it declares, its counts, and its disposal. <see cref="FanOutRules"/> holds the
/// rules as values and is tested as such; the rest drives the sink with recording sinks and a
/// frame that counts its references.
/// </summary>
public sealed class FanOutVideoSinkTests
{
    private static readonly VideoFormatInfo Format = new(4, 4, PixelFormat.Bgra32);

    // ─── Construction ───────────────────────────────────────────────

    [Fact]
    public void AnEmptyTargetList_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new FanOutVideoSink(Array.Empty<IVideoSink>()));
        Assert.Throws<ArgumentException>(() => new FanOutVideoSink(Array.Empty<FanOutTarget>()));
        Assert.Throws<ArgumentException>(() => new FanOutVideoSink(new List<FanOutTarget>()));
    }

    [Fact]
    public void ANullTarget_IsRefused()
    {
        var sink = new RecordingSink("a");

        Assert.Throws<ArgumentNullException>(() => new FanOutVideoSink((IVideoSink[])null!));
        Assert.Throws<ArgumentNullException>(() => new FanOutVideoSink((FanOutTarget[])null!));
        Assert.Throws<ArgumentException>(() => new FanOutVideoSink(sink, null!));
        Assert.Throws<ArgumentException>(() => new FanOutVideoSink(FanOutTarget.Of(sink), null!));
        Assert.Throws<ArgumentNullException>(() => FanOutTarget.Of(null!));
        Assert.Throws<ArgumentNullException>(() => FanOutTarget.Transform(null!, frame => frame.AddRef()));
        Assert.Throws<ArgumentNullException>(() => FanOutTarget.Transform(sink, null!));
    }

    [Fact]
    public void ARepeatedTarget_IsRefused()
    {
        var a = new RecordingSink("a");
        var b = new RecordingSink("b");

        Assert.Throws<ArgumentException>(() => new FanOutVideoSink(a, b, a));
        Assert.Throws<ArgumentException>(() =>
            new FanOutVideoSink(FanOutTarget.Of(a), FanOutTarget.Transform(a, frame => frame.AddRef()))
        );
    }

    [Fact]
    public void ATargetRepeatedThroughANestedFanOut_IsRefused()
    {
        var a = new RecordingSink("a");
        var b = new RecordingSink("b");
        var c = new RecordingSink("c");
        var inner = new FanOutVideoSink(a, b);

        // The same sink beside the fan-out it is already in, and in two fan-outs side by side.
        Assert.Throws<ArgumentException>(() => new FanOutVideoSink(inner, a));
        Assert.Throws<ArgumentException>(() => new FanOutVideoSink(inner, new FanOutVideoSink(c, b)));
        // The same nested fan-out twice, and one deeper down.
        Assert.Throws<ArgumentException>(() => new FanOutVideoSink(inner, inner));
        Assert.Throws<ArgumentException>(() =>
            new FanOutVideoSink(new FanOutVideoSink(new FanOutVideoSink(c), inner), c)
        );
        // A fan-out over the same sinks nests once.
        _ = new FanOutVideoSink(inner, c);
    }

    [Fact]
    public void TargetsWithNoDomainInCommon_AreRefused_NamingWhatEachTakes()
    {
        var cpu = new RecordingSink("cpu") { AcceptedDomains = FrameMemoryDomains.Cpu };
        var gpu = new RecordingSink("gpu") { AcceptedDomains = FrameMemoryDomains.D3D11 };

        var error = Assert.Throws<ArgumentException>(() => new FanOutVideoSink(cpu, gpu));

        Assert.Contains("no memory domain in common", error.Message, StringComparison.Ordinal);
        Assert.Contains("RecordingSink takes D3D11", error.Message, StringComparison.Ordinal);
        // A transform's domains stand in for its sink's.
        Assert.Throws<ArgumentException>(() =>
            new FanOutVideoSink(FanOutTarget.Of(gpu), FanOutTarget.Transform(cpu, frame => frame.AddRef()))
        );
    }

    // ─── Delivery ───────────────────────────────────────────────────

    [Fact]
    public async Task EachTarget_IsGivenItsOwnReference_InOrder_AndTheFanOutReleasesTheOneItWasGiven()
    {
        var log = new List<string>();
        var a = new RecordingSink("a", log) { Keeps = true };
        var b = new RecordingSink("b", log) { Keeps = true };
        var c = new RecordingSink("c", log) { Keeps = true };
        var fanOut = new FanOutVideoSink(a, b, c);
        var frame = new CountedFrame();
        using var cts = new CancellationTokenSource();

        await fanOut.PresentAsync(frame, cts.Token);

        Assert.Equal(["a", "b", "c"], log);
        Assert.All(new[] { a, b, c }, sink => Assert.Same(frame, Assert.Single(sink.Frames)));
        Assert.All(new[] { a, b, c }, sink => Assert.Equal(cts.Token, Assert.Single(sink.Tokens)));
        Assert.Equal(3, frame.References); // one per target; the fan-out's own is released

        foreach (var sink in new[] { a, b, c })
            sink.Frames[0].Dispose();
        Assert.Equal(0, frame.References);
        Assert.Equal(0, frame.OverReleases);
    }

    [Fact]
    public async Task ATargetThatThrows_LeaksNoReference_AndTheOthersStillGetTheFrame()
    {
        var log = new List<string>();
        var failure = new InvalidOperationException("the first target failed");
        var a = new RecordingSink("a", log) { PresentThrows = failure };
        var b = new RecordingSink("b", log);
        var fanOut = new FanOutVideoSink(a, b);
        var frame = new CountedFrame();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fanOut.PresentAsync(frame, CancellationToken.None)
        );

        Assert.Same(failure, thrown); // one failure is thrown as it is
        Assert.Equal(["a", "b"], log);
        Assert.Equal(0, frame.References);
        Assert.Equal(0, frame.OverReleases);
    }

    [Fact]
    public async Task SeveralTargetsThatThrow_AreThrownTogether_InTargetOrder()
    {
        var first = new InvalidOperationException("first");
        var second = new OperationCanceledException("second");
        var a = new RecordingSink("a") { PresentThrows = first };
        var b = new RecordingSink("b");
        var c = new RecordingSink("c") { PresentThrows = second };
        var fanOut = new FanOutVideoSink(a, b, c);
        var frame = new CountedFrame();

        var thrown = await Assert.ThrowsAsync<AggregateException>(async () =>
            await fanOut.PresentAsync(frame, CancellationToken.None)
        );

        Assert.Equal([first, second], thrown.InnerExceptions);
        Assert.Single(b.Frames);
        Assert.Equal(0, frame.References);
    }

    [Fact]
    public async Task ACancelledTarget_DoesNotStopTheOthers_AndTheCallerSeesACancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var a = new RecordingSink("a") { PresentThrows = new OperationCanceledException(cts.Token) };
        var b = new RecordingSink("b");
        var c = new RecordingSink("c") { PresentThrows = new OperationCanceledException(cts.Token) };
        var frame = new CountedFrame();

        // Cancelled at two targets of three: the one between them is still called.
        var fanOut = new FanOutVideoSink(a, b, c);
        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await fanOut.PresentAsync(frame, cts.Token)
        );

        Assert.Same(a.PresentThrows, thrown);
        Assert.Single(b.Frames);
        Assert.Single(c.Frames);
        Assert.Equal(0, frame.References);
    }

    [Fact]
    public async Task ATransformedTarget_IsGivenTheTransformsFrame_AndTheTransformBorrowsTheIncomingOne()
    {
        var plain = new RecordingSink("plain") { Keeps = true };
        var filtered = new RecordingSink("filtered") { Keeps = true };
        var made = new CountedFrame();
        var referencesDuringTransform = -1;
        var frame = new CountedFrame();
        var fanOut = new FanOutVideoSink(
            FanOutTarget.Of(plain),
            FanOutTarget.Transform(
                filtered,
                incoming =>
                {
                    Assert.Same(frame, incoming);
                    referencesDuringTransform = frame.References;
                    return made;
                }
            )
        );

        await fanOut.PresentAsync(frame, CancellationToken.None);

        Assert.Same(made, Assert.Single(filtered.Frames));
        Assert.Equal(1, made.References); // the target's
        Assert.Equal(2, referencesDuringTransform); // the fan-out's and the plain target's
        Assert.Equal(1, frame.References); // the plain target's
    }

    // A frame that differs from a 4x4 square Bgra32 one, unturned, in one thing a format carries.
    private static CountedFrame OfAnotherFormat(string what) =>
        what switch
        {
            "width" => new CountedFrame(width: 8),
            "height" => new CountedFrame(height: 8),
            "pixel format" => new CountedFrame(format: PixelFormat.Nv12),
            "pixel shape" => new CountedFrame(sampleAspectRatio: new SampleAspectRatio(4, 3)),
            "rotation" => new CountedFrame(rotation: VideoRotation.Clockwise90),
            _ => throw new ArgumentOutOfRangeException(nameof(what), what, null),
        };

    [Theory]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("pixel format")]
    [InlineData("pixel shape")]
    [InlineData("rotation")]
    public async Task ATransformThatChangesTheFormat_FailsItsTargetsCall_AndItsFrameIsDisposed(string what)
    {
        var made = OfAnotherFormat(what);
        var log = new List<string>();
        var filtered = new RecordingSink("filtered", log);
        var after = new RecordingSink("after", log);
        var fanOut = new FanOutVideoSink(
            FanOutTarget.Transform(filtered, _ => made),
            FanOutTarget.Of(after)
        );
        var frame = new CountedFrame();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fanOut.PresentAsync(frame, CancellationToken.None)
        );

        Assert.Contains("A transform keeps the frame's dimensions", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(["after"], log); // the filtered sink is not called; the next target is
        Assert.Equal(0, made.References);
        Assert.Equal(0, made.OverReleases);
        Assert.Equal(0, frame.References);
    }

    [Fact]
    public async Task ATargetThatFaultsAfterItYields_LeaksNoReference_AndTheOthersStillGetTheFrame()
    {
        var failure = new InvalidOperationException("the surface went away");
        var faulting = new FaultingLaterSink(failure);
        var after = new RecordingSink("after");
        var fanOut = new FanOutVideoSink(faulting, after);
        var frame = new CountedFrame();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fanOut.PresentAsync(frame, CancellationToken.None)
        );

        Assert.Same(failure, thrown);
        Assert.Single(after.Frames);
        Assert.Equal(0, frame.References);
        Assert.Equal(0, frame.OverReleases);
    }

    [Fact]
    public async Task ATransformThatReturnsNothing_FailsItsTargetsCall()
    {
        var filtered = new RecordingSink("filtered");
        var after = new RecordingSink("after");
        var fanOut = new FanOutVideoSink(
            FanOutTarget.Transform(filtered, _ => null!),
            FanOutTarget.Of(after)
        );
        var frame = new CountedFrame();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fanOut.PresentAsync(frame, CancellationToken.None)
        );

        Assert.Contains("returned no frame", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(filtered.Frames);
        Assert.Single(after.Frames);
        Assert.Equal(0, frame.References);
    }

    [Theory]
    [InlineData(FrameMemoryDomain.Gpu, FrameMemoryDomains.Cpu)]
    [InlineData(FrameMemoryDomain.Cpu, FrameMemoryDomains.D3D11)]
    public async Task ATransformWhoseFrameIsInMemoryItsSinkDoesNotTake_FailsItsTargetsCall_AndItsFrameIsDisposed(
        FrameMemoryDomain made,
        FrameMemoryDomains sinkTakes
    )
    {
        var log = new List<string>();
        var filtered = new RecordingSink("filtered", log) { AcceptedDomains = sinkTakes };
        var after = new RecordingSink("after", log);
        var output = new CountedFrame(memoryDomain: made);
        var fanOut = new FanOutVideoSink(
            FanOutTarget.Transform(filtered, _ => output),
            FanOutTarget.Of(after)
        );
        var frame = new CountedFrame();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fanOut.PresentAsync(frame, CancellationToken.None)
        );

        Assert.Contains($"made a frame in {made} memory", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(["after"], log);
        Assert.Equal(0, output.References);
        Assert.Equal(0, output.OverReleases);
        Assert.Equal(0, frame.References);
    }

    [Fact]
    public async Task ATransformsGpuFrame_ReachesASinkThatTakesAGpuDomain()
    {
        var zeroCopy = new RecordingSink("zero-copy")
        {
            AcceptedDomains = FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D11,
        };
        var output = new CountedFrame(memoryDomain: FrameMemoryDomain.Gpu);
        var fanOut = new FanOutVideoSink(FanOutTarget.Transform(zeroCopy, _ => output));

        await fanOut.PresentAsync(new CountedFrame(), CancellationToken.None);

        Assert.Same(output, Assert.Single(zeroCopy.Frames));
    }

    [Fact]
    public async Task ATransformThatThrows_FailsOnlyItsTarget()
    {
        var failure = new NotSupportedException("the filter reads Bgra32");
        var filtered = new RecordingSink("filtered");
        var after = new RecordingSink("after");
        var fanOut = new FanOutVideoSink(
            FanOutTarget.Transform(filtered, _ => throw failure),
            FanOutTarget.Of(after)
        );
        var frame = new CountedFrame();

        var thrown = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await fanOut.PresentAsync(frame, CancellationToken.None)
        );

        Assert.Same(failure, thrown);
        Assert.Empty(filtered.Frames);
        Assert.Single(after.Frames);
        Assert.Equal(0, frame.References);
    }

    // ─── Formats ────────────────────────────────────────────────────

    [Fact]
    public async Task AFormatChange_ReachesEveryTarget_EvenWhenOneThrows()
    {
        var log = new List<string>();
        var failure = new InvalidOperationException("the surface could not be rebuilt");
        var a = new RecordingSink("a", log) { FormatThrows = failure };
        var b = new RecordingSink("b", log);
        var c = new RecordingSink("c", log);
        var fanOut = new FanOutVideoSink(
            FanOutTarget.Of(a),
            FanOutTarget.Transform(b, frame => frame.AddRef()),
            FanOutTarget.Of(c)
        );

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fanOut.OnFormatChangedAsync(Format, CancellationToken.None)
        );

        Assert.Same(failure, thrown);
        Assert.Equal(["a", "b", "c"], log);
        Assert.All(new[] { a, b, c }, sink => Assert.Same(Format, Assert.Single(sink.Formats)));
    }

    // ─── What it declares ───────────────────────────────────────────

    [Fact]
    public void MaxHeldFrames_IsTheSumOfTheTargets_OrNullWhenAnyIsUnbounded()
    {
        var two = new RecordingSink("two") { MaxHeldFrames = 2 };
        var three = new RecordingSink("three") { MaxHeldFrames = 3 };
        var unbounded = new RecordingSink("unbounded") { MaxHeldFrames = null };

        Assert.Equal(5, new FanOutVideoSink(two, three).MaxHeldFrames);
        Assert.Null(new FanOutVideoSink(two, unbounded, three).MaxHeldFrames);
        Assert.Equal(5, FanOutRules.MaxHeldFrames([2, 3]));
        Assert.Null(FanOutRules.MaxHeldFrames([2, null]));
        // A sum past int.MaxValue reads as unbounded rather than wrapping to a negative count.
        Assert.Null(FanOutRules.MaxHeldFrames([int.MaxValue, 1]));
        Assert.Equal(int.MaxValue, FanOutRules.MaxHeldFrames([int.MaxValue - 1, 1]));
    }

    [Fact]
    public void AcceptedDomains_IsWhatEveryTargetTakes_ATransformsInPlaceOfItsSinks()
    {
        var zeroCopy = new RecordingSink("zero-copy")
        {
            AcceptedDomains = FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D11 | FrameMemoryDomains.D3D12,
        };
        var d3d11 = new RecordingSink("d3d11")
        {
            AcceptedDomains = FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D11,
        };
        var anything = new RecordingSink("anything") { AcceptedDomains = FrameMemoryDomains.Any };

        Assert.Equal(
            FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D11,
            new FanOutVideoSink(zeroCopy, d3d11, new RecordingSink("all") { AcceptedDomains = FrameMemoryDomains.Any }).AcceptedDomains
        );
        // A transform takes CPU frames unless it says otherwise, whatever its sink takes.
        Assert.Equal(
            FrameMemoryDomains.Cpu,
            new FanOutVideoSink(FanOutTarget.Of(zeroCopy), FanOutTarget.Transform(anything, frame => frame.AddRef())).AcceptedDomains
        );
        Assert.Equal(
            FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D12,
            new FanOutVideoSink(
                FanOutTarget.Of(zeroCopy),
                FanOutTarget.Transform(
                    new RecordingSink("cpu"),
                    frame => frame.AddRef(),
                    FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D12
                )
            ).AcceptedDomains
        );
    }

    [Fact]
    public void ANarrowedTarget_IsLoggedOnce_WhenTheFanOutIsMade()
    {
        var zeroCopy = new RecordingSink("zero-copy")
        {
            AcceptedDomains = FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D11,
        };
        var cpu = new RecordingSink("cpu") { AcceptedDomains = FrameMemoryDomains.Cpu };
        var logger = new ListLogger();

        var fanOut = new FanOutVideoSink([FanOutTarget.Of(zeroCopy), FanOutTarget.Of(cpu)], logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("takes Cpu frames", entry.Message, StringComparison.Ordinal);
        Assert.Contains("RecordingSink takes Cpu, D3D11", entry.Message, StringComparison.Ordinal);
        Assert.Equal([0], FanOutRules.Narrowed([zeroCopy.AcceptedDomains, cpu.AcceptedDomains], fanOut.AcceptedDomains));

        // Nothing narrowed, nothing logged.
        var quiet = new ListLogger();
        _ = new FanOutVideoSink([FanOutTarget.Of(cpu), FanOutTarget.Of(new RecordingSink("cpu too"))], quiet);
        Assert.Empty(quiet.Entries);
    }

    [Fact]
    public async Task ANarrowedTarget_IsNotLoggedAgain_PerFrame()
    {
        var zeroCopy = new RecordingSink("zero-copy")
        {
            AcceptedDomains = FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D11,
        };
        var logger = new ListLogger();
        var fanOut = new FanOutVideoSink([FanOutTarget.Of(zeroCopy), FanOutTarget.Of(new RecordingSink("cpu"))], logger);

        await fanOut.OnFormatChangedAsync(Format, CancellationToken.None);
        await fanOut.PresentAsync(new CountedFrame(), CancellationToken.None);
        await fanOut.PresentAsync(new CountedFrame(), CancellationToken.None);

        Assert.Single(logger.Entries);
    }

    [Fact]
    public void PreferredBackends_IsTheFirstListAPlainTargetNames()
    {
        const FrameMemoryDomains gpus = FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D12 | FrameMemoryDomains.Cuda;
        var none = new RecordingSink("none") { AcceptedDomains = gpus };
        var d3d12 = new RecordingSink("d3d12")
        {
            AcceptedDomains = gpus,
            PreferredBackends = [HardwareDecodeBackendKind.D3D12Va],
        };
        var cuda = new RecordingSink("cuda")
        {
            AcceptedDomains = gpus,
            PreferredBackends = [HardwareDecodeBackendKind.Cuda],
        };

        Assert.Equal([HardwareDecodeBackendKind.D3D12Va], new FanOutVideoSink(none, d3d12, cuda).PreferredBackends);
        Assert.Empty(new FanOutVideoSink(none, new RecordingSink("none too") { AcceptedDomains = gpus }).PreferredBackends);
        // A transformed target's sink is given the transform's frames, not the decoder's.
        Assert.Equal(
            [HardwareDecodeBackendKind.D3D12Va],
            new FanOutVideoSink(
                FanOutTarget.Transform(cuda, frame => frame.AddRef(), gpus),
                FanOutTarget.Of(d3d12)
            ).PreferredBackends
        );
    }

    [Fact]
    public void PreferredBackends_KeepsOnlyTheBackendsWhoseFramesTheFanOutTakes()
    {
        var cudaFirst = new RecordingSink("cuda first")
        {
            AcceptedDomains = FrameMemoryDomains.Any,
            PreferredBackends = [HardwareDecodeBackendKind.Cuda],
        };
        var zeroCopy = new RecordingSink("zero-copy")
        {
            AcceptedDomains = FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D11,
            PreferredBackends = [HardwareDecodeBackendKind.D3D12Va, HardwareDecodeBackendKind.D3D11Va],
        };

        // The fan-out takes CPU and D3D11 frames: the first list names no backend it takes, and
        // the second keeps D3D11VA only.
        Assert.Equal([HardwareDecodeBackendKind.D3D11Va], new FanOutVideoSink(cudaFirst, zeroCopy).PreferredBackends);
        Assert.Equal(
            [HardwareDecodeBackendKind.D3D11Va],
            FanOutRules.PreferredBackends(
                [[HardwareDecodeBackendKind.Cuda], [HardwareDecodeBackendKind.D3D12Va, HardwareDecodeBackendKind.D3D11Va]],
                FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D11
            )
        );
    }

    // ─── Counts ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetDiagnostics_CountsTheFramesEveryTargetTook()
    {
        var failing = new RecordingSink("failing") { PresentThrows = new InvalidOperationException() };
        var fanOut = new FanOutVideoSink(new RecordingSink("a"), new RecordingSink("b"));
        var failingFanOut = new FanOutVideoSink(new RecordingSink("c"), failing);

        await fanOut.PresentAsync(new CountedFrame { Pts = TimeSpan.FromSeconds(1) }, CancellationToken.None);
        await fanOut.PresentAsync(new CountedFrame { Pts = TimeSpan.FromSeconds(2) }, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await failingFanOut.PresentAsync(new CountedFrame(), CancellationToken.None)
        );
        await fanOut.DisposeAsync();
        await fanOut.PresentAsync(new CountedFrame { Pts = TimeSpan.FromSeconds(3) }, CancellationToken.None);

        var counts = fanOut.GetDiagnostics();
        Assert.Equal(2, counts.FramesPresented);
        Assert.Equal(0, counts.FramesDropped);
        Assert.Equal(TimeSpan.FromSeconds(2), counts.LastPresentedPresentationTime);
        Assert.Equal(0, failingFanOut.GetDiagnostics().FramesPresented);
    }

    // ─── Disposal ───────────────────────────────────────────────────

    [Fact]
    public async Task Disposing_DisposesNoneOfTheTargets()
    {
        var a = new RecordingSink("a");
        var b = new RecordingSink("b");
        var fanOut = new FanOutVideoSink(a, new FanOutVideoSink(b));

        await fanOut.DisposeAsync();

        Assert.False(a.Disposed);
        Assert.False(b.Disposed);
    }

    [Fact]
    public async Task OnceDisposed_AFrameIsDisposed_AndNoTargetIsCalled()
    {
        var a = new RecordingSink("a");
        var b = new RecordingSink("b");
        var fanOut = new FanOutVideoSink(FanOutTarget.Of(a), FanOutTarget.Transform(b, frame => frame.AddRef()));
        var frame = new CountedFrame();

        await fanOut.DisposeAsync();
        await fanOut.DisposeAsync(); // a second dispose does nothing
        await fanOut.PresentAsync(frame, CancellationToken.None);
        await fanOut.OnFormatChangedAsync(Format, CancellationToken.None);

        Assert.Equal(0, frame.References);
        Assert.Equal(0, frame.OverReleases);
        Assert.Empty(a.Frames);
        Assert.Empty(b.Frames);
        Assert.Empty(a.Formats);
        Assert.Empty(b.Formats);
    }

    // ─── The rules as values ────────────────────────────────────────

    [Fact]
    public void Failure_IsNothing_TheOne_TheFirstOfOnlyCancellations_OrAllTogether()
    {
        var one = new InvalidOperationException("one");
        var cancelled = new OperationCanceledException("cancelled");
        var cancelledToo = new TaskCanceledException("cancelled too");

        Assert.Null(FanOutRules.Failure([]));
        Assert.Same(one, FanOutRules.Failure([one]));
        Assert.Same(cancelled, FanOutRules.Failure([cancelled, cancelledToo]));
        var all = Assert.IsType<AggregateException>(FanOutRules.Failure([cancelled, one]));
        Assert.Equal([cancelled, one], all.InnerExceptions);
    }

    [Fact]
    public void KeepsFormat_ComparesEverythingAFormatCarries()
    {
        var frame = new CountedFrame();

        Assert.True(FanOutRules.KeepsFormat(frame, new CountedFrame()));
        foreach (var what in new[] { "width", "height", "pixel format", "pixel shape", "rotation" })
            Assert.False(FanOutRules.KeepsFormat(frame, OfAnotherFormat(what)), what);
    }

    [Fact]
    public void Takes_ACpuFrameWhereCpuIsTaken_AndAGpuFrameWhereAnyGpuDomainIs()
    {
        Assert.True(FanOutRules.Takes(FrameMemoryDomains.Cpu, FrameMemoryDomain.Cpu));
        Assert.False(FanOutRules.Takes(FrameMemoryDomains.D3D11, FrameMemoryDomain.Cpu));
        Assert.True(FanOutRules.Takes(FrameMemoryDomains.Cpu | FrameMemoryDomains.Cuda, FrameMemoryDomain.Gpu));
        Assert.False(FanOutRules.Takes(FrameMemoryDomains.Cpu, FrameMemoryDomain.Gpu));
    }

    /// <summary>A sink that yields before it disposes its frame, as its owner, and throws.</summary>
    private sealed class FaultingLaterSink(Exception failure) : IVideoSink
    {
        public async ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            await Task.Yield();
            frame.Dispose();
            throw failure;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) => default;

        public ValueTask DisposeAsync() => default;
    }

    /// <summary>A logger that keeps what it is given.</summary>
    private sealed class ListLogger : ILogger<FanOutVideoSink>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
