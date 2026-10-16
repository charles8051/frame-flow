// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media.Core;
using SharpGen.Runtime;

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// Shared helpers for recognising GPU device-loss / TDR on the producer side of the
/// composition-interop presenter. A remote-desktop display transition (the
/// 2026-06-12 teardown-deadlock trigger) is <i>not</i> device loss — these helpers exist
/// for the genuine <c>DXGI_ERROR_DEVICE_REMOVED</c> case (step 6 of that investigation),
/// where the right reaction is "drop the keyed-mutex ring and recreate on the next frame"
/// rather than blocking a doomed <c>AcquireSync</c> / <c>VideoProcessorBlt</c> on the UI
/// thread.
/// </summary>
internal static class D3D11DeviceLoss
{
    /// <summary>
    /// <see langword="true"/> if <paramref name="hr"/> is one of the D3D11/DXGI device-loss
    /// result codes: removed, hung, reset, or a driver internal error (#530). The list is
    /// <see cref="DeviceLoss"/>, which the decoder and the inference factory read too.
    /// </summary>
    public static bool IsDeviceLostCode(int hr) => DeviceLoss.IsDeviceLostCode(hr);

    /// <summary>
    /// <see langword="true"/> if <paramref name="ex"/> carries a D3D11/DXGI device-loss
    /// HRESULT. Vortice surfaces native failures as <see cref="SharpGenException"/>; we
    /// extract the underlying <see cref="Result"/> (this also recognises a device-loss
    /// HRESULT wrapped in any other exception type via
    /// <see cref="Result.GetResultFromException"/>).
    /// </summary>
    public static bool IsDeviceLost(Exception ex) =>
        IsDeviceLostCode(Result.GetResultFromException(ex).Code);
}
