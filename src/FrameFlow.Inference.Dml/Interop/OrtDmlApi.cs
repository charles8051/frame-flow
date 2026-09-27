// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.Dml.Interop;

/// <summary>
/// ONNX Runtime's <c>OrtDmlApi</c>, which the managed binding does not expose: building the
/// DirectML provider on a caller's device and queue, and wrapping a D3D12 buffer as a DirectML
/// allocation ORT can bind.
/// </summary>
/// <remarks>
/// Reached through the C API: <c>OrtGetApiBase</c>, then <c>OrtApi::GetExecutionProviderApi</c>.
/// The C API's tables only grow, so the slots below hold for every version from
/// <see cref="MinimumApiVersion"/> on. The version asked for is the loaded runtime's own, which it
/// always serves.
/// </remarks>
internal static unsafe class OrtDmlApi
{
    /// <summary>The oldest runtime this has run against: the last DirectML release, 1.24.</summary>
    internal const uint MinimumApiVersion = 24;

    // OrtApi slots.
    private const int GetErrorMessageSlot = 2;
    private const int ReleaseStatusSlot = 93;
    private const int GetExecutionProviderApiSlot = 195;

    // OrtDmlApi slots.
    private const int AppendDml1Slot = 1;
    private const int CreateAllocationSlot = 2;
    private const int FreeAllocationSlot = 3;

    private static readonly Lazy<(nint Api, nint Dml)> Tables = new(Load);

    [DllImport("onnxruntime", ExactSpelling = true)]
    private static extern nint OrtGetApiBase();

    /// <summary>
    /// Appends the DirectML provider to <paramref name="options"/>, running on
    /// <paramref name="dmlDevice"/> (an <c>IDMLDevice*</c>) and <paramref name="commandQueue"/>
    /// (an <c>ID3D12CommandQueue*</c>). The provider takes its own references to both.
    /// </summary>
    public static void AppendDirectML(SessionOptions options, nint dmlDevice, nint commandQueue)
    {
        var append = (delegate* unmanaged<nint, nint, nint, nint>)Dml[AppendDml1Slot];
        Check(append(options.DangerousGetHandle(), dmlDevice, commandQueue));
    }

    /// <summary>Wraps <paramref name="resource"/> (an <c>ID3D12Resource*</c>) as a DirectML allocation.</summary>
    public static nint CreateAllocation(nint resource)
    {
        nint allocation = 0;
        Check(((delegate* unmanaged<nint, nint*, nint>)Dml[CreateAllocationSlot])(resource, &allocation));
        return allocation;
    }

    /// <summary>Frees an allocation from <see cref="CreateAllocation"/>. The buffer is untouched.</summary>
    public static void FreeAllocation(nint allocation) =>
        Check(((delegate* unmanaged<nint, nint>)Dml[FreeAllocationSlot])(allocation));

    /// <summary>
    /// The C API version to ask a runtime for: its own minor version. Refuses a runtime older than
    /// <see cref="MinimumApiVersion"/> or a version string it cannot read.
    /// </summary>
    internal static uint ApiVersionOf(string runtimeVersion)
    {
        string[] parts = runtimeVersion.Split('.');
        if (parts.Length < 2
            || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint major)
            || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint minor)
            || major != 1)
        {
            throw new NotSupportedException(
                $"ONNX Runtime reports version '{runtimeVersion}', which is not a 1.x version this can read.");
        }

        if (minor < MinimumApiVersion)
        {
            throw new NotSupportedException(
                $"ONNX Runtime {runtimeVersion} is older than 1.{MinimumApiVersion}, the oldest the device-bound "
                    + "DirectML session supports.");
        }

        return minor;
    }

    private static nint* Api => (nint*)Tables.Value.Api;

    private static nint* Dml => (nint*)Tables.Value.Dml;

    private static (nint Api, nint Dml) Load()
    {
        nint* apiBase = (nint*)OrtGetApiBase();
        var getApi = (delegate* unmanaged<uint, nint*>)apiBase[0];
        var getVersion = (delegate* unmanaged<nint>)apiBase[1];
        string runtimeVersion = Marshal.PtrToStringUTF8(getVersion()) ?? "";
        uint version = ApiVersionOf(runtimeVersion);

        nint* api = getApi(version);
        if (api is null)
            throw new NotSupportedException($"ONNX Runtime {runtimeVersion} does not serve C API version {version}.");

        var getProviderApi = (delegate* unmanaged<byte*, uint, nint**, nint>)api[GetExecutionProviderApiSlot];
        nint* dml = null;
        fixed (byte* name = "DML\0"u8)
            Check(api, getProviderApi(name, version, &dml));
        if (dml is null)
            throw new NotSupportedException($"ONNX Runtime {runtimeVersion} has no DirectML provider API.");

        return ((nint)api, (nint)dml);
    }

    private static void Check(nint status) => Check(Api, status);

    private static void Check(nint* api, nint status)
    {
        if (status == 0)
            return;

        string message = Marshal.PtrToStringUTF8(((delegate* unmanaged<nint, nint>)api[GetErrorMessageSlot])(status))
            ?? "unknown error";
        ((delegate* unmanaged<nint, void>)api[ReleaseStatusSlot])(status);
        throw new InvalidOperationException($"ONNX Runtime: {message}");
    }
}
