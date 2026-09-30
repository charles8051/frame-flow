using System.Runtime.InteropServices;
using FrameFlow.Graph;
using FrameFlow.Inference.Dml.Tests;
using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.OpenVino.Tests;

/// <summary>
/// Sessions on Intel's OpenVINO build of ONNX Runtime (#523), on OpenVINO's CPU device, which every
/// Windows x64 machine has. None uses the GPU device: CI has no Intel GPU. The model negates its
/// input, so each output is exact.
/// </summary>
public sealed class OpenVinoSessionTests
{
    private static readonly long[] Shape = [1, 3, 2, 2];

    private static readonly OpenVinoInferenceSessionOptions Cpu = new() { Device = "CPU", CacheDirectory = null };

    [WindowsX64Fact]
    public void TheCpuDevice_RunsTheModel()
    {
        using var session = new OpenVinoInferenceSession(OnnxModel.Negate(Shape), Cpu);

        AssertNegates(session);
    }

    [WindowsX64Fact]
    public void AModelFile_Opens()
    {
        string model = Path.Combine(Path.GetTempPath(), $"frameflow-openvino-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(model, OnnxModel.Negate(Shape));
        try
        {
            using var session = new OpenVinoInferenceSession(model, Cpu);
            AssertNegates(session);
        }
        finally
        {
            File.Delete(model);
        }
    }

    /// <summary>
    /// Intel's native runtime is the one loaded, and the managed layer FrameFlow.Inference.Ort
    /// brings, 1.24.4, is the same C API version as its 1.24.1.
    /// </summary>
    [WindowsX64Fact]
    public void TheNativeRuntime_IsIntels_AndTheManagedLayerSharesItsApiVersion()
    {
        string native = OrtEnv.Instance().GetVersionString();
        var managed = typeof(OrtEnv).Assembly.GetName().Version!;

        Assert.Equal("1.24.1", native);
        Assert.Equal("1.24", $"{managed.Major}.{managed.Minor}");
        Assert.Contains("OpenVINOExecutionProvider", OrtEnv.Instance().GetAvailableProviders());
    }

    /// <summary>
    /// OpenVINO's provider does not take a lone Celu node (ONNX Runtime 1.24.1 keeps a list of
    /// operators it takes only inside a larger graph), so ONNX Runtime would run the node on its
    /// CPU provider. By default the session refuses that.
    /// </summary>
    [WindowsX64Fact]
    public void AModelOpenVinoTakesOnlyInPart_IsRefusedByDefault()
    {
        var error = Assert.Throws<OnnxRuntimeException>(
            () => new OpenVinoInferenceSession(OnnxModel.Unary("Celu", Shape), Cpu));

        Assert.Contains("fallback to CPU EP has been explicitly disabled", error.Message);
    }

    [WindowsX64Fact]
    public void AllowingCpuFallback_OpensIt()
    {
        using var session = new OpenVinoInferenceSession(
            OnnxModel.Unary("Celu", Shape), Cpu with { AllowCpuFallback = true });
        using var pool = new CpuTensorPool();
        using var input = pool.Rent<float>(new TensorShape(1, 3, 2, 2));
        using var output = pool.Rent<float>(new TensorShape(1, 3, 2, 2));
        for (int i = 0; i < 12; i++)
            input.Span[i] = i - 5.5f;

        session.Run(input, output);

        // Celu with alpha 1: x above zero, e^x - 1 below.
        var expected = input.Span.ToArray().Select(x => x > 0 ? x : MathF.Exp(x) - 1);
        Assert.Equal(expected, output.Span.ToArray(), (a, b) => MathF.Abs(a - b) < 1e-5f);
    }

    /// <summary>
    /// The cache directory reaches OpenVINO: opening writes the compiled model there. OpenVINO holds
    /// the blob open until the process exits, so each run's directory is removed by the next.
    /// </summary>
    [WindowsX64Fact]
    public void TheCompiledModel_IsWrittenToTheCacheDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "frameflow-openvino-cache-tests");
        RemoveEarlierRuns(root);
        string cache = Path.Combine(root, Guid.NewGuid().ToString("N"));

        using (var session = new OpenVinoInferenceSession(OnnxModel.Negate(Shape), Cpu with { CacheDirectory = cache }))
            AssertNegates(session);

        Assert.NotEmpty(Directory.EnumerateFiles(cache, "*.blob"));
    }

    /// <summary>
    /// A device OpenVINO does not have fails as the provider failing to load, which the factory takes
    /// to rule the provider out, so it caches the provider that opened.
    /// </summary>
    [WindowsX64Fact]
    public void ADeviceOpenVinoDoesNotHave_RulesTheProviderOut()
    {
        var factory = InferenceSessionFactoryBuilder.Create(
            ExecutionProvider.OpenVino,
            new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
            {
                [ExecutionProvider.OpenVino] = path => new OpenVinoInferenceSession(path, Cpu with { Device = "CPU.1" }),
                [ExecutionProvider.Cpu] = path => new CpuInferenceSession(path),
            });

        using var session = OpenFrom(factory, OnnxModel.Negate(Shape));

        AssertNegates(session);
        Assert.Equal(ExecutionProvider.Cpu, factory.ActiveProvider);
    }

    /// <summary>
    /// A model OpenVINO takes only in part opens on the next provider, and the factory still tries
    /// OpenVINO first for the next model (#497).
    /// </summary>
    [WindowsX64Fact]
    public void APartialModel_OpensOnTheNextProvider_AndTheNextModelStillTriesOpenVino()
    {
        var opened = new List<ExecutionProvider>();
        var factory = InferenceSessionFactoryBuilder.Create(
            ExecutionProvider.OpenVino,
            new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
            {
                [ExecutionProvider.OpenVino] = path =>
                {
                    var session = new OpenVinoInferenceSession(path, Cpu);
                    opened.Add(ExecutionProvider.OpenVino);
                    return session;
                },
                [ExecutionProvider.Cpu] = path =>
                {
                    var session = new CpuInferenceSession(path);
                    opened.Add(ExecutionProvider.Cpu);
                    return session;
                },
            });

        using (OpenFrom(factory, OnnxModel.Unary("Celu", Shape)))
            Assert.Null(factory.ActiveProvider);
        using (var session = OpenFrom(factory, OnnxModel.Negate(Shape)))
            AssertNegates(session);

        Assert.Equal([ExecutionProvider.Cpu, ExecutionProvider.OpenVino], opened);
        Assert.Equal(ExecutionProvider.OpenVino, factory.ActiveProvider);
    }

    /// <summary>
    /// The package's natives sit beside ONNX Runtime's under runtimes/win-x64/native, where it loads
    /// the OpenVINO provider and the provider loads OpenVINO; the files nothing loads are dropped.
    /// </summary>
    [Fact]
    public void TheOutput_HasOpenVinoBesideOnnxRuntime_AndNoUnusedNatives()
    {
        string native = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
        var files = Directory.EnumerateFiles(native).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Superset(
            new HashSet<string?>(StringComparer.OrdinalIgnoreCase)
            {
                "onnxruntime.dll",
                "onnxruntime_providers_openvino.dll",
                "onnxruntime_providers_shared.dll",
                "openvino.dll",
                "openvino_intel_cpu_plugin.dll",
                "openvino_intel_gpu_plugin.dll",
                "openvino_onnx_frontend.dll",
                "tbb12.dll",
            },
            files);
        Assert.DoesNotContain(files, f => f!.EndsWith("_debug.dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("onnxruntime.lib", files);
    }

    [NotWindowsX64Fact]
    public void AnywhereElse_TheSessionRefusesBeforeLoadingAnything() =>
        Assert.Throws<PlatformNotSupportedException>(() => new OpenVinoInferenceSession(OnnxModel.Negate(Shape), Cpu));

    private static IInferenceSession OpenFrom(IInferenceSessionFactory factory, byte[] model)
    {
        string path = Path.Combine(Path.GetTempPath(), $"frameflow-openvino-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(path, model);
        try
        {
            return factory.Open(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void RemoveEarlierRuns(string root)
    {
        if (!Directory.Exists(root))
            return;
        foreach (string run in Directory.EnumerateDirectories(root))
        {
            try
            {
                Directory.Delete(run, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another test process still holds its blob open.
            }
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

/// <summary>Skips unless the process is Windows x64, the one platform Intel's package ships for.</summary>
internal sealed class WindowsX64FactAttribute : FactAttribute
{
    public WindowsX64FactAttribute()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            Skip = "Intel's OpenVINO build of ONNX Runtime ships for Windows x64 only.";
    }
}

/// <summary>Runs only where <see cref="WindowsX64FactAttribute"/> skips.</summary>
internal sealed class NotWindowsX64FactAttribute : FactAttribute
{
    public NotWindowsX64FactAttribute()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            Skip = "Checks the refusal off Windows x64.";
    }
}
