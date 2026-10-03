// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Avalonia.Windows.Core;

/// <summary>
/// How the D3D11 converter fills its ring: with the pixel shader at the frame's size, or through
/// the video processor at the view's size with the driver's super resolution on.
/// </summary>
/// <param name="SuperResolution">True for the video processor with the extension on.</param>
/// <param name="Width">The ring's width in pixels.</param>
/// <param name="Height">The ring's height in pixels.</param>
internal readonly record struct PresenterOutput(bool SuperResolution, int Width, int Height)
{
    /// <summary>The shader at the frame's own size: the converter the presenter builds by default.</summary>
    public static PresenterOutput Shader(int frameWidth, int frameHeight) => new(false, frameWidth, frameHeight);
}

/// <summary>What the engagement decision reads for one frame.</summary>
/// <param name="Enabled">Whether the view's <c>DriverSuperResolution</c> is on.</param>
/// <param name="D3D11Nv12Frame">Whether the frame is a D3D11VA texture in NV12.</param>
/// <param name="FrameWidth">The frame's width in pixels.</param>
/// <param name="FrameHeight">The frame's height in pixels.</param>
/// <param name="TargetWidth">The width the view shows the frame at, in physical pixels.</param>
/// <param name="TargetHeight">The height the view shows the frame at, in physical pixels.</param>
/// <param name="AdapterVendorId">The PCI vendor of the decoder's adapter.</param>
/// <param name="RemoteSession">Whether the process runs in a remote session.</param>
/// <param name="LeaseAvailable">Whether the process-wide lease is free, or already this view's.</param>
/// <param name="Failed">Whether the video processor failed to set up for this view.</param>
internal readonly record struct SuperResolutionInputs(
    bool Enabled,
    bool D3D11Nv12Frame,
    int FrameWidth,
    int FrameHeight,
    int TargetWidth,
    int TargetHeight,
    uint AdapterVendorId,
    bool RemoteSession,
    bool LeaseAvailable,
    bool Failed);

/// <summary>
/// Whether the presenter scales a frame through the video processor with the driver's super
/// resolution on, and at what size (#560). Pure: the inputs in, an output and a status out.
/// </summary>
/// <remarks>
/// <para>
/// It engages only when every condition holds; otherwise the output is the shader at the frame's
/// size, which is the converter the presenter builds with the feature off. The status names the
/// first condition that failed, in the order checked here.
/// </para>
/// <para>
/// The video processor runs only on an NVIDIA adapter, whose driver the extension belongs to, and
/// only while the view is larger than the frame. A process-wide lease admits one view at a time,
/// because the driver upscales one stream at a time (ADR-0082, amendment of 2026-10-03).
/// Downscaling with the extension on leaves the output unchanged, and 1:1 costs the full price for
/// a small sharpening, so neither engages.
/// </para>
/// </remarks>
internal static class SuperResolutionPolicy
{
    /// <summary>NVIDIA's PCI vendor ID.</summary>
    public const uint NvidiaVendorId = 0x10DE;

    /// <summary>Decides the output and the status for one frame.</summary>
    public static (PresenterOutput Output, SuperResolutionStatus Status) Decide(in SuperResolutionInputs inputs)
    {
        var shader = PresenterOutput.Shader(inputs.FrameWidth, inputs.FrameHeight);
        if (!inputs.Enabled)
            return (shader, SuperResolutionStatus.Off);
        if (!inputs.D3D11Nv12Frame)
            return (shader, SuperResolutionStatus.UnsupportedFrames);
        if (inputs.AdapterVendorId != NvidiaVendorId)
            return (shader, SuperResolutionStatus.UnsupportedAdapter);
        if (inputs.RemoteSession)
            return (shader, SuperResolutionStatus.RemoteSession);
        if (inputs.Failed)
            return (shader, SuperResolutionStatus.Unavailable);
        if (inputs.TargetWidth <= inputs.FrameWidth || inputs.TargetHeight <= inputs.FrameHeight)
            return (shader, SuperResolutionStatus.NotUpscaling);
        if (!inputs.LeaseAvailable)
            return (shader, SuperResolutionStatus.InUseByAnotherView);
        return (new PresenterOutput(true, inputs.TargetWidth, inputs.TargetHeight), SuperResolutionStatus.Requested);
    }

    /// <summary>
    /// The size, in physical pixels, of a surface laid out at <paramref name="width"/> x
    /// <paramref name="height"/> device-independent pixels on a display scaled by
    /// <paramref name="scaling"/>, rounded to the nearest pixel. Zero for an empty or invalid one.
    /// </summary>
    public static (int Width, int Height) TargetSize(double width, double height, double scaling)
    {
        if (!(width > 0) || !(height > 0) || !(scaling > 0))
            return (0, 0);
        return ((int)Math.Round(width * scaling), (int)Math.Round(height * scaling));
    }
}

/// <summary>
/// When a change of the presenter's output is applied (#560). A live resize changes the wanted
/// output on every layout pass, and each change rebuilds the converter and re-imports its ring, so
/// a change applies only once the same output has been wanted for the settle interval. Pure: the
/// caller supplies the timestamps.
/// </summary>
/// <param name="Applied">The output the converter was built with, or null before one is built.</param>
/// <param name="Pending">The output wanted but not yet applied, or null.</param>
/// <param name="PendingSince">The timestamp <see cref="Pending"/> was first wanted at.</param>
internal readonly record struct OutputSettle(PresenterOutput? Applied, PresenterOutput? Pending, long PendingSince)
{
    /// <summary>
    /// Folds in the output wanted at <paramref name="now"/>. Applies it at once when nothing is
    /// applied yet; otherwise once it has been wanted, unchanged, for
    /// <paramref name="settleTicks"/>. Returns whether to apply it now.
    /// </summary>
    public (OutputSettle Next, bool Apply) Advance(PresenterOutput wanted, long now, long settleTicks)
    {
        if (Applied is null)
            return (new OutputSettle(wanted, null, 0), true);
        if (Applied == wanted)
            return (new OutputSettle(Applied, null, 0), false);
        if (Pending != wanted)
            return (new OutputSettle(Applied, wanted, now), false);
        if (now - PendingSince >= settleTicks)
            return (new OutputSettle(wanted, null, 0), true);
        return (this, false);
    }
}
