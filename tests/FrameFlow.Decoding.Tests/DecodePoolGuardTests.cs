using FrameFlow.Decoding.Internal;
using FrameFlow.Media;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// The fixed-pool guard's policy (ADR-0081 decision 5), as table tests over its pure core.
/// </summary>
public sealed class DecodePoolGuardTests
{
    [Theory]
    [InlineData(HardwareDecodeBackendKind.D3D11Va, 3)]
    [InlineData(HardwareDecodeBackendKind.Dxva2, 3)]
    [InlineData(HardwareDecodeBackendKind.Cuda, 0)]
    [InlineData(HardwareDecodeBackendKind.VaApi, 0)]
    [InlineData(HardwareDecodeBackendKind.Qsv, 0)]
    public void AFixedPool_HasItsModelsSpareSurfaces(HardwareDecodeBackendKind backend, int spare)
    {
        Assert.Equal(spare, DecodePoolGuard.SpareSurfaces(backend));
    }

    [Fact]
    public void AGrowablePool_HasNoSpareCount()
    {
        Assert.Null(DecodePoolGuard.SpareSurfaces(HardwareDecodeBackendKind.VideoToolbox));
    }

    [Theory]
    [InlineData(HardwareDecodeBackendKind.D3D11Va, 5, 2)]
    [InlineData(HardwareDecodeBackendKind.D3D11Va, 3, 0)]
    [InlineData(HardwareDecodeBackendKind.D3D11Va, 1, 0)]
    [InlineData(HardwareDecodeBackendKind.D3D11Va, 0, 0)]
    [InlineData(HardwareDecodeBackendKind.Dxva2, 8, 5)]
    [InlineData(HardwareDecodeBackendKind.Cuda, 5, 5)]
    [InlineData(HardwareDecodeBackendKind.VideoToolbox, 5, 0)]
    public void TheExtraSurfaces_AreWhatTheSpareOnesDoNotCover(
        HardwareDecodeBackendKind backend,
        int held,
        int extra
    )
    {
        Assert.Equal(extra, DecodePoolGuard.ExtraSurfacesFor(backend, held));
    }

    [Theory]
    [InlineData(HardwareDecodeBackendKind.D3D11Va, 5)]
    [InlineData(HardwareDecodeBackendKind.D3D11Va, 8)]
    [InlineData(HardwareDecodeBackendKind.Dxva2, 3)]
    [InlineData(HardwareDecodeBackendKind.Cuda, 5)]
    public void OpenedWithItsExtraSurfaces_AFixedPoolsBudgetIsWhatTheCallerHolds(
        HardwareDecodeBackendKind backend,
        int held
    )
    {
        int extra = DecodePoolGuard.ExtraSurfacesFor(backend, held);
        Assert.Equal(held, DecodePoolGuard.BudgetFor(backend, extra));
    }

    [Theory]
    // Frames from a fixed pool: the budget fits, is over the pool, or has no bound.
    [InlineData(HardwareDecodeBackendKind.D3D11Va, 7, true, 10, "Fits")]
    [InlineData(HardwareDecodeBackendKind.D3D11Va, 7, true, 9, "Fits")]
    [InlineData(HardwareDecodeBackendKind.D3D11Va, 7, true, 11, "OverPool")]
    [InlineData(HardwareDecodeBackendKind.D3D11Va, 7, true, null, "Unbounded")]
    [InlineData(HardwareDecodeBackendKind.Cuda, 0, true, 1, "OverPool")]
    // No fixed pool reaches the graph: a readback decoder, software, or a pool that grows.
    [InlineData(HardwareDecodeBackendKind.D3D11Va, 7, false, null, "NoPool")]
    [InlineData(null, 0, true, null, "NoPool")]
    [InlineData(HardwareDecodeBackendKind.VideoToolbox, 0, true, null, "NoPool")]
    public void AGraphsBudget_IsJudgedAgainstThePoolTheDecoderOpened(
        HardwareDecodeBackendKind? backend,
        int extra,
        bool yields,
        int? budget,
        string verdict
    )
    {
        Assert.Equal(
            Enum.Parse<PoolBudgetVerdict>(verdict),
            DecodePoolGuard.Judge(backend, extra, yields, budget)
        );
    }

    [Theory]
    [InlineData(HardwareDecodeBackendKind.D3D11Va, 0, 3)]
    [InlineData(HardwareDecodeBackendKind.D3D11Va, 2, 5)]
    [InlineData(HardwareDecodeBackendKind.D3D11Va, -4, 3)]
    [InlineData(HardwareDecodeBackendKind.Cuda, 0, 0)]
    [InlineData(HardwareDecodeBackendKind.Cuda, 5, 5)]
    [InlineData(HardwareDecodeBackendKind.VideoToolbox, 5, 0)]
    public void TheBudget_IsTheSpareSurfacesPlusTheExtraOnes(
        HardwareDecodeBackendKind backend,
        int extra,
        int budget
    )
    {
        Assert.Equal(budget, DecodePoolGuard.BudgetFor(backend, extra));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 50, true)]
    [InlineData(3, 0, true)]
    [InlineData(3, 2, true)]
    [InlineData(3, 3, false)]
    [InlineData(3, 4, false)]
    public void TheDecoderMayDecode_OnlyBelowTheBudget(int budget, int outstanding, bool may)
    {
        var state = new PoolGuardState(outstanding, budget, OverBudgetReported: false);

        Assert.Equal(may, DecodePoolGuard.MayDecode(state));
    }

    [Fact]
    public void ABreach_IsReportedOnce_AndAgainAfterTheCountRecovers()
    {
        var state = PoolGuardState.Initial(budget: 1);

        (state, bool first) = DecodePoolGuard.HandedOut(state); // 1: at the budget
        Assert.False(first);

        (state, bool breach) = DecodePoolGuard.HandedOut(state); // 2: over
        Assert.True(breach);

        (state, bool again) = DecodePoolGuard.HandedOut(state); // 3: still the same breach
        Assert.False(again);

        state = DecodePoolGuard.Released(state); // 2: still over
        state = DecodePoolGuard.Released(state); // 1: back within
        Assert.False(state.OverBudgetReported);

        (state, bool next) = DecodePoolGuard.HandedOut(state); // 2: a new breach
        Assert.True(next);
        Assert.Equal(2, state.Outstanding);
    }

    [Fact]
    public void AnUnguardedPool_CountsButNeverReports()
    {
        var state = PoolGuardState.Initial(budget: 0);

        for (int i = 0; i < 30; i++)
        {
            (state, bool breach) = DecodePoolGuard.HandedOut(state);
            Assert.False(breach);
        }

        Assert.Equal(30, state.Outstanding);
        Assert.True(DecodePoolGuard.MayDecode(state));
    }

    [Fact]
    public void AReleaseAtZero_StaysAtZero()
    {
        var state = DecodePoolGuard.Released(PoolGuardState.Initial(budget: 3));

        Assert.Equal(0, state.Outstanding);
    }
}
