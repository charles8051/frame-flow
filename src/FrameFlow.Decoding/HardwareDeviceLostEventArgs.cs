// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding;

/// <summary>The device loss <see cref="HardwareDevice.DeviceLost"/> reports.</summary>
public sealed class HardwareDeviceLostEventArgs : EventArgs
{
    /// <summary>Creates the arguments for a device that reported <paramref name="removedReason"/>.</summary>
    /// <param name="removedReason">What the device's <c>GetDeviceRemovedReason</c> returned.</param>
    public HardwareDeviceLostEventArgs(int removedReason) => RemovedReason = removedReason;

    /// <summary>
    /// The HRESULT the device reported for its removal, such as <c>DXGI_ERROR_DEVICE_RESET</c>
    /// (<c>0x887A0007</c>). <see cref="FrameFlow.Media.DeviceLostException.RemovedReason"/> on each
    /// player's failure carries the same value.
    /// </summary>
    public int RemovedReason { get; }
}
