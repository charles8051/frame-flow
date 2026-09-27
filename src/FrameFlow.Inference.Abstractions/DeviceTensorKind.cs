// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>The GPU API a <see cref="DeviceTensor"/>'s handles belong to.</summary>
public enum DeviceTensorKind
{
    /// <summary>
    /// Direct3D 12. The buffer is an <c>ID3D12Resource*</c>, the device an <c>ID3D12Device*</c>, and
    /// the ready fence an <c>ID3D12Fence*</c>.
    /// </summary>
    D3D12 = 1,
}
