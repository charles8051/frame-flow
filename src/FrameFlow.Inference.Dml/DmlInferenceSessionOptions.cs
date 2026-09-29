// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Frozen;
using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.Dml;

/// <summary>How a <see cref="DmlInferenceSession"/> loads its model.</summary>
public sealed record DmlInferenceSessionOptions
{
    private readonly IReadOnlyDictionary<string, long> _freeDimensions = FrozenDictionary<string, long>.Empty;
    private readonly GraphOptimizationLevel _optimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC;

    /// <summary>
    /// Sizes for the model's named free dimensions, fixed when the session loads, such as
    /// <c>batch = 1</c> or <c>height = 518</c>. Empty by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DirectML runs a model whose input shape is pinned faster than one it has to keep general,
    /// and compiles it faster on the first run. The session's <c>InputShapes</c> and
    /// <c>OutputShapes</c> report the fixed sizes, and every run has to use them.
    /// </para>
    /// <para>
    /// A name the model does not use is ignored, as ONNX Runtime ignores it. The session logs any
    /// input dimension still free after loading, so a misspelt name shows there.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">A name is empty or blank, or a size is below 1.</exception>
    public IReadOnlyDictionary<string, long> FreeDimensions
    {
        get => _freeDimensions;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            foreach (var (name, size) in value)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("A free dimension's name must not be empty.", nameof(value));
                if (size < 1)
                {
                    throw new ArgumentException(
                        $"Free dimension '{name}' is fixed at {size}; a size must be at least 1.", nameof(value));
                }
            }

            // A copy, so the caller changing its dictionary later cannot change what was checked.
            _freeDimensions = value.ToFrozenDictionary(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// How far ONNX Runtime optimizes the graph before DirectML runs it. Defaults to
    /// <see cref="GraphOptimizationLevel.ORT_ENABLE_BASIC"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The level also sets how much GPU memory the session holds. An image encoder measured at
    /// <see cref="GraphOptimizationLevel.ORT_ENABLE_ALL"/> held about 1.3 GB less than at basic,
    /// with its own time unchanged, and a second session sharing the GPU ran 37 times faster (#481).
    /// Raise it when sessions share a GPU.
    /// </para>
    /// <para>
    /// Basic stays the default because the session was written on the premise that the DirectML
    /// provider needs it. The measurements so far found it neither needed nor helpful on the
    /// adapters tried, which does not rule it out on others.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined level.</exception>
    public GraphOptimizationLevel OptimizationLevel
    {
        get => _optimizationLevel;
        init
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Not a defined optimization level.");
            _optimizationLevel = value;
        }
    }
}
