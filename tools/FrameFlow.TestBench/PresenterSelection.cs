namespace FrameFlow.TestBench;

/// <summary>Which video surface the bench presents to.</summary>
internal enum PresenterKind
{
    /// <summary>No window. Counts frames, with an optional synthetic present cost.</summary>
    Headless,

    /// <summary>A window, presenting through <c>WriteableBitmap</c>.</summary>
    Cpu,

    /// <summary>A window, presenting through the zero-copy compositor path.</summary>
    Gpu,
}

/// <summary>
/// What was asked for, what was built, and why they differ.
/// </summary>
/// <remarks>
/// <para>
/// The two are separate fields on purpose. <c>--presenter gpu</c> falls back to the CPU
/// surface off Windows and the flag still reads <c>gpu</c>, so a bench that reported the
/// request would let a run measure the software path while its transcript said otherwise.
/// The ADR's <c>require</c> rule existed to catch exactly this and checked the resolved
/// configuration rather than the flag string; with the grammar gone, reporting the
/// resolved value is what is left of that guarantee, and it is the more useful half.
/// </para>
/// <para>
/// This does not answer the harder question of whether the zero-copy path <i>engaged</i>
/// once running. <c>CompositionInteropVideoView</c> decides GPU-import against
/// CPU-upload per frame and keeps the answer in a private field, surfacing it only as a
/// log line. <see cref="FrameFlow.Media.Diagnostics.VideoSinkDiagnosticsSnapshot"/> has
/// no presenter-kind field either. So this reports the surface that was constructed,
/// which is the question <c>--presenter</c> asks.
/// </para>
/// </remarks>
/// <param name="Requested">What the invocation asked for.</param>
/// <param name="Resolved">What was actually built.</param>
/// <param name="Reason">Why they differ, or <see langword="null"/> when they do not.</param>
internal readonly record struct PresenterSelection(
    PresenterKind Requested,
    PresenterKind Resolved,
    string? Reason
)
{
    internal bool FellBack => Requested != Resolved;

    /// <summary>Whether the resolved presenter needs a window and a UI thread.</summary>
    internal bool NeedsWindow => Resolved is PresenterKind.Cpu or PresenterKind.Gpu;

    /// <summary>
    /// Applies the platform rule. The GPU surface is Windows-only.
    /// </summary>
    /// <remarks>
    /// <c>FrameFlow.Avalonia.Windows</c> carries no platform gate of its own — it is
    /// platform-neutral by construction and simply depends on Direct3D at run time — so
    /// the check belongs to whoever builds the surface. Three examples already make it
    /// (<c>AvaloniaPlayer</c>, <c>LiveCaptioning</c>, <c>Multicast</c>); the bench is the
    /// fourth, and the first to report the outcome rather than only logging it.
    /// </remarks>
    internal static PresenterSelection Resolve(PresenterKind requested) =>
        requested == PresenterKind.Gpu && !OperatingSystem.IsWindows()
            ? new PresenterSelection(
                requested,
                PresenterKind.Cpu,
                "the zero-copy compositor surface is Windows-only"
            )
            : new PresenterSelection(requested, requested, null);

    /// <summary>Parses the <c>--presenter</c> argument.</summary>
    internal static PresenterKind? ParseKind(string text) =>
        text.Trim().ToLowerInvariant() switch
        {
            "headless" or "none" => PresenterKind.Headless,
            "cpu" => PresenterKind.Cpu,
            "gpu" => PresenterKind.Gpu,
            _ => null,
        };

    public override string ToString() =>
        FellBack ? $"{Resolved} (requested {Requested}: {Reason})" : Resolved.ToString();
}
