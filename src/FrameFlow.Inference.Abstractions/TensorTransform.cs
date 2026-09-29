// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Numerics;

namespace FrameFlow.Inference;

/// <summary>
/// The mapping <see cref="ImageToTensor"/> applied, from tensor coordinates to frame pixels. A
/// postprocessor uses it to put a model's outputs back on the frame.
/// </summary>
/// <remarks>
/// <para>
/// Tensor coordinates are normalized: <c>(0, 0)</c> is the tensor's top-left corner and
/// <c>(1, 1)</c> its bottom-right, whatever its size in pixels. A model that reports positions in
/// input pixels divides them by the tensor's width and height first.
/// </para>
/// <para>
/// The inverse is computed once, when the matrix is set, so <see cref="ToTensor"/> can map every
/// pixel of a region without inverting each time (#484). Two transforms are equal when their
/// matrices are.
/// </para>
/// </remarks>
public readonly record struct TensorTransform
{
    private readonly Matrix3x2 _tensorToFrame;
    private readonly Matrix3x2 _frameToTensor;

    // False only for default(TensorTransform), which no constructor or initializer has set.
    private readonly bool _inverted;

    /// <summary>A transform whose matrix is <paramref name="TensorToFrame"/>.</summary>
    /// <param name="TensorToFrame">
    /// The affine matrix, in <see cref="Matrix3x2"/>'s row-vector convention:
    /// <c>frame = Vector2.Transform(tensor, TensorToFrame)</c>.
    /// </param>
    public TensorTransform(Matrix3x2 TensorToFrame) => this.TensorToFrame = TensorToFrame;

    /// <summary>
    /// The affine matrix, in <see cref="Matrix3x2"/>'s row-vector convention:
    /// <c>frame = Vector2.Transform(tensor, TensorToFrame)</c>.
    /// </summary>
    public Matrix3x2 TensorToFrame
    {
        get => _tensorToFrame;
        init
        {
            _tensorToFrame = value;
            // A singular matrix inverts to NaN, as it did when ToTensor inverted on every call.
            Matrix3x2.Invert(value, out _frameToTensor);
            _inverted = true;
        }
    }

    /// <summary>The frame pixel position of the tensor point <c>(u, v)</c>.</summary>
    public (float X, float Y) ToFrame(float u, float v)
    {
        var p = Vector2.Transform(new Vector2(u, v), _tensorToFrame);
        return (p.X, p.Y);
    }

    /// <summary>The tensor point at the frame pixel position <c>(x, y)</c>.</summary>
    public (float U, float V) ToTensor(float x, float y)
    {
        var frameToTensor = _frameToTensor;
        if (!_inverted)
            Matrix3x2.Invert(_tensorToFrame, out frameToTensor);

        var p = Vector2.Transform(new Vector2(x, y), frameToTensor);
        return (p.X, p.Y);
    }

    /// <summary>Whether <paramref name="other"/> has the same matrix.</summary>
    public bool Equals(TensorTransform other) => _tensorToFrame.Equals(other._tensorToFrame);

    /// <inheritdoc />
    public override int GetHashCode() => _tensorToFrame.GetHashCode();

    /// <summary>The matrix, as the positional form of this record gave it.</summary>
    public void Deconstruct(out Matrix3x2 TensorToFrame) => TensorToFrame = _tensorToFrame;
}
