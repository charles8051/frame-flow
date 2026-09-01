// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using FrameFlow.Media;
using FrameFlow.Native.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Native;

/// <summary>
/// Discovers which FFmpeg hardware-decode backends are compiled into the
/// loaded build and which of those actually initialise on this host
/// (ADR-0033, step 1).
/// </summary>
/// <remarks>
/// <para>
/// Runs exactly once during <see cref="FrameFlowBootstrapper.Initialize"/> after
/// the FFmpeg load has been confirmed. The probe enumerates types via
/// <c>av_hwdevice_iterate_types</c> and, for each, attempts
/// <c>av_hwdevice_ctx_create</c> with a default device specifier. The temporary
/// context is unref'd immediately — only the success/failure verdict is kept.
/// </para>
/// <para>
/// Cost on a typical machine: 10–50 ms across all compiled-in backends. Costs
/// rise on systems with multiple GPUs or contested device files; consumers can
/// disable probing via <see cref="FrameFlowNativeOptions.SkipHardwareProbe"/>.
/// </para>
/// <para>
/// <b>Backends are checked before they are attempted.</b> A compiled-in backend
/// whose driver library is not installed is not merely a probe failure — the
/// attempt takes the process down.
/// </para>
/// <para>
/// The mechanism is the FFmpeg build, not FFmpeg's hwdevice code. These builds bind
/// their optional driver dependencies through <c>implib.so</c> lazy-loading stubs,
/// and a stub that cannot <c>dlopen</c> its library aborts rather than returning an
/// error:
/// <c>implib-gen: libva-drm.so.2: failed to load library ... via dlopen</c>.
/// Nothing managed can catch that. It applies to every stubbed driver, which is why
/// the table in <see cref="DriverLibrariesFor"/> covers each backend with a known
/// one rather than only the first that was seen to abort.
/// </para>
/// <para>
/// For a library this matters more than it does for a test run. "Linux host with
/// FFmpeg and no GPU driver" describes most servers, and without the pre-check the
/// consumer's application dies during bootstrap for owning such a machine.
/// </para>
/// </remarks>
internal static partial class HardwareDecodeProbe
{
    /// <summary>
    /// Walks the FFmpeg hwdevice types and returns a populated
    /// <see cref="HardwareDecodeCapabilities"/>. Never throws — failures per
    /// backend are captured in the corresponding
    /// <see cref="HardwareDecodeBackend.DiagnosticMessage"/>.
    /// </summary>
    internal static HardwareDecodeCapabilities Run(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var backends = new List<HardwareDecodeBackend>();
        int type = FFAvUtil.av_hwdevice_iterate_types(FFAvUtil.AvHwDeviceTypeNone);

        while (type != FFAvUtil.AvHwDeviceTypeNone)
        {
            var avName = FFAvUtil.AvHwDeviceGetTypeName(type);
            var kind = ClassifyBackend(type);
            var display = DisplayNameFor(kind);

            // Backends with known driver libraries are pre-checked, because attempting one
            // whose driver is absent aborts the process rather than failing.
            if (FirstMissingDriver(kind) is { } driver)
            {
                var missing =
                    $"Skipped '{avName}': driver library '{driver}' is not loadable on this host, "
                    + "so the backend cannot initialise.";
                LogHwProbeDriverMissing(logger, avName, display, driver);

                backends.Add(
                    new HardwareDecodeBackend(
                        Kind: kind,
                        DisplayName: display,
                        AvDeviceTypeName: avName,
                        Initialized: false,
                        DiagnosticMessage: missing
                    )
                );

                type = FFAvUtil.av_hwdevice_iterate_types(type);
                continue;
            }

            // Attempt to create a temporary device context. NULL device → use the
            // platform default (default GPU, default render node, etc.). We do not
            // pass any options — a more sophisticated probe could enumerate
            // devices per backend, but that is overkill for v1.
            int rc = FFAvUtil.av_hwdevice_ctx_create(
                out nint ctxRef,
                type,
                device: null,
                opts: nint.Zero,
                flags: 0
            );

            if (rc == 0 && ctxRef != nint.Zero)
            {
                FFAvUtil.av_buffer_unref(ref ctxRef);

                LogHwProbeSuccess(logger, avName, display);

                backends.Add(
                    new HardwareDecodeBackend(
                        Kind: kind,
                        DisplayName: display,
                        AvDeviceTypeName: avName,
                        Initialized: true,
                        DiagnosticMessage: null
                    )
                );
            }
            else
            {
                var diagnostic = $"av_hwdevice_ctx_create returned {rc} for type '{avName}'.";
                LogHwProbeFailure(logger, avName, display, rc);

                backends.Add(
                    new HardwareDecodeBackend(
                        Kind: kind,
                        DisplayName: display,
                        AvDeviceTypeName: avName,
                        Initialized: false,
                        DiagnosticMessage: diagnostic
                    )
                );
            }

            type = FFAvUtil.av_hwdevice_iterate_types(type);
        }

        return new HardwareDecodeCapabilities(backends);
    }

    /// <summary>
    /// The driver libraries a backend needs before <c>av_hwdevice_ctx_create</c> could
    /// possibly succeed. Empty when the OS supplies the backend and there is nothing
    /// separate to check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Skipping a backend whose driver will not load costs nothing: the create could not
    /// have succeeded either way. What it buys is not aborting while finding that out.
    /// </para>
    /// <para>
    /// <b>Every library a backend needs is listed, not just its headline one.</b> VAAPI is
    /// the reason: <c>libva.so.2</c> is present on hosts that lack <c>libva-drm.so.2</c>,
    /// and it was the latter that aborted. A host carrying the X11 backend but not the DRM
    /// one is therefore skipped when it might have worked — the diagnostic names which
    /// library was missing, and not running is recoverable where aborting is not.
    /// </para>
    /// <para>
    /// Windows and macOS backends that the OS integrates (D3D11VA, DXVA2, VideoToolbox)
    /// have nothing separate to load, so they keep the plain attempt-and-report path.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> DriverLibrariesFor(HardwareDecodeBackendKind kind)
    {
        if (OperatingSystem.IsLinux())
        {
            return kind switch
            {
                HardwareDecodeBackendKind.Cuda => ["libcuda.so.1"],
                HardwareDecodeBackendKind.VaApi => ["libva.so.2", "libva-drm.so.2"],
                HardwareDecodeBackendKind.Vdpau => ["libvdpau.so.1"],
                HardwareDecodeBackendKind.Vulkan => ["libvulkan.so.1"],
                HardwareDecodeBackendKind.OpenCl => ["libOpenCL.so.1"],
                HardwareDecodeBackendKind.Drm => ["libdrm.so.2"],
                _ => [],
            };
        }

        if (OperatingSystem.IsWindows())
        {
            return kind switch
            {
                HardwareDecodeBackendKind.Cuda => ["nvcuda.dll"],
                HardwareDecodeBackendKind.Vulkan => ["vulkan-1.dll"],
                _ => [],
            };
        }

        // macOS: VideoToolbox is part of the OS, and CUDA has not existed since 10.14.
        return [];
    }

    /// <summary>
    /// The first library <paramref name="kind"/> needs that will not load here, or
    /// <see langword="null"/> if they all load (or it needs none).
    /// </summary>
    /// <remarks>
    /// Each handle is released immediately — this asks a question, it does not take a
    /// dependency. FFmpeg loads its own afterwards, and <c>dlopen</c> is reference counted,
    /// so the probe and FFmpeg do not interfere.
    /// </remarks>
    private static string? FirstMissingDriver(HardwareDecodeBackendKind kind)
    {
        foreach (var library in DriverLibrariesFor(kind))
        {
            if (!NativeLibrary.TryLoad(library, out var handle))
                return library;

            NativeLibrary.Free(handle);
        }

        return null;
    }

    /// <summary>
    /// Maps an FFmpeg <c>AVHWDeviceType</c> integer onto FrameFlow's
    /// <see cref="HardwareDecodeBackendKind"/>. Unknown values fall through to
    /// <see cref="HardwareDecodeBackendKind.Other"/> so the probe stays
    /// forward-compatible with new FFmpeg releases.
    /// </summary>
    internal static HardwareDecodeBackendKind ClassifyBackend(int avHwDeviceType) =>
        avHwDeviceType switch
        {
            FFAvUtil.AvHwDeviceTypeCuda => HardwareDecodeBackendKind.Cuda,
            FFAvUtil.AvHwDeviceTypeVaApi => HardwareDecodeBackendKind.VaApi,
            FFAvUtil.AvHwDeviceTypeD3D11Va => HardwareDecodeBackendKind.D3D11Va,
            FFAvUtil.AvHwDeviceTypeDxva2 => HardwareDecodeBackendKind.Dxva2,
            FFAvUtil.AvHwDeviceTypeVideoToolbox => HardwareDecodeBackendKind.VideoToolbox,
            FFAvUtil.AvHwDeviceTypeQsv => HardwareDecodeBackendKind.Qsv,
            FFAvUtil.AvHwDeviceTypeMediaCodec => HardwareDecodeBackendKind.MediaCodec,
            FFAvUtil.AvHwDeviceTypeVulkan => HardwareDecodeBackendKind.Vulkan,
            FFAvUtil.AvHwDeviceTypeDrm => HardwareDecodeBackendKind.Drm,
            FFAvUtil.AvHwDeviceTypeVdpau => HardwareDecodeBackendKind.Vdpau,
            FFAvUtil.AvHwDeviceTypeD3D12Va => HardwareDecodeBackendKind.D3D12Va,
            FFAvUtil.AvHwDeviceTypeOpenCl => HardwareDecodeBackendKind.OpenCl,
            _ => HardwareDecodeBackendKind.Other,
        };

    /// <summary>
    /// Returns the inverse mapping: from a FrameFlow
    /// <see cref="HardwareDecodeBackendKind"/> back to the FFmpeg
    /// <c>AVHWDeviceType</c> integer. Returns
    /// <see cref="FFAvUtil.AvHwDeviceTypeNone"/> for
    /// <see cref="HardwareDecodeBackendKind.Other"/> because there is no
    /// well-defined inverse.
    /// </summary>
    internal static int ToAvHwDeviceType(HardwareDecodeBackendKind kind) =>
        kind switch
        {
            HardwareDecodeBackendKind.Cuda => FFAvUtil.AvHwDeviceTypeCuda,
            HardwareDecodeBackendKind.VaApi => FFAvUtil.AvHwDeviceTypeVaApi,
            HardwareDecodeBackendKind.D3D11Va => FFAvUtil.AvHwDeviceTypeD3D11Va,
            HardwareDecodeBackendKind.Dxva2 => FFAvUtil.AvHwDeviceTypeDxva2,
            HardwareDecodeBackendKind.VideoToolbox => FFAvUtil.AvHwDeviceTypeVideoToolbox,
            HardwareDecodeBackendKind.Qsv => FFAvUtil.AvHwDeviceTypeQsv,
            HardwareDecodeBackendKind.MediaCodec => FFAvUtil.AvHwDeviceTypeMediaCodec,
            HardwareDecodeBackendKind.Vulkan => FFAvUtil.AvHwDeviceTypeVulkan,
            HardwareDecodeBackendKind.Drm => FFAvUtil.AvHwDeviceTypeDrm,
            HardwareDecodeBackendKind.Vdpau => FFAvUtil.AvHwDeviceTypeVdpau,
            HardwareDecodeBackendKind.D3D12Va => FFAvUtil.AvHwDeviceTypeD3D12Va,
            HardwareDecodeBackendKind.OpenCl => FFAvUtil.AvHwDeviceTypeOpenCl,
            _ => FFAvUtil.AvHwDeviceTypeNone,
        };

    private static string DisplayNameFor(HardwareDecodeBackendKind kind) =>
        kind switch
        {
            HardwareDecodeBackendKind.Cuda => "NVIDIA CUDA (NVDEC)",
            HardwareDecodeBackendKind.VaApi => "VA-API",
            HardwareDecodeBackendKind.D3D11Va => "Direct3D 11 Video Acceleration",
            HardwareDecodeBackendKind.Dxva2 => "DirectX Video Acceleration 2",
            HardwareDecodeBackendKind.VideoToolbox => "Apple VideoToolbox",
            HardwareDecodeBackendKind.Qsv => "Intel Quick Sync Video",
            HardwareDecodeBackendKind.MediaCodec => "Android MediaCodec",
            HardwareDecodeBackendKind.Vulkan => "Vulkan Video",
            HardwareDecodeBackendKind.Drm => "Linux DRM",
            HardwareDecodeBackendKind.Vdpau => "NVIDIA VDPAU",
            HardwareDecodeBackendKind.D3D12Va => "Direct3D 12 Video Acceleration",
            HardwareDecodeBackendKind.OpenCl => "OpenCL",
            _ => "Other hardware backend",
        };

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Hardware decode probe: {AvName} ({DisplayName}) initialized successfully."
    )]
    private static partial void LogHwProbeSuccess(
        ILogger logger,
        string avName,
        string displayName
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Hardware decode probe: {AvName} ({DisplayName}) failed to initialize (code {ReturnCode})."
    )]
    private static partial void LogHwProbeFailure(
        ILogger logger,
        string avName,
        string displayName,
        int returnCode
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Hardware decode probe: {AvName} ({DisplayName}) skipped — driver library '{Driver}' is not loadable on this host."
    )]
    private static partial void LogHwProbeDriverMissing(
        ILogger logger,
        string avName,
        string displayName,
        string driver
    );
}
