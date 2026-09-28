using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>
/// The managed ONNX Runtime layer resolves to the DirectML native runtime's version (#438). A
/// floating managed reference took the newest release over DirectML's 1.24.4.
/// </summary>
public sealed class RuntimeVersionTests
{
    [Fact]
    public void TheManagedLayer_IsTheNativeRuntimesVersion()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string native = OrtEnv.Instance().GetVersionString();
        var managed = typeof(OrtEnv).Assembly.GetName().Version!;

        Assert.Equal(native, $"{managed.Major}.{managed.Minor}.{managed.Build}");
    }
}
