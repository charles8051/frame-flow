// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media.Core;

/// <summary>
/// The HRESULTs that mean a GPU device was removed or reset (#759, #530). Pure. Every check in
/// FrameFlow that asks whether a failure was device loss reads this one list: the decoder's, the
/// presenter's and the inference factory's.
/// </summary>
/// <remarks>
/// <para>
/// These are the values <c>GetDeviceRemovedReason</c> returns for a lost device, plus
/// <c>D3DDDIERR_DEVICEREMOVED</c>, which a D3D9-era runtime reports for the same event. A device
/// that reports one stays lost: every later call on it fails, and the only recovery is a new
/// device.
/// </para>
/// <para>
/// <c>DXGI_ERROR_INVALID_CALL</c> (<c>887A0001</c>) is left out. <c>GetDeviceRemovedReason</c>
/// can return it too, but it says the application made a call it should not have, not that the
/// driver failed.
/// </para>
/// </remarks>
internal static class DeviceLoss
{
    /// <summary><c>DXGI_ERROR_DEVICE_REMOVED</c>: the device was physically removed or the driver upgraded.</summary>
    internal const int DeviceRemoved = unchecked((int)0x887A0005);

    /// <summary><c>DXGI_ERROR_DEVICE_HUNG</c>: the device stopped responding to commands.</summary>
    internal const int DeviceHung = unchecked((int)0x887A0006);

    /// <summary><c>DXGI_ERROR_DEVICE_RESET</c>: the device failed and was reset (a TDR).</summary>
    internal const int DeviceReset = unchecked((int)0x887A0007);

    /// <summary><c>DXGI_ERROR_DRIVER_INTERNAL_ERROR</c>: the driver failed and put the device in the removed state.</summary>
    internal const int DriverInternalError = unchecked((int)0x887A0020);

    /// <summary><c>D3DDDIERR_DEVICEREMOVED</c>: the device was removed, as the D3D9-era runtime reports it.</summary>
    internal const int DdiDeviceRemoved = unchecked((int)0x88760870);

    /// <summary>Every code <see cref="IsDeviceLostCode"/> accepts.</summary>
    internal static IReadOnlyList<int> Codes { get; } =
        [DeviceRemoved, DeviceHung, DeviceReset, DriverInternalError, DdiDeviceRemoved];

    /// <summary>Whether <paramref name="hresult"/> means the GPU device was removed or reset.</summary>
    public static bool IsDeviceLostCode(int hresult) =>
        hresult
            is DeviceRemoved
                or DeviceHung
                or DeviceReset
                or DriverInternalError
                or DdiDeviceRemoved;
}

/// <summary>
/// Whether a shared GPU device has been reported lost, and with which reason. Pure: the owner of
/// the device advances it with each reason it reads, and raises its event when
/// <see cref="Observe"/> says to. It says so once for the life of the device.
/// </summary>
/// <param name="Lost">Whether a lost reason has been observed.</param>
/// <param name="Reason">The first lost reason observed, or 0 while <see cref="Lost"/> is false.</param>
internal readonly record struct DeviceLossLatch(bool Lost, int Reason)
{
    /// <summary>A device not yet seen lost.</summary>
    public static DeviceLossLatch Live => default;

    /// <summary>
    /// The latch after the device reports <paramref name="reason"/>, and whether this is the
    /// report that should raise the owner's event.
    /// </summary>
    /// <param name="reason">
    /// What <c>GetDeviceRemovedReason</c> returned. <c>S_OK</c> and any code that is not device
    /// loss leave the latch as it was.
    /// </param>
    /// <remarks>
    /// A removed device does not come back, so once lost the latch stays lost and keeps the first
    /// reason. A later report, of the same reason or another, raises nothing.
    /// </remarks>
    public (DeviceLossLatch Next, bool Raise) Observe(int reason)
    {
        if (Lost || !DeviceLoss.IsDeviceLostCode(reason))
            return (this, false);
        return (new DeviceLossLatch(true, reason), true);
    }
}
