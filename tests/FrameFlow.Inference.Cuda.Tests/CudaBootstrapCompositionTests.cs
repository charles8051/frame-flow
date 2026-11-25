using FrameFlow.Inference.Cuda;
using Xunit;

namespace FrameFlow.Inference.Cuda.Tests;

/// <summary>
/// Unit tests for the pure composition of gathered probe facts into a
/// <see cref="CudaBootstrapResult"/> (<see cref="CudaBootstrapper.Compose"/>,
/// <see cref="CudaBootstrapper.LinuxFacts"/>), and for the Linux install
/// instructions. No loader, no ONNX Runtime, no GPU.
/// </summary>
public sealed class CudaBootstrapCompositionTests
{
    private static readonly CudaBootstrapper.OrtProbeFacts OrtReady = new(true, "1.26.0", null);

    private static LinuxCudaVerdict Verdict(MissingNativeComponent missing, params CudaLibraryFailure[] failed) =>
        new(missing, [], failed);

    private static CudaLibraryFailure Failure(string soname, MissingNativeComponent component, string? error) =>
        new(new CudaNativeLibrary(soname, component, Required: true), error);

    [Fact]
    public void NothingMissingAndOrtReady_IsReadyWithNoInstructions()
    {
        var result = CudaBootstrapper.Compose(
            CudaBootstrapper.LinuxFacts(Verdict(MissingNativeComponent.None), "/cuda/lib", "/cudnn/lib", OrtReady),
            new LinuxCudaInstallInstructionProvider()
        );

        Assert.True(result.IsReady);
        Assert.Equal(MissingNativeComponent.None, result.Missing);
        Assert.Empty(result.Instructions);
        Assert.Equal("/cuda/lib", result.DetectedCudaToolkitBin);
        Assert.Equal("/cudnn/lib", result.DetectedCudnnBin);
        Assert.Equal("1.26.0", result.OrtVersion);
        Assert.Null(result.DriverFailure);
    }

    [Fact]
    public void LibrariesLoadedButOrtProviderFails_IsMissingTheProviderOnly()
    {
        var ortFailure = new InvalidOperationException("provider load failed");

        var result = CudaBootstrapper.Compose(
            CudaBootstrapper.LinuxFacts(
                Verdict(MissingNativeComponent.None),
                "/cuda/lib",
                "/cudnn/lib",
                new CudaBootstrapper.OrtProbeFacts(false, "1.26.0", ortFailure)
            ),
            new LinuxCudaInstallInstructionProvider()
        );

        Assert.Equal(CudaBootstrapStatus.MissingComponents, result.Status);
        Assert.Equal(MissingNativeComponent.OrtCudaProvider, result.Missing);
        Assert.Same(ortFailure, result.OrtFailure);
        Assert.Equal(
            [MissingNativeComponent.OrtCudaProvider],
            result.Instructions.Select(i => i.Component)
        );
    }

    [Fact]
    public void EverythingMissing_InstructionsAreInDependencyOrder()
    {
        var missing =
            MissingNativeComponent.GpuDriver
            | MissingNativeComponent.CudaToolkit
            | MissingNativeComponent.CuDnn;

        var result = CudaBootstrapper.Compose(
            CudaBootstrapper.LinuxFacts(
                Verdict(missing, Failure("libcuda.so.1", MissingNativeComponent.GpuDriver, "no driver")),
                "/should/not/appear",
                "/nor/this",
                new CudaBootstrapper.OrtProbeFacts(false, "1.26.0", new InvalidOperationException())
            ),
            new LinuxCudaInstallInstructionProvider()
        );

        Assert.Equal(
            [
                MissingNativeComponent.GpuDriver,
                MissingNativeComponent.CudaToolkit,
                MissingNativeComponent.CuDnn,
                MissingNativeComponent.OrtCudaProvider,
            ],
            result.Instructions.Select(i => i.Component)
        );
        // A directory is not reported for a layer that is missing.
        Assert.Null(result.DetectedCudaToolkitBin);
        Assert.Null(result.DetectedCudnnBin);
    }

    [Fact]
    public void MissingDriver_CarriesTheLoadersMessage()
    {
        var result = CudaBootstrapper.Compose(
            CudaBootstrapper.LinuxFacts(
                Verdict(
                    MissingNativeComponent.GpuDriver,
                    Failure("libcuda.so.1", MissingNativeComponent.GpuDriver, "libcuda.so.1: cannot open shared object file")
                ),
                "/cuda/lib",
                "/cudnn/lib",
                OrtReady
            ),
            new LinuxCudaInstallInstructionProvider()
        );

        var failure = Assert.IsType<DllNotFoundException>(result.DriverFailure);
        Assert.Equal("libcuda.so.1: cannot open shared object file", failure.Message);
        Assert.Equal("/cuda/lib", result.DetectedCudaToolkitBin);
    }

    [Fact]
    public void NoInstructionProvider_StillReportsWhatIsMissing()
    {
        var result = CudaBootstrapper.Compose(
            CudaBootstrapper.LinuxFacts(Verdict(MissingNativeComponent.CuDnn), "/cuda/lib", null, OrtReady),
            instructions: null
        );

        Assert.Equal(CudaBootstrapStatus.MissingComponents, result.Status);
        Assert.Equal(MissingNativeComponent.CuDnn, result.Missing);
        Assert.Empty(result.Instructions);
    }

    // ── Linux install instructions ───────────────────────────────────────

    [Theory]
    [InlineData(MissingNativeComponent.GpuDriver, "libcuda.so.1")]
    [InlineData(MissingNativeComponent.CudaToolkit, "cuda-cudart-12-")]
    [InlineData(MissingNativeComponent.CudaToolkit, "LD_LIBRARY_PATH")]
    [InlineData(MissingNativeComponent.CuDnn, "libcudnn9-cuda-12")]
    [InlineData(MissingNativeComponent.CuDnn, "runtimes/linux-x64/native")]
    [InlineData(MissingNativeComponent.OrtCudaProvider, "libonnxruntime_providers_cuda.so")]
    public void LinuxInstruction_NamesTheLinuxRemedy(MissingNativeComponent component, string expected)
    {
        var instruction = new LinuxCudaInstallInstructionProvider().GetInstruction(component);

        Assert.Equal(component, instruction.Component);
        Assert.Contains(expected, instruction.Description + " " + instruction.ActionableStep, StringComparison.Ordinal);
        Assert.DoesNotContain(".dll", instruction.Description + instruction.ActionableStep, StringComparison.Ordinal);
        Assert.NotNull(instruction.DocumentationUri);
    }

    [Theory]
    [InlineData(MissingNativeComponent.None)]
    [InlineData(MissingNativeComponent.CudaToolkit | MissingNativeComponent.CuDnn)]
    public void LinuxInstruction_RejectsNoneAndCombinations(MissingNativeComponent component) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LinuxCudaInstallInstructionProvider().GetInstruction(component)
        );
}
