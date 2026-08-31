// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// The minimal frame primitive Crossbar's runtime requires. Library-specific
/// frame types (e.g. <c>ICameraFrame</c> in <c>Periphery.Camera</c>,
/// <c>IVideoFrame</c> in <c>FrameFlow.Media</c>) extend this with
/// pixel-format vocabularies, plane accessors, ref-counting semantics, etc.
/// </summary>
/// <remarks>
/// <para>
/// Crossbar uses <see cref="IFrame"/> as a structural constraint rather than
/// an inheritance root — it is sufficient to satisfy the runtime's needs
/// (dimensions, timestamp, deterministic disposal). Consumers should
/// continue to design their richer frame types as the public surface;
/// <see cref="IFrame"/> exists so the runtime stays format-neutral.
/// </para>
/// <para>
/// Disposal semantics are intentionally library-specific. Crossbar treats
/// every frame it surfaces as owned by the recipient — sinks dispose
/// frames they accept; transforms dispose inputs once they emit
/// replacements. The frame-lifetime model (single-owner lease,
/// ref-counted, etc.) is internal to whatever <see cref="IFrame"/>
/// implementation is in use.
/// </para>
/// </remarks>
public interface IFrame : IDisposable
{
    /// <summary>Frame width in pixels.</summary>
    int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    int Height { get; }

    /// <summary>Presentation timestamp from the producer's clock.</summary>
    TimeSpan Timestamp { get; }
}
