// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Frozen;

namespace FrameFlow.Inference.Dml;

/// <summary>How a <see cref="DmlInferenceSession"/> loads its model.</summary>
public sealed record DmlInferenceSessionOptions
{
    private readonly IReadOnlyDictionary<string, long> _freeDimensions = FrozenDictionary<string, long>.Empty;

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
}
