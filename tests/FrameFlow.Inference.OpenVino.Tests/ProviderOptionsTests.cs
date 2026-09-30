using System.Text.Json;
using FrameFlow.Inference.OpenVino.Core;

namespace FrameFlow.Inference.OpenVino.Tests;

/// <summary>
/// The options a session asks ONNX Runtime for, and the options record's own checks (#523). Pure:
/// nothing here loads a native library, so these run on every OS.
/// </summary>
public sealed class ProviderOptionsTests
{
    [Fact]
    public void TheDefaults_AreTheInvestigations()
    {
        var options = new OpenVinoInferenceSessionOptions();

        Assert.Equal("GPU", options.Device);
        Assert.Equal(
            OpenVinoProviderOptions.CacheDirectoryUnder(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            options.CacheDirectory);
        Assert.Equal(OpenVinoQueueThrottle.Low, options.QueueThrottle);
        Assert.Null(options.PrecisionHint);
        Assert.False(options.AllowCpuFallback);
    }

    [Fact]
    public void TheDefaults_AskForTheGpu_Throttled_Cached_AndWhole()
    {
        var options = new OpenVinoInferenceSessionOptions { CacheDirectory = @"C:\cache" };

        var provider = OpenVinoProviderOptions.For(options);

        Assert.Equal(["device_type", "load_config"], provider.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("GPU", provider["device_type"]);
        Assert.Equal(
            new Dictionary<string, Dictionary<string, string>>
            {
                ["GPU"] = new()
                {
                    ["PERFORMANCE_HINT"] = "LATENCY",
                    ["GPU_QUEUE_THROTTLE"] = "LOW",
                    ["CACHE_DIR"] = @"C:\cache",
                },
            },
            Parse(provider["load_config"]));
        Assert.Equal(
            new Dictionary<string, string> { ["session.disable_cpu_ep_fallback"] = "1" },
            OpenVinoProviderOptions.SessionEntries(options));
    }

    [Fact]
    public void AnIndexedGpu_IsTheDevice_AndItsPropertiesGoUnderGpu()
    {
        // The provider looks properties up by the device's name without its index, and skips an
        // entry keyed "GPU.1".
        var options = new OpenVinoInferenceSessionOptions { Device = "GPU.1", CacheDirectory = null };

        var provider = OpenVinoProviderOptions.For(options);

        Assert.Equal("GPU.1", provider["device_type"]);
        var config = Parse(provider["load_config"]);
        Assert.Equal(["GPU"], config.Keys);
        Assert.Equal("LOW", config["GPU"]["GPU_QUEUE_THROTTLE"]);
    }

    [Theory]
    [InlineData("CPU")]
    [InlineData("NPU")]
    [InlineData("NPU.0")]
    public void OffTheGpu_TheThrottleIsNotSent(string device)
    {
        var options = new OpenVinoInferenceSessionOptions { Device = device, CacheDirectory = null };

        var config = Parse(OpenVinoProviderOptions.LoadConfig(options));

        var properties = Assert.Single(config).Value;
        Assert.Equal(new Dictionary<string, string> { ["PERFORMANCE_HINT"] = "LATENCY" }, properties);
    }

    [Theory]
    [InlineData(OpenVinoQueueThrottle.Low, "LOW")]
    [InlineData(OpenVinoQueueThrottle.Medium, "MEDIUM")]
    [InlineData(OpenVinoQueueThrottle.High, "HIGH")]
    public void EachThrottle_IsOpenVinosName(OpenVinoQueueThrottle throttle, string expected)
    {
        var options = new OpenVinoInferenceSessionOptions { QueueThrottle = throttle };

        Assert.Equal(expected, Parse(OpenVinoProviderOptions.LoadConfig(options))["GPU"]["GPU_QUEUE_THROTTLE"]);
    }

    [Fact]
    public void NoThrottle_LeavesOpenVinosOwn()
    {
        var options = new OpenVinoInferenceSessionOptions { QueueThrottle = null };

        Assert.DoesNotContain("GPU_QUEUE_THROTTLE", Parse(OpenVinoProviderOptions.LoadConfig(options))["GPU"].Keys);
    }

    [Fact]
    public void APrecisionHint_IsSent_AndNoneLeavesTheDevicesOwn()
    {
        var f32 = new OpenVinoInferenceSessionOptions { PrecisionHint = "f32" };
        var none = new OpenVinoInferenceSessionOptions();

        Assert.Equal("f32", Parse(OpenVinoProviderOptions.LoadConfig(f32))["GPU"]["INFERENCE_PRECISION_HINT"]);
        Assert.DoesNotContain("INFERENCE_PRECISION_HINT", Parse(OpenVinoProviderOptions.LoadConfig(none))["GPU"].Keys);
    }

    [Fact]
    public void NoCacheDirectory_SendsNoCache()
    {
        var options = new OpenVinoInferenceSessionOptions { CacheDirectory = null };

        Assert.DoesNotContain("CACHE_DIR", Parse(OpenVinoProviderOptions.LoadConfig(options))["GPU"].Keys);
    }

    [Fact]
    public void AllowingCpuFallback_SendsNoSessionEntry()
    {
        var options = new OpenVinoInferenceSessionOptions { AllowCpuFallback = true };

        Assert.Empty(OpenVinoProviderOptions.SessionEntries(options));
    }

    [Theory]
    [InlineData(@"C:\Users\A User\AppData\Local\FrameFlow.Inference.OpenVino\cache")]
    [InlineData("/home/user/.local/share/cache \"quoted\"")]
    [InlineData(@"D:\données\キャッシュ\+&<>'")]
    public void ACachePath_SurvivesTheJson(string path)
    {
        var options = new OpenVinoInferenceSessionOptions { CacheDirectory = path };

        Assert.Equal(path, Parse(OpenVinoProviderOptions.LoadConfig(options))["GPU"]["CACHE_DIR"]);
    }

    [Fact]
    public void TheDefaultCache_IsUnderLocalApplicationData_OrOffWhenThereIsNone()
    {
        Assert.Equal(
            Path.Combine("local", "FrameFlow.Inference.OpenVino", "cache"),
            OpenVinoProviderOptions.CacheDirectoryUnder("local"));
        Assert.Null(OpenVinoProviderOptions.CacheDirectoryUnder(""));
    }

    [Theory]
    [InlineData("CPU")]
    [InlineData("GPU")]
    [InlineData("NPU")]
    [InlineData("GPU.0")]
    [InlineData("GPU.12")]
    public void ADevice_IsAccepted(string device) =>
        Assert.Equal(device, new OpenVinoInferenceSessionOptions { Device = device }.Device);

    [Theory]
    [InlineData("")]
    [InlineData("gpu")]
    [InlineData("GPU.")]
    [InlineData("GPU.x")]
    [InlineData("GPU.1.2")]
    [InlineData(" GPU")]
    [InlineData("AUTO")]
    [InlineData("AUTO:GPU,CPU")]
    [InlineData("HETERO:GPU,CPU")]
    [InlineData("VPU")]
    public void AnythingElse_IsRefused(string device) =>
        Assert.Throws<ArgumentException>(() => new OpenVinoInferenceSessionOptions { Device = device });

    [Fact]
    public void ANullDevice_IsRefused() =>
        Assert.Throws<ArgumentNullException>(() => new OpenVinoInferenceSessionOptions { Device = null! });

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ABlankCacheDirectory_IsRefused(string path) =>
        Assert.Throws<ArgumentException>(() => new OpenVinoInferenceSessionOptions { CacheDirectory = path });

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ABlankPrecisionHint_IsRefused(string hint) =>
        Assert.Throws<ArgumentException>(() => new OpenVinoInferenceSessionOptions { PrecisionHint = hint });

    [Fact]
    public void AnUndefinedThrottle_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenVinoInferenceSessionOptions { QueueThrottle = (OpenVinoQueueThrottle)7 });

    /// <summary>The <c>load_config</c> JSON as the provider reads it: device, then property, then value.</summary>
    private static Dictionary<string, Dictionary<string, string>> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)!;
}
