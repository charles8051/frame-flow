using System.Runtime.InteropServices;
using FrameFlow.Graph;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>
/// A model's named free dimensions fixed when a DirectML session loads it (#472). The model negates
/// its input, so a run on the fixed shape is exact.
/// </summary>
public sealed class FreeDimensionTests
{
    private static readonly byte[] Dynamic = OnnxModel.Negate("batch", 3, "height", "width");

    private static readonly DmlInferenceSessionOptions AllFixed = new()
    {
        FreeDimensions = new Dictionary<string, long> { ["batch"] = 1, ["height"] = 8, ["width"] = 16 },
    };

    [Fact]
    public void NoOptions_FixNothing() => Assert.Empty(new DmlInferenceSessionOptions().FreeDimensions);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ANameMustNotBeBlank(string name)
    {
        var error = Assert.Throws<ArgumentException>(
            () => new DmlInferenceSessionOptions { FreeDimensions = new Dictionary<string, long> { [name] = 1 } });
        Assert.Contains("name", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ASizeMustBeAtLeastOne(long size)
    {
        var error = Assert.Throws<ArgumentException>(
            () => new DmlInferenceSessionOptions { FreeDimensions = new Dictionary<string, long> { ["height"] = size } });
        Assert.Contains("height", error.Message);
    }

    [Fact]
    public void TheOptionsKeepTheirOwnCopy()
    {
        var sizes = new Dictionary<string, long> { ["height"] = 8 };
        var options = new DmlInferenceSessionOptions { FreeDimensions = sizes };

        sizes["height"] = 0;
        sizes["width"] = 16;

        Assert.Equal(new Dictionary<string, long> { ["height"] = 8 }, options.FreeDimensions);
    }

    [RequiresDirectMLFact]
    public void WithoutOptions_TheDimensionsStayFree()
    {
        using var session = new DmlInferenceSession(Dynamic);

        Assert.Equal([-1, 3, -1, -1], session.InputShapes[0]);
    }

    [RequiresDirectMLFact]
    public void TheSessionReportsTheFixedShape_AndRunsIt()
    {
        using var session = new DmlInferenceSession(Dynamic, AllFixed);

        Assert.Equal([1, 3, 8, 16], session.InputShapes[0]);
        Assert.Equal([1, 3, 8, 16], session.OutputShapes[0]);
        AssertNegates(session);
    }

    [RequiresDirectMLFact]
    public void TheDeviceBoundSession_FixesThemToo()
    {
        using var device = WarpDevice();
        using var queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Compute));
        using var session = DmlInferenceSession.OnDevice(Dynamic, device.NativePointer, queue.NativePointer, AllFixed);

        Assert.Equal([1, 3, 8, 16], session.InputShapes[0]);
        AssertNegates(session);
    }

    [RequiresDirectMLFact]
    public void ADimensionLeftFree_IsLoggedByName()
    {
        var logger = new RecordingLogger();
        var options = new DmlInferenceSessionOptions
        {
            FreeDimensions = new Dictionary<string, long> { ["batch"] = 1, ["hieght"] = 8, ["width"] = 16 },
        };

        using var session = new DmlInferenceSession(Dynamic, options, logger);

        Assert.Equal([1, 3, -1, 16], session.InputShapes[0]);
        string message = Assert.Single(logger.Messages);
        Assert.Contains("'x'", message);
        Assert.Contains("[height]", message);
    }

    [RequiresDirectMLFact]
    public void EveryDimensionFixed_LogsNothing()
    {
        var logger = new RecordingLogger();

        using var session = new DmlInferenceSession(Dynamic, AllFixed, logger);

        Assert.Empty(logger.Messages);
    }

    private static void AssertNegates(DmlInferenceSession session)
    {
        using var pool = new CpuTensorPool();
        var shape = new TensorShape(1, 3, 8, 16);
        using var input = pool.Rent<float>(shape);
        using var output = pool.Rent<float>(shape);
        for (int i = 0; i < input.Span.Length; i++)
            input.Span[i] = i;

        session.Run(input, output);

        Assert.Equal(input.ReadOnlySpan.ToArray().Select(v => -v), MemoryMarshal.Cast<byte, float>(output.Bytes.Span).ToArray());
    }

    private static ID3D12Device WarpDevice()
    {
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(debug: false);
        using var adapter = factory.EnumWarpAdapter<IDXGIAdapter>();
        Vortice.Direct3D12.D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? device).CheckError();
        return device!;
    }

    private sealed class RecordingLogger : ILogger<DmlInferenceSession>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}

/// <summary>Skipped unless the DirectML execution provider loads here.</summary>
internal sealed class RequiresDirectMLFactAttribute : FactAttribute
{
    public RequiresDirectMLFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "DirectML runs on Windows only.";
        else if (!DmlProbe.IsAvailable)
            Skip = $"The DirectML execution provider does not load here: {DmlProbe.Failure?.Message}";
    }
}
