using FrameFlow.Graph;
using FrameFlow.Inference.Dml.Tests;
using FrameFlow.Inference.WinML.Core;
using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.WinML.Tests;

/// <summary>
/// Sessions through Windows ML (#423). The model negates its input, so each output is exact. The
/// CPU provider is always registered, so those tests run on any Windows; a vendor provider needs
/// Windows 11 24H2 and one installed.
/// </summary>
public sealed class WinMLSessionTests
{
    private static readonly long[] Shape = [1, 3, 2, 2];

    [Theory]
    [InlineData(WinMLDevicePolicy.PreferGpu, ExecutionProviderDevicePolicy.PREFER_GPU)]
    [InlineData(WinMLDevicePolicy.PreferNpu, ExecutionProviderDevicePolicy.PREFER_NPU)]
    [InlineData(WinMLDevicePolicy.PreferCpu, ExecutionProviderDevicePolicy.PREFER_CPU)]
    [InlineData(WinMLDevicePolicy.MaxPerformance, ExecutionProviderDevicePolicy.MAX_PERFORMANCE)]
    [InlineData(WinMLDevicePolicy.MaxEfficiency, ExecutionProviderDevicePolicy.MAX_EFFICIENCY)]
    [InlineData(WinMLDevicePolicy.MinOverallPower, ExecutionProviderDevicePolicy.MIN_OVERALL_POWER)]
    public void EachPolicy_IsOrtsOwn(WinMLDevicePolicy policy, ExecutionProviderDevicePolicy expected) =>
        Assert.Equal(expected, DevicePolicies.ToOrt(policy));

    [Fact]
    public void AnUndefinedPolicy_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DevicePolicies.ToOrt((WinMLDevicePolicy)99));

    [Theory]
    [InlineData("12345", 12345UL)]
    [InlineData("0", 0UL)]
    [InlineData("", null)]
    [InlineData("0x10", null)]
    [InlineData(null, null)]
    public void TheAdapterLuid_IsReadAsOrtWritesIt(string? value, ulong? expected) =>
        Assert.Equal(expected, WinMLProviders.ParseLuid(value));

    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X64, "win-x64")]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm64, "win-arm64")]
    public void TheRuntime_IsLookedForBesideTheAppThenUnderItsRid(System.Runtime.InteropServices.Architecture architecture, string rid)
    {
        var candidates = WinMLRuntime.Candidates("app", architecture);

        Assert.Equal([Path.Combine("app", "onnxruntime.dll"), Path.Combine("app", "runtimes", rid, "native", "onnxruntime.dll")], candidates);
    }

    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X86)]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm)]
    public void AnArchitectureThePackageShipsNoRuntimeFor_HasNowhereToLook(System.Runtime.InteropServices.Architecture architecture) =>
        Assert.Empty(WinMLRuntime.Candidates("app", architecture));

    [Theory]
    [InlineData(@"C:\Windows\System32\onnxruntime.dll", true)]
    [InlineData(@"c:\windows\system32\ONNXRUNTIME.DLL", true)]
    [InlineData(@"C:\app\onnxruntime.dll", false)]
    [InlineData(@"C:\Windows\System32\sub\onnxruntime.dll", false)]
    public void WindowsOwnCopy_IsTheOneInTheSystemDirectory(string path, bool expected) =>
        Assert.Equal(expected, WinMLRuntime.IsSystemCopy(path, @"C:\Windows\System32"));

    [Theory]
    [InlineData("1.27.1")]
    [InlineData("1.30.0")]
    [InlineData("2.0.0")]
    public void ARuntimeAtLeastWindowsMLs_IsAccepted(string version) =>
        Assert.Null(WinMLRuntime.Refusal(version, @"C:\app\onnxruntime.dll", @"C:\Windows\System32"));

    [Theory]
    [InlineData("1.24.4", @"C:\app\onnxruntime.dll", "Another inference package")]
    [InlineData("1.26.0", @"C:\app\runtimes\win-x64\native\onnxruntime.dll", "Another inference package")]
    [InlineData("1.17.1", @"C:\Windows\System32\onnxruntime.dll", "Windows' own copy")]
    [InlineData("garbage", @"C:\app\onnxruntime.dll", "Another inference package")]
    public void AnOlderRuntime_IsRefusedWithItsLikelyCause(string version, string path, string cause)
    {
        string? refusal = WinMLRuntime.Refusal(version, path, @"C:\Windows\System32");

        Assert.NotNull(refusal);
        Assert.Contains(path, refusal);
        Assert.Contains(version, refusal);
        Assert.Contains(cause, refusal);
    }

    /// <summary>
    /// This project builds without a runtime identifier, so the runtime is under runtimes/; the
    /// package loaded it there rather than the one Windows carries.
    /// </summary>
    [WindowsFact]
    public void ThePackagesOwnRuntime_IsTheOneLoaded()
    {
        string loaded = WinMLRuntime.EnsureLoaded();

        Assert.StartsWith(AppContext.BaseDirectory, loaded);
    }

    [WindowsFact]
    public void ThePreferCpuPolicy_RunsTheModel()
    {
        using var session = new WinMLInferenceSession(OnnxModel.Negate(Shape), WinMLDevicePolicy.PreferCpu);

        AssertNegates(session);
    }

    [WindowsFact]
    public void TheCpuProvider_IsAvailable_AndNamedItRunsTheModel()
    {
        var cpu = Assert.Single(WinMLProviders.Available(), p => p.Name == "CPUExecutionProvider");
        Assert.Equal(WinMLDeviceType.Cpu, cpu.DeviceType);

        using var session = WinMLInferenceSession.OnProvider(OnnxModel.Negate(Shape), cpu.Name);

        AssertNegates(session);
    }

    [WindowsFact]
    public void AProviderNotRegistered_IsRefusedWithWhatIs()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            WinMLInferenceSession.OnProvider(OnnxModel.Negate(Shape), "NoSuchExecutionProvider"));

        Assert.Equal("providerName", error.ParamName);
        Assert.Contains("CPUExecutionProvider", error.Message);
    }

    /// <summary>
    /// After registering what is installed, a vendor GPU provider, named with its adapter, runs the
    /// model. On an NVIDIA GPU that is TensorRT-RTX, which builds an engine as it opens.
    /// </summary>
    [RequiresWindowsMLGpuProviderFact]
    public async Task AVendorGpuProvider_OnItsAdapter_RunsTheModel()
    {
        await WinMLProviders.RegisterInstalledAsync();
        var provider = WinMLProviders.Available().First(RequiresWindowsMLGpuProviderFactAttribute.IsVendorGpu);

        using var session = WinMLInferenceSession.OnProvider(OnnxModel.Negate(Shape), provider.Name, provider.AdapterLuid);

        AssertNegates(session);
    }

    [RequiresWindowsMLGpuProviderFact]
    public async Task CreateAsync_RegistersAndOpensTheModel()
    {
        string model = Path.Combine(Path.GetTempPath(), $"frameflow-winml-{Guid.NewGuid():N}.onnx");
        await File.WriteAllBytesAsync(model, OnnxModel.Negate(Shape));
        try
        {
            using var session = await WinMLInferenceSession.CreateAsync(model);
            AssertNegates(session);
        }
        finally
        {
            File.Delete(model);
        }
    }

    private static void AssertNegates(IInferenceSession session)
    {
        using var pool = new CpuTensorPool();
        using var input = pool.Rent<float>(new TensorShape(1, 3, 2, 2));
        using var output = pool.Rent<float>(new TensorShape(1, 3, 2, 2));
        for (int i = 0; i < 12; i++)
            input.Span[i] = i - 5.5f;

        session.Run(input, output);

        Assert.Equal(input.Span.ToArray().Select(v => -v), output.Span.ToArray());
    }
}

/// <summary>Skips off Windows, where Windows ML does not exist.</summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows ML runs only on Windows.";
    }
}

/// <summary>
/// Skips unless registering the installed Windows ML providers yields a vendor GPU provider: a GPU
/// one other than DirectML, which needs Windows 11 24H2 and a provider installed for the GPU.
/// </summary>
internal sealed class RequiresWindowsMLGpuProviderFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(() =>
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            return "The Windows ML vendor catalog needs Windows 11 24H2 (build 26100) or later.";
        try
        {
            WinMLProviders.RegisterInstalledAsync().GetAwaiter().GetResult();
            return WinMLProviders.Available().Any(IsVendorGpu)
                ? null
                : "No vendor GPU provider is installed for Windows ML on this machine.";
        }
        catch (Exception ex)
        {
            return $"Windows ML could not register providers: {ex.GetType().Name}: {ex.Message}";
        }
    });

    public RequiresWindowsMLGpuProviderFactAttribute()
    {
        if (Reason.Value is { } reason)
            Skip = reason;
    }

    public static bool IsVendorGpu(WinMLProvider provider) =>
        provider.DeviceType == WinMLDeviceType.Gpu && provider.Name != "DmlExecutionProvider";
}
