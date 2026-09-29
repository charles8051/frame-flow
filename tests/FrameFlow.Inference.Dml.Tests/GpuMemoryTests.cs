using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>
/// Reading this process's GPU memory budget and usage from DXGI, and the snapshots a DirectML session
/// logs and publishes around its open (#503).
/// </summary>
public sealed class GpuMemoryTests
{
    private static readonly byte[] Model = OnnxModel.Negate("batch", 3, "height", "width");

    [WindowsFact]
    public void TheDefaultAdapter_IsTheFirstDxgiEnumerates()
    {
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(debug: false);
        factory.EnumAdapters1(0, out IDXGIAdapter1 first).CheckError();
        using (first)
        {
            var snapshot = GpuMemory.ReadDefaultAdapter();

            Assert.Equal((ulong)(long)first.Description1.Luid, snapshot.AdapterLuid);
            Assert.True(snapshot.Local.BudgetBytes > 0 || snapshot.NonLocal.BudgetBytes > 0, "The adapter reports no budget.");
        }
    }

    [WindowsFact]
    public void AnAdapter_IsFoundByItsLuid()
    {
        using var device = WarpDevice();
        ulong luid = (ulong)device.AdapterLuid;

        var snapshot = GpuMemory.ReadAdapter(luid);

        Assert.Equal(luid, snapshot.AdapterLuid);
        Assert.True(snapshot.Local.BudgetBytes > 0 || snapshot.NonLocal.BudgetBytes > 0, "The adapter reports no budget.");
    }

    [WindowsFact]
    public void ALuidNoAdapterHas_IsRefused()
    {
        var error = Assert.Throws<COMException>(() => GpuMemory.ReadAdapter(ulong.MaxValue));

        Assert.Contains("EnumAdapterByLuid", error.Message);
    }

    [WindowsFact]
    public void ADevice_LeadsToItsAdapter()
    {
        using var device = WarpDevice();

        Assert.Equal((ulong)device.AdapterLuid, GpuMemory.AdapterLuidOf(device.NativePointer));
    }

    [Fact]
    public void NoDevice_IsRefused() =>
        Assert.Throws<ArgumentNullException>(() => GpuMemory.AdapterLuidOf(0));

    [RequiresDirectMLFact]
    public void ASession_LogsTheDefaultAdaptersMemoryBeforeAndAfterItOpens()
    {
        var logger = new RecordingLogger();
        string adapter = GpuMemory.ReadDefaultAdapter().AdapterLuid.ToString(CultureInfo.InvariantCulture);

        using var session = new DmlInferenceSession(Model, logger);

        AssertLoggedAroundTheOpen(logger, adapter);
    }

    [RequiresDirectMLFact]
    public void ADeviceBoundSession_LogsItsDevicesAdapter()
    {
        var logger = new RecordingLogger();
        using var device = WarpDevice();
        using var queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Compute));

        using var session = DmlInferenceSession.OnDevice(Model, device.NativePointer, queue.NativePointer, logger);

        AssertLoggedAroundTheOpen(logger, ((ulong)device.AdapterLuid).ToString(CultureInfo.InvariantCulture));
    }

    [RequiresDirectMLFact]
    public void TheMeter_PublishesUsageAndBudgetForTheAdapterASessionOpenedOn()
    {
        using var device = WarpDevice();
        using var queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Compute));
        string adapter = ((ulong)device.AdapterLuid).ToString(CultureInfo.InvariantCulture);
        using var session = DmlInferenceSession.OnDevice(Model, device.NativePointer, queue.NativePointer);

        var measured = new List<(string Instrument, string? Adapter, string? Segment, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "FrameFlow.Inference.Dml")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var byKey = tags.ToArray().ToDictionary(t => t.Key, t => t.Value as string);
            measured.Add((instrument.Name, byKey.GetValueOrDefault("adapter"), byKey.GetValueOrDefault("segment"), value));
        });
        listener.Start();
        listener.RecordObservableInstruments();

        var ours = measured.Where(m => m.Adapter == adapter).ToList();
        Assert.Equal(
            [
                ("frameflow.inference.dml.gpu_memory_budget", "local"),
                ("frameflow.inference.dml.gpu_memory_budget", "non_local"),
                ("frameflow.inference.dml.gpu_memory_usage", "local"),
                ("frameflow.inference.dml.gpu_memory_usage", "non_local"),
            ],
            ours.Select(m => (m.Instrument, m.Segment)).Order());
        Assert.All(ours, m => Assert.True(m.Value >= 0, $"{m.Instrument} {m.Segment} is {m.Value}."));
    }

    private static void AssertLoggedAroundTheOpen(RecordingLogger logger, string adapter)
    {
        var opening = Assert.Single(logger.Entries, e => e.Event == "LogOpening");
        var opened = Assert.Single(logger.Entries, e => e.Event == "LogOpened");

        Assert.Equal(LogLevel.Debug, opening.Level);
        Assert.Equal(LogLevel.Information, opened.Level);
        Assert.True(logger.Entries.IndexOf(opening) < logger.Entries.IndexOf(opened), "The snapshots are out of order.");
        Assert.Contains($"adapter {adapter}.", opening.Message);
        Assert.Contains($"adapter {adapter}.", opened.Message);
        Assert.Contains("MiB local budget", opened.Message);
    }

    private static ID3D12Device WarpDevice()
    {
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(debug: false);
        using var adapter = factory.EnumWarpAdapter<IDXGIAdapter>();
        Vortice.Direct3D12.D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? device).CheckError();
        return device!;
    }
}

/// <summary>Skipped off Windows.</summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "DXGI is Windows only.";
    }
}
