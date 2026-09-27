// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Numerics;

namespace FrameFlow.Inference;

/// <summary>
/// The mapping <see cref="ImageToTensor"/> applied, from tensor coordinates to frame pixels. A
/// postprocessor uses it to put a model's outputs back on the frame.
/// </summary>
/// <remarks>
/// Tensor coordinates are normalized: <c>(0, 0)</c> is the tensor's top-left corner and
/// <c>(1, 1)</c> its bottom-right, whatever its size in pixels. A model that reports positions in
/// input pixels divides them by the tensor's width and height first.
/// </remarks>
/// <param name="TensorToFrame">
/// The affine matrix, in <see cref="Matrix3x2"/>'s row-vector convention:
/// <c>frame = Vector2.Transform(tensor, TensorToFrame)</c>.
/// </param>
public readonly record struct TensorTransform(Matrix3x2 TensorToFrame)
{
    /// <summary>The frame pixel position of the tensor point <c>(u, v)</c>.</summary>
    public (float X, float Y) ToFrame(float u, float v)
    {
        var p = Vector2.Transform(new Vector2(u, v), TensorToFrame);
        return (p.X, p.Y);
    }

    /// <summary>The tensor point at the frame pixel position <c>(x, y)</c>.</summary>
    public (float U, float V) ToTensor(float x, float y)
    {
        Matrix3x2.Invert(TensorToFrame, out var frameToTensor);
        var p = Vector2.Transform(new Vector2(x, y), frameToTensor);
        return (p.X, p.Y);
    }
}
