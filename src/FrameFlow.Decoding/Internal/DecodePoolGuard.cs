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
    /// <summary>No pool reaches the graph: software decode, or a decoder that reads back.</summary>
    NoPool,

    /// <summary>
    /// The pool was opened with room for everything the graph can hold, or grows, and is guarded
    /// at the graph's budget (#416).
    /// </summary>
    Fits,

    /// <summary>
    /// The graph can hold more than the pool was opened for. The decoder waits at its budget.
    /// </summary>
    OverPool,

    /// <summary>A holder on the path declares no bound, so no pool size is enough.</summary>
    Unbounded,
}

/// <summary>
/// The pool guard's policy (ADR-0081 decision 5, and #416 for growable pools), as total functions over
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
    /// decode grow. So does Vulkan: FFmpeg creates an image for each request with no ceiling,
    /// and ignores <c>extra_hw_frames</c> because its decoder sets no initial pool size (#414).
    /// So does D3D12VA: FFmpeg creates a texture for each request, and uses a fixed texture
    /// array only when the caller asks for one, which FrameFlow does not (#415). The other
    /// backends are uncharacterised (#230) and treated as fixed with no known spare.
    /// </remarks>
    public static int? SpareSurfaces(HardwareDecodeBackendKind backend) =>
        backend switch
        {
            HardwareDecodeBackendKind.D3D11Va or HardwareDecodeBackendKind.Dxva2 => 3,
            HardwareDecodeBackendKind.VideoToolbox
                or HardwareDecodeBackendKind.Vulkan
                or HardwareDecodeBackendKind.D3D12Va => null,
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
    /// The budget a pool is guarded at. A fixed pool's is its spare surfaces plus the
    /// <c>extra_hw_frames</c> the decoder opened with, and zero, which leaves it unguarded, for an
    /// uncharacterised one opened without extra surfaces, where nothing says how many are free. A
    /// growable pool's is <paramref name="graphFrames"/>, the most frames the decoder's graph can
    /// hold, so a holder that keeps more than it declared parks the decoder rather than growing
    /// GPU memory without limit (#416). Zero when no graph has declared one.
    /// </summary>
    public static int BudgetFor(HardwareDecodeBackendKind backend, int extraHwFrames, int graphFrames) =>
        SpareSurfaces(backend) is int spare ? spare + Math.Max(0, extraHwFrames) : Math.Max(0, graphFrames);

    /// <summary>
    /// Judges a graph's budget against the pool a decoder opened: <paramref name="budgetFrames"/>
    /// is the most frames the graph can hold, or <see langword="null"/> when a holder on the path
    /// declares no bound. A path with no bound is refused on a growable pool as on a fixed one:
    /// the fixed pool faults, and the growable one takes GPU memory until an allocation fails.
    /// </summary>
    public static PoolBudgetVerdict Judge(
        HardwareDecodeBackendKind? backend,
        int extraHwFrames,
        bool yieldsHardwareFrames,
        int? budgetFrames
    )
    {
        if (!yieldsHardwareFrames || backend is not { } bound)
            return PoolBudgetVerdict.NoPool;
        if (budgetFrames is not { } frames)
            return PoolBudgetVerdict.Unbounded;
        if (SpareSurfaces(bound) is null)
            return PoolBudgetVerdict.Fits;
        return frames > BudgetFor(bound, extraHwFrames, graphFrames: 0)
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
