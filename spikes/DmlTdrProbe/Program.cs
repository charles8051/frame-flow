// DmlTdrProbe — throwaway diagnostic for the "can DirectML recover in-process
// after a Windows GPU TDR?" question.
//
// WHAT THIS ANSWERS
// -----------------
// After a TDR, ORT's AppendExecutionProvider_DML() fails forever in the
// affected process with 887A0006 (DXGI_ERROR_DEVICE_HUNG), thrown from
// dml_provider_factory.cc:524 — which is a plain D3D12CreateDevice() call on a
// FRESHLY created IDXGIFactory4 (created at line 510 of the same function, on
// every call). So "ORT is holding a stale DXGI factory" cannot be the
// explanation. This probe establishes what actually IS poisoned by exercising
// five independent device-creation paths in ONE process:
//
//   A  bare D3D12CreateDevice(default adapter)         — the OS call itself
//   B  DMLCreateDevice on A's device                   — DirectML layer
//   C  ORT AppendExecutionProvider_DML()               — ORT's DXGI path
//   D  ORT AppendExecutionProvider("DML", {...})       — ORT's DXCore path
//   E  caller-owned device via ...Ex_DML (P/Invoke)    — THE HYPOTHESIS
//
// Reading the result:
//   A fails  -> D3D12 device creation is poisoned process-wide. E cannot
//               possibly help, because E has to call A first. Restart is the
//               only recovery. (This is the expected outcome.)
//   A succeeds but C fails -> something in ORT's specific path (DXGI
//               EnumAdapters1 ordering, IsSoftwareAdapter) is the problem, and
//               the caller-owned path E is worth building.
//   C fails but D succeeds -> DXGI enumeration is poisoned but DXCore is not.
//               This would be the cheap win: it needs no interop at all, just
//               a provider-options dictionary.
//
// HOW TO RUN IT
// -------------
// This is a POST-TDR probe, not a TDR provoker. Do not run it on a production
// device. Run it on a dev/test box (or a signage box taken out of service) that
// has just experienced a real TDR — LiveKernelEvent 0x141 in the System log.
// Deliberately do NOT use ID3D12Device5::RemoveDevice to simulate: that only
// poisons the one device object it is called on and recovers cleanly on the
// next create, so it would produce a false "recovery works" result.
//
//   dotnet run --project spikes/DmlTdrProbe -- --repeat 20 --delay 30
//
// Run it once on a healthy box first to confirm all five paths pass. Then run
// it after a TDR and compare.

using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace DmlTdrProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        int repeat = ArgInt(args, "--repeat", 1);
        int delaySeconds = ArgInt(args, "--delay", 0);

        Console.WriteLine($"DmlTdrProbe  pid={Environment.ProcessId}  {DateTimeOffset.UtcNow:O}");
        Console.WriteLine($"ORT native pinned 1.24.4 / managed 1.28.0; repeat={repeat} delay={delaySeconds}s");
        Console.WriteLine();

        for (int round = 1; round <= repeat; round++)
        {
            Console.WriteLine($"--- round {round}/{repeat}  {DateTimeOffset.UtcNow:HH:mm:ss}Z ---");
            RunRound();
            Console.WriteLine();

            if (round < repeat && delaySeconds > 0)
                Thread.Sleep(TimeSpan.FromSeconds(delaySeconds));
        }

        return 0;
    }

    private static void RunRound()
    {
        nint d3d12Device = 0;
        nint dmlDevice = 0;
        nint commandQueue = 0;

        try
        {
            // A — the bare OS call. If this fails, nothing downstream can work.
            int hr = Native.D3D12CreateDevice(0, Native.FeatureLevel11_0, Native.IidId3d12Device, out d3d12Device);
            Report("A  D3D12CreateDevice(default adapter)", hr);
            if (hr < 0) return;

            // B — DirectML on top of A.
            hr = Native.DMLCreateDevice1(
                d3d12Device, Native.DmlCreateDeviceFlagNone, Native.DmlFeatureLevel5_0, Native.IidIdmlDevice, out dmlDevice);
            Report("B  DMLCreateDevice1(FL 5_0)", hr);

            // C — ORT's default path: fresh IDXGIFactory4 -> EnumAdapters1(0) ->
            //     D3D12CreateDevice. This is the one that fails in production.
            ReportOrt("C  ORT AppendExecutionProvider_DML()", static o => o.AppendExecutionProvider_DML());

            // D — ORT's DXCore path. Reached by supplying performance_preference
            //     and/or device_filter with NO device_id, which routes through
            //     CreateFromProviderOptions -> CreateFromDeviceOptions ->
            //     DXCoreCreateAdapterFactory instead of DXGI. Zero interop.
            ReportOrt("D  ORT AppendExecutionProvider(\"DML\", perf=high_performance)", static o =>
                o.AppendExecutionProvider("DML", new Dictionary<string, string>
                {
                    ["performance_preference"] = "high_performance",
                }));

            // E — the hypothesis under test: hand ORT a device WE created.
            if (dmlDevice != 0)
            {
                hr = Native.CreateCommandQueue(d3d12Device, out commandQueue);
                Report("E1 ID3D12Device::CreateCommandQueue", hr);

                if (hr >= 0)
                    ReportEx("E2 ORT ...AppendExecutionProviderEx_DML(caller-owned)", dmlDevice, commandQueue);
            }
        }
        finally
        {
            Native.SafeRelease(ref commandQueue);
            Native.SafeRelease(ref dmlDevice);
            Native.SafeRelease(ref d3d12Device);
        }
    }

    private static void ReportOrt(string label, Action<SessionOptions> configure)
    {
        SessionOptions? options = null;
        try
        {
            options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
                EnableMemoryPattern = false,
            };
            configure(options);
            Console.WriteLine($"  PASS  {label}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {label}");
            Console.WriteLine($"        {ex.GetType().Name}: {Squash(ex.Message)}");
        }
        finally
        {
            options?.Dispose();
        }
    }

    private static void ReportEx(string label, nint dmlDevice, nint commandQueue)
    {
        SessionOptions? options = null;
        try
        {
            options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
                EnableMemoryPattern = false,
            };

            // Returns OrtStatus* — null on success. We deliberately do not
            // decode the status (that needs the OrtApi struct); non-null is
            // enough to answer the question, and this is a throwaway probe.
            nint status = Native.OrtSessionOptionsAppendExecutionProviderEx_DML(
                options.DangerousGetHandle(), dmlDevice, commandQueue);

            Console.WriteLine(status == 0
                ? $"  PASS  {label}"
                : $"  FAIL  {label} (non-null OrtStatus*)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {label}");
            Console.WriteLine($"        {ex.GetType().Name}: {Squash(ex.Message)}");
        }
        finally
        {
            options?.Dispose();
        }
    }

    private static void Report(string label, int hr) =>
        Console.WriteLine(hr >= 0
            ? $"  PASS  {label}"
            : $"  FAIL  {label}  hr=0x{hr:X8}{Annotate(hr)}");

    private static string Annotate(int hr) => unchecked((uint)hr) switch
    {
        0x887A0006 => "  (DXGI_ERROR_DEVICE_HUNG)",
        0x887A0005 => "  (DXGI_ERROR_DEVICE_REMOVED)",
        0x887A0020 => "  (DXGI_ERROR_DRIVER_INTERNAL_ERROR)",
        0x887A0004 => "  (DXGI_ERROR_UNSUPPORTED)",
        _ => string.Empty,
    };

    private static string Squash(string message) =>
        message.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static int ArgInt(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length
               && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }
}

/// <summary>Minimal hand-rolled interop — no Vortice/Silk/TerraFX dependency for a throwaway probe.</summary>
internal static class Native
{
    internal const uint FeatureLevel11_0 = 0xB000;
    internal const uint DmlCreateDeviceFlagNone = 0;

    /// <summary>DML_FEATURE_LEVEL_5_0 — the level ORT's CreateDMLDevice requests.</summary>
    internal const uint DmlFeatureLevel5_0 = 0x5000;

    internal static readonly Guid IidId3d12Device = new("189819f1-1db6-4b57-be54-1821339b85f7");
    internal static readonly Guid IidIdmlDevice = new("6dbd6437-96fd-423f-a98c-ae5e7c2a573f");
    internal static readonly Guid IidId3d12CommandQueue = new("0ec870a6-5d7e-4c22-8cfc-5baae07616ed");

    [DllImport("d3d12.dll", ExactSpelling = true)]
    internal static extern int D3D12CreateDevice(nint pAdapter, uint minimumFeatureLevel, in Guid riid, out nint ppDevice);

    /// <summary>Matches ORT's DMLProviderFactoryCreator::CreateDMLDevice exactly (DMLCreateDevice1 @ FL 5_0).</summary>
    [DllImport("DirectML.dll", ExactSpelling = true)]
    internal static extern int DMLCreateDevice1(nint d3d12Device, uint flags, uint minimumFeatureLevel, in Guid riid, out nint ppv);

    [DllImport("onnxruntime", ExactSpelling = true)]
    internal static extern nint OrtSessionOptionsAppendExecutionProviderEx_DML(nint options, nint dmlDevice, nint cmdQueue);

    [StructLayout(LayoutKind.Sequential)]
    private struct CommandQueueDesc
    {
        public int Type;       // D3D12_COMMAND_LIST_TYPE_COMPUTE = 2
        public int Priority;
        public int Flags;
        public uint NodeMask;
    }

    /// <summary>ID3D12Device::CreateCommandQueue — vtable slot 8 (IUnknown 0-2, ID3D12Object 3-6, GetNodeCount 7).</summary>
    internal static unsafe int CreateCommandQueue(nint device, out nint commandQueue)
    {
        var desc = new CommandQueueDesc { Type = 2 };
        nint* vtable = *(nint**)device;
        var createCommandQueue =
            (delegate* unmanaged[Stdcall]<nint, CommandQueueDesc*, Guid*, nint*, int>)vtable[8];

        nint result = 0;
        Guid iid = IidId3d12CommandQueue;
        int hr = createCommandQueue(device, &desc, &iid, &result);
        commandQueue = result;
        return hr;
    }

    /// <summary>IUnknown::Release — vtable slot 2.</summary>
    internal static unsafe void SafeRelease(ref nint pointer)
    {
        if (pointer == 0) return;
        nint* vtable = *(nint**)pointer;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
        release(pointer);
        pointer = 0;
    }
}
