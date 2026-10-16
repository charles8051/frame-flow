// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media.Core;

namespace FrameFlow.Media;

/// <summary>
/// A failure caused by the GPU device being removed or reset (#759). It carries the reason the
/// device reported, and a playback error built from it has the category
/// <see cref="ErrorCategory.DeviceLost"/>.
/// </summary>
/// <remarks>
/// <para>
/// A lost device stays lost. Every player decoding on it fails, and so does every later item
/// opened on it. The recovery is a new device: dispose the shared device, create another, and
/// load or replace each player's media on it.
/// </para>
/// <para>
/// The failure that revealed the loss, usually a <see cref="MediaException"/> from the decoder,
/// is the <see cref="Exception.InnerException"/>. It derives from
/// <see cref="InvalidOperationException"/>, as that failure does.
/// </para>
/// </remarks>
public sealed class DeviceLostException : InvalidOperationException
{
    /// <summary>Creates a device-loss failure for the reason the device reported.</summary>
    /// <param name="message">A description of the failure.</param>
    /// <param name="removedReason">
    /// What <c>GetDeviceRemovedReason</c> returned, such as <c>DXGI_ERROR_DEVICE_RESET</c>
    /// (<c>0x887A0007</c>).
    /// </param>
    /// <param name="innerException">The failure that revealed the loss, if any.</param>
    public DeviceLostException(string message, int removedReason, Exception? innerException = null)
        : base(message, innerException)
    {
        RemovedReason = removedReason;
        HResult = removedReason;
    }

    /// <summary>
    /// The HRESULT the device reported for its removal, such as <c>DXGI_ERROR_DEVICE_REMOVED</c>
    /// (<c>0x887A0005</c>), <c>DXGI_ERROR_DEVICE_HUNG</c> (<c>0x887A0006</c>),
    /// <c>DXGI_ERROR_DEVICE_RESET</c> (<c>0x887A0007</c>) or
    /// <c>DXGI_ERROR_DRIVER_INTERNAL_ERROR</c> (<c>0x887A0020</c>).
    /// </summary>
    public int RemovedReason { get; }

    /// <summary>
    /// Whether <paramref name="hresult"/> is one of the codes FrameFlow treats as device loss: the
    /// four above and <c>D3DDDIERR_DEVICEREMOVED</c> (<c>0x88760870</c>).
    /// </summary>
    public static bool IsDeviceLostCode(int hresult) => DeviceLoss.IsDeviceLostCode(hresult);
}
