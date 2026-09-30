// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using FrameFlow.Inference.Core;

namespace FrameFlow.Inference;

/// <summary>
/// Reads a Float32 or Float16 tensor as floats: a Float32 tensor's own elements, or a Float16
/// tensor's converted into a buffer this keeps and reuses, so a decoder written for floats reads a
/// model's fp16 output too (#10). A span it returns is valid until its next read. Not thread-safe.
/// </summary>
internal sealed class FloatReader
{
    private float[] _buffer = [];

    /// <summary>Reads <paramref name="tensor"/>'s elements as floats.</summary>
    /// <exception cref="NotSupportedException"><paramref name="tensor"/> holds neither floats nor halves.</exception>
    public ReadOnlySpan<float> Read(ICpuTensor tensor)
    {
        ArgumentNullException.ThrowIfNull(tensor);
        var bytes = tensor.Bytes.Span;
        switch (tensor.Dtype)
        {
            case DType.Float32:
                return MemoryMarshal.Cast<byte, float>(bytes);
            case DType.Float16:
                var halves = MemoryMarshal.Cast<byte, Half>(bytes);
                if (_buffer.Length < halves.Length)
                    _buffer = new float[halves.Length];
                var floats = _buffer.AsSpan(0, halves.Length);
                HalfToFloatKernel.ToFloats(halves, floats);
                return floats;
            default:
                throw new NotSupportedException($"A {tensor.Dtype} tensor is read as floats only when it holds Float32 or Float16.");
        }
    }
}
