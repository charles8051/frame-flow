// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;
using CatalogProvider = Microsoft.Windows.AI.MachineLearning.ExecutionProvider;

namespace FrameFlow.Inference.WinML;

/// <summary>
/// The execution providers Windows ML makes available to ONNX Runtime in this process.
/// </summary>
/// <remarks>
/// A session sees only the CPU and DirectML providers until the installed vendor providers are
/// registered. Register once per process, before opening sessions.
/// </remarks>
public static class WinMLProviders
{
    private static readonly object Gate = new();
    private static Task<IReadOnlyList<string>>? _registration;

    /// <summary>
    /// Registers the providers Windows ML has already installed on this machine, once per process.
    /// Downloads and installs nothing. Returns the names registered.
    /// </summary>
    /// <remarks>
    /// The vendor catalog needs Windows 11 24H2 (build 26100) or later. Below that, nothing is
    /// registered and sessions keep the CPU and DirectML providers.
    /// </remarks>
    public static Task<IReadOnlyList<string>> RegisterInstalledAsync()
    {
        lock (Gate)
            return _registration ??= RegisterAsync(install: false, progress: null);
    }

    /// <summary>
    /// Downloads and installs, system-wide, the certified providers Windows ML offers for this
    /// machine's hardware, then registers them. Returns the names registered.
    /// </summary>
    /// <remarks>
    /// Installing changes the machine, not only the process, and is not transactional: providers
    /// installed before a failure stay installed. Prefer <see cref="RegisterInstalledAsync"/> in an
    /// app, and install from a setup step the user has agreed to.
    /// </remarks>
    /// <param name="progress">Receives the install's progress, 0 to 100.</param>
    public static Task<IReadOnlyList<string>> InstallAndRegisterAsync(IProgress<double>? progress = null)
    {
        var task = RegisterAsync(install: true, progress);
        lock (Gate)
            _registration = task;
        return task;
    }

    /// <summary>The providers ONNX Runtime can use now, one entry per provider and device.</summary>
    public static IReadOnlyList<WinMLProvider> Available()
    {
        WinMLRuntime.EnsureLoaded();
        return [.. OrtEnv.Instance().GetEpDevices().Select(ToProvider)];
    }

    /// <summary>The ONNX Runtime devices named <paramref name="name"/>, on the adapter <paramref name="adapterLuid"/> when given.</summary>
    internal static IReadOnlyList<OrtEpDevice> Devices(string name, ulong? adapterLuid)
    {
        WinMLRuntime.EnsureLoaded();
        return [.. OrtEnv.Instance().GetEpDevices()
            .Where(d => d.EpName == name && (adapterLuid is null || AdapterLuidOf(d) == adapterLuid))];
    }

    /// <summary>The adapter LUID in a device's metadata, as ONNX Runtime writes it: a decimal string.</summary>
    internal static ulong? ParseLuid(string? value) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong luid) ? luid : null;

    private static async Task<IReadOnlyList<string>> RegisterAsync(bool install, IProgress<double>? progress)
    {
        WinMLRuntime.EnsureLoaded();
        var catalog = ExecutionProviderCatalog.GetDefault();
        var operation = install ? catalog.EnsureAndRegisterCertifiedAsync() : catalog.RegisterCertifiedAsync();
        if (progress is not null)
            operation.Progress = (_, percent) => progress.Report(percent);
        IList<CatalogProvider> registered = await operation;
        return [.. registered.Select(p => p.Name)];
    }

    private static WinMLProvider ToProvider(OrtEpDevice device) =>
        new(
            device.EpName,
            device.EpVendor,
            device.HardwareDevice.Type switch
            {
                OrtHardwareDeviceType.GPU => WinMLDeviceType.Gpu,
                OrtHardwareDeviceType.NPU => WinMLDeviceType.Npu,
                _ => WinMLDeviceType.Cpu,
            },
            AdapterLuidOf(device));

    private static ulong? AdapterLuidOf(OrtEpDevice device) =>
        device.HardwareDevice.Metadata.Entries.TryGetValue("LUID", out var luid) ? ParseLuid(luid) : null;
}
