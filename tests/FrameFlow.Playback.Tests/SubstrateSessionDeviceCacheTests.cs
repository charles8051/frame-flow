// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding.Internal;
using FrameFlow.Native.Interop;
using FrameFlow.Playback.Core;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// What an item's fault does to the player's shared hardware devices (#759): the session evicts
/// the device its decoder borrowed only when the GPU lost it, and only that device, never whichever
/// the cache holds for the backend by then. Over <see cref="ItemRuntimeRig"/>, with devices FFmpeg
/// allocated and nothing opened, so no GPU is needed. Every loss is fed in.
/// </summary>
/// <remarks>
/// The session evicts before it reports the fault, on the same thread, so once the rig has seen
/// the fault the eviction has happened or never will.
/// </remarks>
public sealed class SubstrateSessionDeviceCacheTests
{
    private const int DeviceReset = unchecked((int)0x887A0007);

    // AVERROR_INVALIDDATA, which avcodec_send_packet returns for a corrupt packet.
    private const int InvalidData = unchecked((int)0xBEBBB1B7);

    private static readonly TimeSpan Bound = ItemRuntimeRig.Bound;

    [RequiresFfmpegFact]
    public async Task ACorruptPacketFault_KeepsTheCachedDevice()
    {
        using var cache = UnopenedDevices.Cache();
        var bound = Lend(cache);
        await using var rig = ItemRuntimeRig.Create(deviceCache: cache);
        rig.Media.BoundDevice = bound;
        await rig.PlayingAsync();
        var corrupt = DecodeDriver.Fault("avcodec_send_packet", InvalidData, MediaFailureStage.SendPacket);

        rig.Media.Latest(ItemWork.Graph).Fault(corrupt);

        Assert.Same(corrupt, await rig.WorkerFault.WaitAsync(Bound));
        Assert.Same(bound, Lend(cache));
    }

    [RequiresFfmpegFact]
    public async Task AFaultOnALostDevice_EvictsIt()
    {
        using var cache = UnopenedDevices.Cache();
        var bound = Lend(cache);
        await using var rig = ItemRuntimeRig.Create(deviceCache: cache);
        rig.Media.BoundDevice = bound;
        await rig.PlayingAsync();
        Assert.True(bound.ReportRemovedReason(DeviceReset));

        rig.Media.Latest(ItemWork.Graph).Fault(new DeviceLostException("The GPU device was lost.", DeviceReset));
        await rig.WorkerFault.WaitAsync(Bound);

        // Released at the fault, not left for the next item's borrow to find.
        Assert.Throws<ObjectDisposedException>(() => bound.Borrow());
    }

    /// <summary>
    /// The old runtime's device is lost and the next item's decoder has been lent a new one when
    /// the old runtime's fault arrives, as one prepared ahead, or ending behind the next, reports
    /// it.
    /// </summary>
    [RequiresFfmpegFact]
    public async Task ALateFaultFromAnOldRuntime_LeavesTheNewerDevice()
    {
        using var cache = UnopenedDevices.Cache();
        var old = Lend(cache);
        await using var rig = ItemRuntimeRig.Create(deviceCache: cache);
        rig.Media.BoundDevice = old;
        await rig.PlayingAsync();
        Assert.True(old.ReportRemovedReason(DeviceReset));
        var newer = Lend(cache);
        Assert.NotSame(old, newer);

        rig.Media.Latest(ItemWork.Graph).Fault(new DeviceLostException("The GPU device was lost.", DeviceReset));
        await rig.WorkerFault.WaitAsync(Bound);

        Assert.Same(newer, Lend(cache));
    }

    // The device the cache lends a decoder binding the backend now. The reference the decoder would
    // own is released at once.
    private static HardwareDevice Lend(HardwareDeviceCache cache)
    {
        nint reference = cache.Borrow(UnopenedDevices.Backend, out var owner);
        Assert.NotEqual(nint.Zero, reference);
        FFAvUtil.av_buffer_unref(ref reference);
        return Assert.IsType<HardwareDevice>(owner);
    }
}

/// <summary>
/// Device caches whose devices FFmpeg allocated for the platform's first backend and nothing opened
/// (<see cref="HardwareDevice.Unopened"/>): they touch no hardware.
/// </summary>
internal static class UnopenedDevices
{
    private static readonly Lazy<bool> Loadable = new(() =>
    {
        try
        {
            HardwareDevice.Unopened(Backend).Dispose();
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    });

    /// <summary>The platform's first backend, which its FFmpeg build always has a device type for.</summary>
    public static HardwareDecodeBackendKind Backend { get; } =
        OperatingSystem.IsWindows() ? HardwareDecodeBackendKind.D3D11Va
        : OperatingSystem.IsMacOS() ? HardwareDecodeBackendKind.VideoToolbox
        : HardwareDecodeBackendKind.VaApi;

    /// <summary>Whether FFmpeg's libraries load here.</summary>
    public static bool Available => Loadable.Value;

    public static HardwareDeviceCache Cache() => new(logger: null, HardwareDevice.Unopened);
}

/// <summary>Skipped when FFmpeg's libraries do not load here: the test holds real device contexts.</summary>
internal sealed class RequiresFfmpegFactAttribute : FactAttribute
{
    public RequiresFfmpegFactAttribute()
    {
        if (!UnopenedDevices.Available)
            Skip = "FFmpeg shared libraries not available. Run scripts/fetch-ffmpeg.cs.";
    }
}
