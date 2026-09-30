// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using FrameFlow.Inference.OpenVino.Core;
using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.OpenVino;

/// <summary>
/// ONNX Runtime inference session on Intel's OpenVINO execution provider, accepting
/// <see cref="ICpuTensor"/> inputs and outputs. Sibling to <c>DmlInferenceSession</c> and
/// <c>WinMLInferenceSession</c> per ADR-0049 §3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it fits.</b> Windows ML reaches OpenVINO only on Windows 11 24H2 and later; this session
/// reaches it on any Windows x64. On a Gen9 Intel GPU it ran yolov8n at 13.3 ms against DirectML's
/// 24.7 ms (docs/investigations/2026-09-29-openvino-ep-on-intel-igpu.md).
/// </para>
/// <para>
/// <b>Options.</b> An <see cref="OpenVinoInferenceSessionOptions"/> sets the device, GPU by default,
/// the compiled-model cache, the GPU queue throttle and the precision hint. The session also asks
/// OpenVINO for latency (<c>PERFORMANCE_HINT=LATENCY</c>), since it runs one inference at a time.
/// </para>
/// <para>
/// <b>A model OpenVINO takes only in part</b> fails to open, unless
/// <see cref="OpenVinoInferenceSessionOptions.AllowCpuFallback"/> lets the rest run on ONNX Runtime's
/// CPU provider. That option says why.
/// </para>
/// <para>
/// <b>First open.</b> OpenVINO compiles the model for the device as the session opens: 16 s for a
/// YOLO model on the Gen9 GPU, about 4 s from the cache. Open off the UI thread.
/// </para>
/// <para>
/// <b>Windows x64 only.</b> Intel's package carries its <c>onnxruntime.dll</c>, OpenVINO and its
/// plugins for win-x64 alone. The session throws <see cref="PlatformNotSupportedException"/> anywhere
/// else, before loading anything.
/// </para>
/// <para>
/// <b>Threading.</b> A single session is safe for sequential Run calls. Concurrent calls against one
/// session are not supported.
/// </para>
/// </remarks>
public sealed class OpenVinoInferenceSession : OrtInferenceSessionBase
{
    private static readonly OpenVinoInferenceSessionOptions DefaultOptions = new();

    /// <summary>Loads a model from <paramref name="modelPath"/> on OpenVINO's GPU device.</summary>
    /// <exception cref="PlatformNotSupportedException">The process is not Windows x64.</exception>
    public OpenVinoInferenceSession(string modelPath)
        : this(modelPath, DefaultOptions) { }

    /// <summary>Loads a model from <paramref name="modelPath"/> as <paramref name="options"/> says.</summary>
    /// <exception cref="PlatformNotSupportedException">The process is not Windows x64.</exception>
    // CA2000: the base owns and disposes the SessionOptions built here.
#pragma warning disable CA2000
    public OpenVinoInferenceSession(string modelPath, OpenVinoInferenceSessionOptions options)
        : base(modelPath, BuildSessionOptions(options)) { }
#pragma warning restore CA2000

    /// <summary>Loads a model from <paramref name="modelBytes"/> on OpenVINO's GPU device.</summary>
    /// <exception cref="PlatformNotSupportedException">The process is not Windows x64.</exception>
    public OpenVinoInferenceSession(byte[] modelBytes)
        : this(modelBytes, DefaultOptions) { }

    /// <summary>Loads a model from <paramref name="modelBytes"/> as <paramref name="options"/> says.</summary>
    /// <exception cref="PlatformNotSupportedException">The process is not Windows x64.</exception>
#pragma warning disable CA2000
    public OpenVinoInferenceSession(byte[] modelBytes, OpenVinoInferenceSessionOptions options)
        : base(modelBytes, BuildSessionOptions(options)) { }
#pragma warning restore CA2000

    internal static SessionOptions BuildSessionOptions(OpenVinoInferenceSessionOptions sessionOptions)
    {
        ArgumentNullException.ThrowIfNull(sessionOptions);
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                "FrameFlow.Inference.OpenVino runs on Windows x64 only: Intel's OpenVINO build of ONNX Runtime "
                    + $"ships no native runtime for {RuntimeInformation.RuntimeIdentifier}.");
        }

        if (sessionOptions.CacheDirectory is { } cache)
            Directory.CreateDirectory(cache);

        var options = new SessionOptions();
        try
        {
            foreach (var (key, value) in OpenVinoProviderOptions.SessionEntries(sessionOptions))
                options.AddSessionConfigEntry(key, value);
            options.AppendExecutionProvider(
                OpenVinoProviderOptions.ProviderName,
                new Dictionary<string, string>(OpenVinoProviderOptions.For(sessionOptions)));
            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }
}
