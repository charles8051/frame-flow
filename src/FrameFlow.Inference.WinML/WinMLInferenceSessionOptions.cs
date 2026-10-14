// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Frozen;
using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.WinML;

/// <summary>How a <see cref="WinMLInferenceSession"/> loads its model.</summary>
public sealed record WinMLInferenceSessionOptions
{
    private readonly GraphOptimizationLevel _optimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
    private readonly IReadOnlyDictionary<string, string> _providerOptions = FrozenDictionary<string, string>.Empty;
    private readonly IReadOnlyDictionary<string, string> _sessionConfigEntries = FrozenDictionary<string, string>.Empty;

    /// <summary>
    /// How far ONNX Runtime optimizes the graph before the provider runs it. Defaults to
    /// <see cref="GraphOptimizationLevel.ORT_ENABLE_ALL"/>, ONNX Runtime's own default.
    /// </summary>
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

    /// <summary>
    /// Options for the provider the session appends, by the provider's own names, such as where
    /// TensorRT-RTX keeps its engine cache. Empty by default.
    /// </summary>
    /// <remarks>
    /// Only <see cref="WinMLInferenceSession.OnProvider(string, WinMLInferenceSessionOptions, string, ulong?, Microsoft.Extensions.Logging.ILogger{WinMLInferenceSession}?)"/>
    /// names a provider, so only it takes provider options. A session that lets a
    /// <see cref="WinMLDevicePolicy"/> choose refuses them. The provider checks the names and values
    /// when the session loads.
    /// </remarks>
    /// <exception cref="ArgumentException">A name is empty or blank, or a value is null.</exception>
    public IReadOnlyDictionary<string, string> ProviderOptions
    {
        get => _providerOptions;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            foreach (var (name, setting) in value)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("A provider option's name must not be empty.", nameof(value));
                if (setting is null)
                    throw new ArgumentException($"Provider option '{name}' has no value.", nameof(value));
            }

            // A copy, so the caller changing its dictionary later cannot change what was checked.
            _providerOptions = value.ToFrozenDictionary(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// ONNX Runtime session configuration entries, by its own keys (#716). Empty by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// They apply however the session chooses its provider. Two that the tests exercise:
    /// <c>ep.context_enable</c>, <c>ep.context_file_path</c> and <c>ep.context_embed_mode</c>, which
    /// have TensorRT-RTX write its compiled engine to an EP-context model that later sessions open
    /// in its place; and <c>session.disable_cpu_ep_fallback</c>, which fails the load when nodes
    /// would run on the CPU instead of the provider asked for.
    /// </para>
    /// <para>
    /// ONNX Runtime accepts any key without checking it, so a misspelt key changes nothing.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">A key is empty or blank, or a value is null.</exception>
    public IReadOnlyDictionary<string, string> SessionConfigEntries
    {
        get => _sessionConfigEntries;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            foreach (var (key, setting) in value)
            {
                if (string.IsNullOrWhiteSpace(key))
                    throw new ArgumentException("A session config entry's key must not be empty.", nameof(value));
                if (setting is null)
                    throw new ArgumentException($"Session config entry '{key}' has no value.", nameof(value));
            }

            _sessionConfigEntries = value.ToFrozenDictionary(StringComparer.Ordinal);
        }
    }
}
