// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.ML.OnnxRuntime.Tensors;

namespace FrameFlow.Inference.Dml.Core;

/// <summary>
/// Whether a device tensor fits the model input it is bound to: the same element type, the same
/// rank, and the same size in every dimension the model fixes. Pure.
/// </summary>
internal static class DeviceInputFit
{
    /// <summary>
    /// Null when <paramref name="tensor"/> fits; otherwise why not. A dimension of <c>-1</c> in
    /// <paramref name="modelShape"/> is dynamic and takes any size.
    /// </summary>
    public static string? Mismatch(
        IReadOnlyList<long> modelShape, TensorElementType modelType, TensorElementType tensorType, DeviceTensor tensor)
    {
        ArgumentNullException.ThrowIfNull(modelShape);

        bool shapeFits = modelShape.Count == tensor.Shape.Rank;
        for (int i = 0; shapeFits && i < modelShape.Count; i++)
            shapeFits = modelShape[i] < 0 || modelShape[i] == tensor.Shape[i];

        if (shapeFits && modelType == tensorType)
            return null;

        return $"the tensor is {tensorType} [{string.Join(", ", Enumerable.Range(0, tensor.Shape.Rank).Select(i => tensor.Shape[i]))}] "
            + $"and the model takes {modelType} [{string.Join(", ", modelShape.Select(d => d < 0 ? "?" : d.ToString(System.Globalization.CultureInfo.InvariantCulture)))}]";
    }
}
