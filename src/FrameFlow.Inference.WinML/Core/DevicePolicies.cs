// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.WinML.Core;

/// <summary>Maps FrameFlow's device policy to ONNX Runtime's. Pure.</summary>
internal static class DevicePolicies
{
    public static ExecutionProviderDevicePolicy ToOrt(WinMLDevicePolicy policy) => policy switch
    {
        WinMLDevicePolicy.PreferGpu => ExecutionProviderDevicePolicy.PREFER_GPU,
        WinMLDevicePolicy.PreferNpu => ExecutionProviderDevicePolicy.PREFER_NPU,
        WinMLDevicePolicy.PreferCpu => ExecutionProviderDevicePolicy.PREFER_CPU,
        WinMLDevicePolicy.MaxPerformance => ExecutionProviderDevicePolicy.MAX_PERFORMANCE,
        WinMLDevicePolicy.MaxEfficiency => ExecutionProviderDevicePolicy.MAX_EFFICIENCY,
        WinMLDevicePolicy.MinOverallPower => ExecutionProviderDevicePolicy.MIN_OVERALL_POWER,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Undefined device policy."),
    };
}
