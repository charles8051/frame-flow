// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Decoding.Internal;

/// <summary>
/// One pool's guard: how many frames it has handed downstream that are still live, the most it
/// may hand out, and whether the current breach has been reported.
/// </summary>
/// <param name="Outstanding">Frames built from the pool and not yet released.</param>
/// <param name="Budget">The most frames the pool may hand out; zero means unguarded.</param>
/// <param name="OverBudgetReported">Whether the breach in progress has been logged.</param>
internal readonly record struct PoolGuardState(int Outstanding, int Budget, bool OverBudgetReported)
{
    /// <summary>A pool that has handed nothing out.</summary>
    public static PoolGuardState Initial(int budget) => new(0, budget, false);

    /// <summary>Whether the guard limits this pool at all.</summary>
    public bool IsGuarded => Budget > 0;
}

/// <summary>What a graph's frame budget means for a hardware decoder's pool (ADR-0081).</summary>
internal enum PoolBudgetVerdict
{
    /// <summary>No fixed pool: software decode, readback, or a pool that grows.</summary>
    NoPool,

    /// <summary>The pool was opened with room for everything the graph can hold.</summary>
    Fits,

    /// <summary>
    /// The graph can hold more than the pool was opened for. The decoder waits at its budget.
    /// </summary>
    OverPool,

    /// <summary>A holder on the path declares no bound, so no pool size is enough.</summary>
    Unbounded,
}

/// <summary>
/// The fixed-pool guard's policy (ADR-0081 decision 5, phase 1), as total functions over
/// <see cref="PoolGuardState"/>. The shell (<see cref="DecodePoolGeneration"/>) owns the wait,
/// its cancellation and the watchdog's clock.
/// </summary>
/// <remarks>
/// <para>
/// An exhausted pool fails the decode call and the decoder faults (#370). So when a pool has
/// handed out its budget, the decoder waits for a release before it decodes again, and the
/// pool paces the decoder instead.
/// </para>
/// <para>
/// Only frames handed downstream count. A frame the decoder skips for lateness is released
/// before it is handed on, so lateness recovery never waits on the guard.
/// </para>
/// </remarks>
internal static class DecodePoolGuard
{
    /// <summary>
    /// Surfaces a backend's pool holds beyond what the decoder can use itself, or
    /// <see langword="null"/> for a pool that grows.
    /// </summary>
    /// <remarks>
    /// FFmpeg sizes a D3D11VA or DXVA2 pool as one working surface, the codec's reference
    /// maximum, three more working surfaces and <c>extra_hw_frames</c>, so a stream using every
    /// reference leaves three for consumers (frame-pool ownership). VideoToolbox and software
    /// decode grow. The other backends are uncharacterised (#230) and treated as fixed with no
    /// known spare.
    /// </remarks>
    public static int? SpareSurfaces(HardwareDecodeBackendKind backend) =>
        backend switch
        {
            HardwareDecodeBackendKind.D3D11Va or HardwareDecodeBackendKind.Dxva2 => 3,
            HardwareDecodeBackendKind.VideoToolbox => null,
            _ => 0,
        };

    /// <summary>
    /// The <c>extra_hw_frames</c> to open a decoder with so that <paramref name="heldFrames"/>
    /// can be out at once: what the pool's spare surfaces do not cover. Zero for a growable pool,
    /// which FFmpeg does not size.
    /// </summary>
    public static int ExtraSurfacesFor(HardwareDecodeBackendKind backend, int heldFrames) =>
        SpareSurfaces(backend) is int spare ? Math.Max(0, heldFrames - spare) : 0;

    /// <summary>
    /// The phase-1 budget: the pool's spare surfaces plus the <c>extra_hw_frames</c> the decoder
    /// opened with. Zero, which leaves the pool unguarded, for a growable pool, and for an
    /// uncharacterised one opened without extra surfaces, where nothing says how many are free.
    /// </summary>
    public static int BudgetFor(HardwareDecodeBackendKind backend, int extraHwFrames) =>
        SpareSurfaces(backend) is int spare ? spare + Math.Max(0, extraHwFrames) : 0;

    /// <summary>
    /// Judges a graph's budget against the pool a decoder opened: <paramref name="budgetFrames"/>
    /// is the most frames the graph can hold, or <see langword="null"/> when a holder on the path
    /// declares no bound.
    /// </summary>
    public static PoolBudgetVerdict Judge(
        HardwareDecodeBackendKind? backend,
        int extraHwFrames,
        bool yieldsHardwareFrames,
        int? budgetFrames
    )
    {
        if (!yieldsHardwareFrames || backend is not { } bound || SpareSurfaces(bound) is null)
            return PoolBudgetVerdict.NoPool;
        if (budgetFrames is not { } frames)
            return PoolBudgetVerdict.Unbounded;
        return frames > BudgetFor(bound, extraHwFrames)
            ? PoolBudgetVerdict.OverPool
            : PoolBudgetVerdict.Fits;
    }

    /// <summary>Whether the decoder may decode into the pool now.</summary>
    public static bool MayDecode(PoolGuardState state) =>
        !state.IsGuarded || state.Outstanding < state.Budget;

    /// <summary>
    /// A frame built from the pool was handed downstream. Reports a breach once: the budget can
    /// be passed only by frames the decoder had already decoded when it last proceeded, which a
    /// reordering codec can release in a burst.
    /// </summary>
    public static (PoolGuardState State, bool ReportOverBudget) HandedOut(PoolGuardState state)
    {
        int outstanding = state.Outstanding + 1;
        bool over = state.IsGuarded && outstanding > state.Budget;
        return (
            state with { Outstanding = outstanding, OverBudgetReported = state.OverBudgetReported || over },
            over && !state.OverBudgetReported
        );
    }

    /// <summary>
    /// A frame built from the pool was released. A breach ends when the count is back within
    /// the budget, so the next one is reported again.
    /// </summary>
    public static PoolGuardState Released(PoolGuardState state)
    {
        int outstanding = Math.Max(0, state.Outstanding - 1);
        return state with
        {
            Outstanding = outstanding,
            OverBudgetReported = state.OverBudgetReported && outstanding > state.Budget,
        };
    }
}
