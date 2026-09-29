using FrameFlow.Graph;
using FrameFlow.Inference.Dml.Tests;
using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.WinML.Tests;

/// <summary>
/// What a <see cref="WinMLInferenceSessionOptions"/> sets on a Windows ML session (#481): the graph
/// optimization level, and a named provider's own options.
/// </summary>
public sealed class WinMLOptionsTests
{
    private static readonly long[] Shape = [1, 3, 2, 2];

    [Fact]
    public void TheDefaults_AreOrtsLevel_AndNoProviderOptions()
    {
        var options = new WinMLInferenceSessionOptions();

        Assert.Equal(GraphOptimizationLevel.ORT_ENABLE_ALL, options.OptimizationLevel);
        Assert.Empty(options.ProviderOptions);
    }

    [Fact]
    public void AnUndefinedLevel_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WinMLInferenceSessionOptions { OptimizationLevel = (GraphOptimizationLevel)42 });

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void AProviderOptionWithNoName_IsRefused(string name) =>
        Assert.Throws<ArgumentException>(
            () => new WinMLInferenceSessionOptions { ProviderOptions = new Dictionary<string, string> { [name] = "1" } });

    [Fact]
    public void AProviderOptionWithNoValue_IsRefused() =>
        Assert.Throws<ArgumentException>(
            () => new WinMLInferenceSessionOptions { ProviderOptions = new Dictionary<string, string> { ["a"] = null! } });

    [Fact]
    public void TheOptionsKeepTheirOwnCopy()
    {
        var settings = new Dictionary<string, string> { ["a"] = "1" };
        var options = new WinMLInferenceSessionOptions { ProviderOptions = settings };

        settings["a"] = "2";
        settings["b"] = "3";

        Assert.Equal(new Dictionary<string, string> { ["a"] = "1" }, options.ProviderOptions);
    }

    [Fact]
    public void ADevicePolicy_RefusesProviderOptions()
    {
        var options = new WinMLInferenceSessionOptions { ProviderOptions = new Dictionary<string, string> { ["a"] = "1" } };

        var error = Assert.Throws<ArgumentException>(
            () => WinMLInferenceSession.PolicyOptions(WinMLDevicePolicy.PreferCpu, options));

        Assert.Contains("OnProvider", error.Message);
    }

    [WindowsFact]
    public void ADevicePolicysSessionOptions_CarryTheLevel()
    {
        using var options = WinMLInferenceSession.PolicyOptions(
            WinMLDevicePolicy.PreferCpu,
            new WinMLInferenceSessionOptions { OptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC });

        Assert.Equal(GraphOptimizationLevel.ORT_ENABLE_BASIC, options.GraphOptimizationLevel);
    }

    [WindowsFact]
    public void ANamedProvidersSessionOptions_CarryTheLevel()
    {
        using var options = WinMLInferenceSession.ProviderOptions(
            "CPUExecutionProvider",
            null,
            new WinMLInferenceSessionOptions { OptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL });

        Assert.Equal(GraphOptimizationLevel.ORT_DISABLE_ALL, options.GraphOptimizationLevel);
    }

    [WindowsFact]
    public void WithOptions_EachWayOfOpening_RunsTheModel()
    {
        var options = new WinMLInferenceSessionOptions { OptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC };

        using (var session = new WinMLInferenceSession(OnnxModel.Negate(Shape), options, WinMLDevicePolicy.PreferCpu))
            AssertNegates(session);

        using (var session = WinMLInferenceSession.OnProvider(OnnxModel.Negate(Shape), options, "CPUExecutionProvider"))
            AssertNegates(session);
    }

    /// <summary>
    /// The CPU provider ignores names it does not know, so it cannot show that options arrive.
    /// TensorRT-RTX refuses them, naming the option.
    /// </summary>
    [RequiresTensorRtRtxFact]
    public async Task TensorRtRtx_RefusesAnOptionItDoesNotKnow_ByName()
    {
        var provider = await TensorRtRtx();
        var options = new WinMLInferenceSessionOptions
        {
            ProviderOptions = new Dictionary<string, string> { ["no_such_option"] = "1" },
        };

        var error = Assert.Throws<OnnxRuntimeException>(
            () => WinMLInferenceSession.OnProvider(OnnxModel.Negate(Shape), options, provider.Name, provider.AdapterLuid));

        Assert.Contains("no_such_option", error.Message);
    }

    [RequiresTensorRtRtxFact]
    public async Task TensorRtRtx_KeepsItsEngineCache_WhereTheOptionSays()
    {
        var provider = await TensorRtRtx();
        string cache = Path.Combine(Path.GetTempPath(), $"frameflow-trt-cache-{Guid.NewGuid():N}");
        var options = new WinMLInferenceSessionOptions
        {
            ProviderOptions = new Dictionary<string, string> { ["nv_runtime_cache_path"] = cache },
        };
        try
        {
            using (var session = WinMLInferenceSession.OnProvider(OnnxModel.Negate(Shape), options, provider.Name, provider.AdapterLuid))
                AssertNegates(session);

            Assert.NotEmpty(Directory.GetFiles(cache, "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, recursive: true);
        }
    }

    private static async Task<WinMLProvider> TensorRtRtx()
    {
        await WinMLProviders.RegisterInstalledAsync();
        return WinMLProviders.Available().First(p => p.Name == RequiresTensorRtRtxFactAttribute.Name);
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

/// <summary>Skips unless Windows ML has a TensorRT-RTX provider registered, whose option names the tests use.</summary>
internal sealed class RequiresTensorRtRtxFactAttribute : FactAttribute
{
    public const string Name = "NvTensorRTRTXExecutionProvider";

    public RequiresTensorRtRtxFactAttribute()
    {
        // The GPU gate registers the installed providers before it decides, so Available() below
        // already lists them.
        var gpu = new RequiresWindowsMLGpuProviderFactAttribute();
        if (gpu.Skip is not null)
            Skip = gpu.Skip;
        else if (!WinMLProviders.Available().Any(p => p.Name == Name))
            Skip = $"No {Name} is installed for Windows ML on this machine.";
    }
}
