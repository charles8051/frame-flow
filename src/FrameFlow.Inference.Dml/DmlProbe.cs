// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.Dml;

/// <summary>
/// Process-cached probe reporting whether the DirectML execution
/// provider is loadable on the current host. Companion to
/// <see cref="FrameFlow.Inference.Cuda.OnnxProbe"/>; substantially
/// simpler because DirectML has no bootstrap requirements
/// (DirectML.dll ships in-box on Windows 10 1903+).
/// </summary>
/// <remarks>
/// Constructs a <see cref="SessionOptions"/>, appends the DirectML
/// EP, and reports success or the underlying exception. Cached for
/// the lifetime of the process — DirectML loadability doesn't change
/// dynamically.
/// </remarks>
public static class DmlProbe
{
    private static readonly Lazy<ProbeResult> _result = new(Run, isThreadSafe: true);

    /// <summary>True when the DirectML EP is loadable in this process.</summary>
    public static bool IsAvailable => _result.Value.Failure is null;

    /// <summary>The exception raised when probing the EP, or null if available.</summary>
    public static Exception? Failure => _result.Value.Failure;

    private static ProbeResult Run()
    {
        try
        {
            using var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
                EnableMemoryPattern = false,
            };
            options.AppendExecutionProvider_DML();
            return new ProbeResult(null);
        }
        catch (Exception ex)
        {
            return new ProbeResult(ex);
        }
    }

    private readonly record struct ProbeResult(Exception? Failure);
}
