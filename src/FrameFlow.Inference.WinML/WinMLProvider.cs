// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.WinML;

/// <summary>The kind of device a provider runs on.</summary>
public enum WinMLDeviceType
{
    /// <summary>The CPU.</summary>
    Cpu,

    /// <summary>A GPU.</summary>
    Gpu,

    /// <summary>An NPU.</summary>
    Npu,
}

/// <summary>An execution provider ONNX Runtime can use now, on one device.</summary>
/// <param name="Name">The provider's name, such as <c>NvTensorRTRTXExecutionProvider</c>, as <c>OnProvider</c> takes it.</param>
/// <param name="Vendor">Who ships the provider.</param>
/// <param name="DeviceType">The kind of device it runs on.</param>
/// <param name="AdapterLuid">
/// The DXGI adapter LUID of a GPU it runs on, when the device reports one: what matches a provider to
/// a <c>HardwareDevice</c>'s adapter.
/// </param>
public sealed record WinMLProvider(string Name, string Vendor, WinMLDeviceType DeviceType, ulong? AdapterLuid);
