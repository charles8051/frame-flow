// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media.Tests.Doubles;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Pins the claim side of ADR-0085 decision 8: a claim on a <see cref="FanOutVideoSink"/> takes
/// every claimable sink beneath it, through nested fan-outs, all or none, and releases them
/// together; a target without the interface is fed but not claimed; a fan-out nobody uses holds
/// nothing; and of two builds racing for overlapping fan-outs, one wins.
/// </summary>
/// <remarks>
/// The race is driven, not timed. Each build claims on a thread of its own that stops after every
/// try on a sink, and the test lets one build or the other take its next try, through every
/// schedule up to six steps. A build that fails stops after the try that failed, before it
/// releases what it took, so the schedules include the one where both have failed while each
/// still holds what the other wanted. One global order is what keeps that schedule from existing.
/// </remarks>
public sealed class FanOutVideoSinkClaimTests
{
    private static string? Holder(IClaimableSink sink) => sink.Claimant;

    private static IClaimableSink Claimable(FanOutVideoSink fanOut) => fanOut;

    [Fact]
    public void AFanOutNobodyUses_HoldsNoClaims()
    {
        var a = new ClaimableRecordingSink("a");
        var b = new ClaimableRecordingSink("b");
        var fanOut = new FanOutVideoSink(a, new FanOutVideoSink(b), new RecordingSink("plain"));

        Assert.Null(Holder(a));
        Assert.Null(Holder(b));
        Assert.Null(Claimable(fanOut).Claimant);
    }

    [Fact]
    public void AClaim_TakesEveryClaimableSinkBeneathIt_ThroughNesting_AndItsLeaseReleasesThemTogether()
    {
        var a = new ClaimableRecordingSink("a");
        var b = new ClaimableRecordingSink("b");
        var c = new ClaimableRecordingSink("c");
        var fanOut = new FanOutVideoSink(
            FanOutTarget.Of(a),
            FanOutTarget.Of(new FanOutVideoSink(b)),
            FanOutTarget.Transform(c, frame => frame.AddRef())
        );

        var lease = Claimable(fanOut).TryClaim("a player");

        Assert.NotNull(lease);
        Assert.All(new[] { a, b, c }, sink => Assert.Equal("a player", Holder(sink)));
        Assert.Equal("a player", Claimable(fanOut).Claimant);
        Assert.Null(Claimable(fanOut).TryClaim("another player"));

        lease!.Dispose();
        Assert.All(new[] { a, b, c }, sink => Assert.Null(Holder(sink)));
        Assert.Null(Claimable(fanOut).Claimant);

        // Released once: disposed again, it leaves a later claim standing.
        using var later = Claimable(fanOut).TryClaim("the next player");
        lease.Dispose();
        Assert.All(new[] { a, b, c }, sink => Assert.Equal("the next player", Holder(sink)));
    }

    [Fact]
    public void AHeldTarget_FailsTheClaim_ReleasesWhatItTook_AndIsTheOneNamed()
    {
        // a comes before b in the claim order, so it is taken before b is found held.
        var a = new ClaimableRecordingSink("a");
        var b = new ClaimableRecordingSink("b");
        var fanOut = new FanOutVideoSink(b, a);
        using var other = b.TryClaim("another player");

        Assert.Null(Claimable(fanOut).TryClaim("a player"));
        Assert.Null(Holder(a));
        Assert.Equal("another player", Claimable(fanOut).Claimant);

        // A build replaces the fan-out by the sinks beneath it, so the sink it names is the target.
        Assert.Null(SinkClaimSet.TryClaim([fanOut], "a player", out var held));
        Assert.Same(b, held);
        Assert.Null(Holder(a));
    }

    [Fact]
    public async Task ATargetWithoutTheInterface_IsFedButNotClaimed()
    {
        var claimable = new ClaimableRecordingSink("claimable");
        var plain = new RecordingSink("plain");
        var fanOut = new FanOutVideoSink(claimable, plain);
        var plainOnly = new FanOutVideoSink(new RecordingSink("plain too"));

        using var lease = Claimable(fanOut).TryClaim("a player");
        await fanOut.PresentAsync(new CountedFrame(), CancellationToken.None);

        Assert.NotNull(lease);
        Assert.Single(plain.Frames);
        // A fan-out over sinks without the interface guards nothing, so claims on it all succeed.
        using var first = Claimable(plainOnly).TryClaim("a player");
        using var second = Claimable(plainOnly).TryClaim("another player");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(Claimable(plainOnly).Claimant);
    }

    [Fact]
    public void ABuild_ReplacesTheFanOutByTheSinksBeneathIt_InTheOneOrder()
    {
        var v1 = new ClaimableRecordingSink("v1");
        var v2 = new ClaimableRecordingSink("v2");
        var v3 = new ClaimableRecordingSink("v3");
        var outer = new FanOutVideoSink(v3, new FanOutVideoSink(v2, v1));

        var order = SinkClaimSet.InClaimOrder([outer]);

        Assert.Equal([v1, v2, v3], order.OfType<ClaimableRecordingSink>());
        Assert.DoesNotContain(order, sink => sink is FanOutVideoSink);
    }

    [Fact]
    public async Task AClaimOnADisposedFanOut_Throws_AndLeavesNoTargetClaimed()
    {
        var a = new ClaimableRecordingSink("a");
        var inner = new FanOutVideoSink(new ClaimableRecordingSink("b"));
        var fanOut = new FanOutVideoSink(a);
        var outer = new FanOutVideoSink(new ClaimableRecordingSink("c"), inner);
        await fanOut.DisposeAsync();
        await inner.DisposeAsync();

        var error = Assert.Throws<ObjectDisposedException>(() => Claimable(fanOut).TryClaim("a player"));
        Assert.Equal(typeof(FanOutVideoSink).FullName, error.ObjectName);
        Assert.Throws<ObjectDisposedException>(() => SinkClaimSet.TryClaim([fanOut], "a player", out _));
        Assert.Throws<ObjectDisposedException>(() => SinkClaimSet.TryClaim([outer], "a player", out _));
        Assert.Null(Holder(a));
        Assert.Null(Claimable(outer).Claimant);
    }

    // ─── Two builds racing ──────────────────────────────────────────

    [Fact]
    public void TwoBuildsRacing_ForFanOutsBesideAnAudioSink_NeverBothFail()
    {
        // The fan-outs are made on either side of the audio sink, so their own orders would put
        // the shared view before the audio sink in one build and after it in the other.
        RaceEverySchedule(() =>
        {
            var v1 = new SteppedSink("v1");
            var v2 = new SteppedSink("v2");
            var v3 = new SteppedSink("v3");
            var first = new FanOutVideoSink(v1, v2);
            var audio = new SteppedSink("audio");
            var second = new FanOutVideoSink(v2, v3);
            return ([first, audio], [audio, second], [v1, v2, v3, audio]);
        });
    }

    [Fact]
    public void TwoBuildsRacing_ForNestedFanOuts_NeverBothFail()
    {
        // Each outer fan-out holds one view directly and the other through a nested fan-out made
        // after both views, so each outer's own order would take the views the other way round.
        RaceEverySchedule(() =>
        {
            var va = new SteppedSink("va");
            var vb = new SteppedSink("vb");
            var first = new FanOutVideoSink(new FanOutVideoSink(va), vb);
            var second = new FanOutVideoSink(new FanOutVideoSink(vb), va);
            return ([first], [second], [va, vb]);
        });
    }

    private const int ScheduleSteps = 6;

    /// <summary>
    /// Runs two builds over fresh sinks from <paramref name="arrange"/> under every schedule of
    /// <see cref="ScheduleSteps"/> steps, then lets each finish, and checks that exactly one won
    /// and that releasing it frees every sink.
    /// </summary>
    private static void RaceEverySchedule(
        Func<(IClaimableSink[] First, IClaimableSink[] Second, SteppedSink[] Leaves)> arrange
    )
    {
        for (var schedule = 0; schedule < 1 << ScheduleSteps; schedule++)
        {
            var (firstSinks, secondSinks, leaves) = arrange();
            using var first = new SteppedClaim(firstSinks, "the first build");
            using var second = new SteppedClaim(secondSinks, "the second build");

            for (var step = 0; step < ScheduleSteps; step++)
                ((schedule >> step & 1) == 0 ? first : second).Step();
            while (!first.Finished || !second.Finished)
            {
                first.Step();
                second.Step();
            }

            Assert.True(
                first.Won ^ second.Won,
                $"Schedule {Convert.ToString(schedule, 2).PadLeft(ScheduleSteps, '0')}: the first build "
                    + $"{(first.Won ? "won" : "failed")} and the second {(second.Won ? "won" : "failed")}."
            );
            first.Release();
            second.Release();
            Assert.All(leaves, leaf => Assert.Null(leaf.Claimant));
        }
    }

    /// <summary>
    /// A claimable sink whose every try stops the build that made it, after the try and before the
    /// build acts on what it got, until the test lets that build go on.
    /// </summary>
    private sealed class SteppedSink(string name) : IVideoSink, IClaimableSink
    {
        private readonly SinkClaimGate _claims = new(typeof(SteppedSink));

        public IDisposable? TryClaim(string claimant)
        {
            var lease = _claims.TryClaim(claimant);
            SteppedClaim.AfterTry();
            return lease;
        }

        public string? Claimant => _claims.Claimant;

        public long ClaimOrder => _claims.ClaimOrder;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) => default;

        public ValueTask DisposeAsync() => default;

        public override string ToString() => name;
    }

    /// <summary>
    /// One build's claim, as <see cref="SinkClaimSet"/> takes it, run on a thread of its own that
    /// stops after each try on a <see cref="SteppedSink"/>. <see cref="Step"/> lets it run to its
    /// next try or to its end, and waits for that.
    /// </summary>
    private sealed class SteppedClaim : IDisposable
    {
        [ThreadStatic]
        private static SteppedClaim? t_current;

        // Bounds a failure only: a step that neither tries a sink nor finishes is a bug.
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

        private readonly SemaphoreSlim _go = new(0);
        private readonly SemaphoreSlim _stopped = new(0);
        private int _finished;
        private IDisposable? _claims;
        private Exception? _error;

        public SteppedClaim(IClaimableSink[] sinks, string claimant)
        {
            var thread = new Thread(() =>
            {
                t_current = this;
                _go.Wait();
                try
                {
                    _claims = SinkClaimSet.TryClaim(sinks, claimant, out _);
                }
                catch (Exception ex)
                {
                    _error = ex;
                }
                finally
                {
                    Volatile.Write(ref _finished, 1);
                    _stopped.Release();
                }
            })
            {
                IsBackground = true,
                Name = claimant,
            };
            thread.Start();
        }

        public bool Finished => Volatile.Read(ref _finished) != 0;

        /// <summary>Whether the claim took every sink. Read once it has finished.</summary>
        public bool Won
        {
            get
            {
                if (_error is not null)
                    throw new InvalidOperationException("The claim threw.", _error);
                return _claims is not null;
            }
        }

        /// <summary>Called on the build's own thread after each try: stops it until the next step.</summary>
        public static void AfterTry()
        {
            if (t_current is not { } current)
                return;
            current._stopped.Release();
            current._go.Wait();
        }

        public void Step()
        {
            if (Finished)
                return;
            _go.Release();
            Assert.True(_stopped.Wait(Bound), "The build neither tried a sink nor finished.");
        }

        /// <summary>Releases what the claim took.</summary>
        public void Release() => _claims?.Dispose();

        public void Dispose()
        {
            while (!Finished)
                Step();
            Release();
            _go.Dispose();
            _stopped.Dispose();
        }
    }
}
