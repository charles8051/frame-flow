// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

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
    // DXGI device-loss HRESULTs (stable Win32 ABI values; Vortice's ResultCode spelling
    // varies by version, the values do not). Matched as the unsigned 0x887A000x family.
    private const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
    private const int DXGI_ERROR_DEVICE_HUNG = unchecked((int)0x887A0006);
    private const int DXGI_ERROR_DEVICE_RESET = unchecked((int)0x887A0007);
    private const int D3DDDIERR_DEVICEREMOVED = unchecked((int)0x88760870);

    /// <summary>
    /// <see langword="true"/> if <paramref name="hr"/> is one of the D3D11/DXGI
    /// device-loss result codes (removed / hung / reset).
    /// </summary>
    public static bool IsDeviceLostCode(int hr) =>
        hr == DXGI_ERROR_DEVICE_REMOVED
        || hr == DXGI_ERROR_DEVICE_HUNG
        || hr == DXGI_ERROR_DEVICE_RESET
        || hr == D3DDDIERR_DEVICEREMOVED;

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
