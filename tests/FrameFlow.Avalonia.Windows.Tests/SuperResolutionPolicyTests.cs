using FrameFlow.Avalonia.Windows.Core;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// When the presenter scales through the video processor with the driver's super resolution on,
/// and what it reports when it does not (#560). Pure.
/// </summary>
public sealed class SuperResolutionPolicyTests
{
    // 720p shown at 4K on an NVIDIA adapter in a local session: everything holds.
    private static readonly SuperResolutionInputs Engaged = new(
        Enabled: true,
        D3D11Nv12Frame: true,
        FrameWidth: 1280,
        FrameHeight: 720,
        TargetWidth: 3840,
        TargetHeight: 2160,
        AdapterVendorId: SuperResolutionPolicy.NvidiaVendorId,
        RemoteSession: false,
        LeaseAvailable: true,
        Failed: false);

    private static readonly PresenterOutput Shader = PresenterOutput.Shader(1280, 720);

    [Fact]
    public void WhenEveryConditionHolds_ItScalesToTheTarget()
    {
        var (output, status) = SuperResolutionPolicy.Decide(Engaged);

        Assert.Equal(new PresenterOutput(true, 3840, 2160), output);
        Assert.Equal(SuperResolutionStatus.Requested, status);
    }

    public static TheoryData<string, SuperResolutionStatus> OneConditionFails => new()
    {
        { "off", SuperResolutionStatus.Off },
        { "D3D12VA, 10-bit or a software frame", SuperResolutionStatus.UnsupportedFrames },
        { "another vendor's adapter", SuperResolutionStatus.UnsupportedAdapter },
        { "a remote session", SuperResolutionStatus.RemoteSession },
        { "the video processor failed", SuperResolutionStatus.Unavailable },
        { "shown at its own size", SuperResolutionStatus.NotUpscaling },
        { "shown smaller", SuperResolutionStatus.NotUpscaling },
        { "wider but not taller", SuperResolutionStatus.NotUpscaling },
        { "another view holds the lease", SuperResolutionStatus.InUseByAnotherView },
    };

    [Theory]
    [MemberData(nameof(OneConditionFails))]
    public void WhenOneConditionFails_ItConvertsWithTheShaderAtTheFramesSize_AndNamesIt(
        string condition, SuperResolutionStatus expected)
    {
        var inputs = condition switch
        {
            "off" => Engaged with { Enabled = false },
            "D3D12VA, 10-bit or a software frame" => Engaged with { D3D11Nv12Frame = false },
            "another vendor's adapter" => Engaged with { AdapterVendorId = 0x8086 },
            "a remote session" => Engaged with { RemoteSession = true },
            "the video processor failed" => Engaged with { Failed = true },
            "shown at its own size" => Engaged with { TargetWidth = 1280, TargetHeight = 720 },
            "shown smaller" => Engaged with { TargetWidth = 640, TargetHeight = 360 },
            "wider but not taller" => Engaged with { TargetWidth = 2000, TargetHeight = 720 },
            "another view holds the lease" => Engaged with { LeaseAvailable = false },
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };

        var (output, status) = SuperResolutionPolicy.Decide(inputs);

        Assert.Equal(Shader, output);
        Assert.Equal(expected, status);
    }

    [Fact]
    public void TheStatusNamesTheFirstFailingCondition_InTheOrderChecked()
    {
        // Every condition fails. Off comes first, so a view with the feature off never reports a
        // cause it was not asked about; the lease comes last, so a view that would not use it
        // never reports another view as the reason.
        var allFail = new SuperResolutionInputs(
            Enabled: false, D3D11Nv12Frame: false, FrameWidth: 1280, FrameHeight: 720,
            TargetWidth: 640, TargetHeight: 360, AdapterVendorId: 0x8086, RemoteSession: true,
            LeaseAvailable: false, Failed: true);

        Assert.Equal(SuperResolutionStatus.Off, SuperResolutionPolicy.Decide(allFail).Status);
        Assert.Equal(
            SuperResolutionStatus.UnsupportedFrames,
            SuperResolutionPolicy.Decide(allFail with { Enabled = true }).Status);
        Assert.Equal(
            SuperResolutionStatus.NotUpscaling,
            SuperResolutionPolicy.Decide(Engaged with { TargetWidth = 640, TargetHeight = 360, LeaseAvailable = false }).Status);
    }

    [Theory]
    [InlineData(960, 540, 2.0, 1920, 1080)]
    [InlineData(1280.4, 720.4, 1.0, 1280, 720)] // rounded to the nearest pixel
    [InlineData(853.3333, 480, 1.5, 1280, 720)]
    [InlineData(0, 540, 1.0, 0, 0)]
    [InlineData(960, 540, 0, 0, 0)]
    public void TargetSize_IsTheLayoutInPhysicalPixels(
        double width, double height, double scaling, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), SuperResolutionPolicy.TargetSize(width, height, scaling));
    }

    // ── Settle ──────────────────────────────────────────────────────────────

    private const long Settle = 200;
    private static readonly PresenterOutput At4K = new(true, 3840, 2160);
    private static readonly PresenterOutput At1440 = new(true, 2560, 1440);

    [Fact]
    public void TheFirstOutput_AppliesAtOnce()
    {
        var (next, apply) = default(OutputSettle).Advance(At4K, now: 1000, Settle);

        Assert.True(apply);
        Assert.Equal(At4K, next.Applied);
    }

    [Fact]
    public void TheAppliedOutput_StaysWithoutARebuild()
    {
        var (next, apply) = new OutputSettle(At4K, null, 0).Advance(At4K, now: 5000, Settle);

        Assert.False(apply);
        Assert.Null(next.Pending);
    }

    [Fact]
    public void ANewOutput_AppliesOnlyOnceItHasHeldForTheSettleInterval()
    {
        var settle = new OutputSettle(Shader, null, 0);

        (settle, var first) = settle.Advance(At4K, now: 1000, Settle);
        (settle, var early) = settle.Advance(At4K, now: 1199, Settle);
        (settle, var held) = settle.Advance(At4K, now: 1200, Settle);

        Assert.False(first);
        Assert.False(early);
        Assert.True(held);
        Assert.Equal(At4K, settle.Applied);
        Assert.Null(settle.Pending);
    }

    [Fact]
    public void AResize_RestartsTheInterval_EachTimeTheWantedOutputChanges()
    {
        // A window dragged through sizes: each new size starts the wait again, so the converter is
        // rebuilt once, for the size the drag ends at.
        var settle = new OutputSettle(Shader, null, 0);

        (settle, _) = settle.Advance(At1440, now: 1000, Settle);
        (settle, var midDrag) = settle.Advance(At4K, now: 1150, Settle);
        (settle, var stillShort) = settle.Advance(At4K, now: 1300, Settle);
        (settle, var settled) = settle.Advance(At4K, now: 1350, Settle);

        Assert.False(midDrag);
        Assert.False(stillShort);
        Assert.True(settled);
        Assert.Equal(At4K, settle.Applied);
    }

    [Fact]
    public void GoingBackToTheAppliedOutput_CancelsThePendingOne()
    {
        var settle = new OutputSettle(At4K, null, 0);

        (settle, _) = settle.Advance(At1440, now: 1000, Settle);
        (settle, var back) = settle.Advance(At4K, now: 1100, Settle);
        (settle, var later) = settle.Advance(At4K, now: 5000, Settle);

        Assert.False(back);
        Assert.False(later);
        Assert.Null(settle.Pending);
        Assert.Equal(At4K, settle.Applied);
    }

    // ── Lease ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheLease_AdmitsOneHolder_UntilItReleases()
    {
        var lease = new SuperResolutionLease();
        object first = new(), second = new();

        Assert.True(lease.TryAcquire(first));
        Assert.True(lease.TryAcquire(first)); // already held
        Assert.False(lease.TryAcquire(second));
        Assert.False(lease.IsAvailableTo(second));
        Assert.True(lease.IsAvailableTo(first));

        lease.Release(second); // not the holder: no effect
        Assert.False(lease.IsAvailableTo(second));

        lease.Release(first);
        Assert.True(lease.TryAcquire(second));
    }

    [Fact]
    public void ADroppedConvertersDrain_HoldsTheNextHoldersBlitsBack_UntilItEnds()
    {
        var lease = new SuperResolutionLease();
        object first = new(), second = new();

        Assert.True(lease.TryAcquire(first));
        Assert.True(lease.MayBlit(first));
        Assert.False(lease.MayBlit(second));

        var drain = lease.BeginDrain();
        lease.Release(first);
        Assert.True(lease.IsAvailableTo(second)); // the lease passes on at once
        Assert.True(lease.TryAcquire(second));
        Assert.False(lease.MayBlit(second)); // its blits wait for the drain

        var other = lease.BeginDrain();
        drain.End();
        drain.End(); // ends once
        Assert.False(lease.MayBlit(second));

        other.End();
        Assert.True(lease.MayBlit(second));
        Assert.False(lease.MayBlit(first));
    }
}
