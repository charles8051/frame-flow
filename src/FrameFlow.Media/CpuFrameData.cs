// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Provides read-only access to CPU-resident planar YUV or packed frame data.
/// </summary>
/// <remarks>
/// <para>
/// This struct does <strong>not</strong> own the underlying memory — it is a view
/// into buffers managed by an <see cref="IFramePool"/> or a decode allocator.
/// Callers must not retain references beyond the lifetime of the owning
/// <see cref="IVideoFrame"/>.
/// </para>
/// <para>
/// For packed formats (e.g. <see cref="PixelFormat.Bgra32"/>), all pixel data is
/// in <see cref="PlaneY"/> and the U/V planes are empty.
/// </para>
/// </remarks>
/// <param name="PlaneY">Y (or packed) plane data.</param>
/// <param name="PlaneU">U (Cb) plane data. Empty for packed formats.</param>
/// <param name="PlaneV">V (Cr) plane data. Empty for packed formats.</param>
/// <param name="StrideY">Byte stride of the Y plane.</param>
/// <param name="StrideU">Byte stride of the U plane.</param>
/// <param name="StrideV">Byte stride of the V plane.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
public readonly record struct CpuFrameData(
    ReadOnlyMemory<byte> PlaneY,
    ReadOnlyMemory<byte> PlaneU,
    ReadOnlyMemory<byte> PlaneV,
    int StrideY,
    int StrideU,
    int StrideV,
    int Width,
    int Height
);
