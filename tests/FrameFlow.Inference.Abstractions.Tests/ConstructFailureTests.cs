using System.Runtime.InteropServices;
using FrameFlow.Inference.Core;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// What the factory reads from a provider's construction failure (#497). Pure.
/// </summary>
/// <remarks>
/// ONNX Runtime raises its failures as <c>OnnxRuntimeException</c>, which has no public constructor;
/// the classifier reads only the message, so those rows carry the message on a plain exception.
/// Rows marked "observed" were raised on a Windows machine with a discrete NVIDIA GPU, the ONNX
/// Runtime DirectML build or Intel's OpenVINO build, and FrameFlow's sessions; the device-lost ones
/// during a GPU reset. The rest
/// follow ONNX Runtime's source and its reported failures; no DirectML out-of-memory was observed
/// (docs/investigations/2026-09-29-dml-out-of-memory.md).
/// </remarks>
public sealed class ConstructFailureTests
{
    public static TheoryData<string, Exception, string> Cases => new()
    {
        // Device lost.
        {
            "observed: DirectML on a suspended device",
            new Exception(
                @"[ErrorCode:RuntimeException] E:\_work\1\s\onnxruntime\core\providers\dml\dml_provider_factory.cc(596)\onnxruntime.dll!00007FFC9F64B428: (caller: 00007FFC9F64B484) Exception(1) tid(9264) 887A0005 The GPU device instance has been suspended. Use GetDeviceRemovedReason to determine the appropriate action."),
            nameof(ConstructFailureKind.DeviceLost)
        },
        {
            "observed: DirectML after a hang",
            new Exception(
                @"[ErrorCode:RuntimeException] E:\_work\1\s\onnxruntime\core\providers\dml\dml_provider_factory.cc(524)\onnxruntime.dll!00007FFC9F64B118: (caller: 00007FFC9F64B46A) Exception(2) tid(9264) 887A0006 The GPU will not respond to more commands, most likely because of an invalid command passed by the calling application."),
            nameof(ConstructFailureKind.DeviceLost)
        },
        {
            "observed: a D3D12 call on a suspended device",
            new COMException(
                "The GPU device instance has been suspended. Use GetDeviceRemovedReason to determine the appropriate action. (0x887A0005)",
                unchecked((int)0x887A0005)),
            nameof(ConstructFailureKind.DeviceLost)
        },
        {
            "DXGI_ERROR_DEVICE_RESET by HRESULT alone",
            new COMException("Device reset.", unchecked((int)0x887A0007)),
            nameof(ConstructFailureKind.DeviceLost)
        },

        // Out of memory.
        {
            "CUDA",
            new Exception(
                "[ErrorCode:RuntimeException] onnxruntime::CudaCall CUDA failure 2: out of memory ; GPU=0 ; hostname=HOST ; expr=cudaMalloc((void**)&p, size);"),
            nameof(ConstructFailureKind.OutOfMemory)
        },
        {
            "ONNX Runtime's arena",
            new Exception(
                "[ErrorCode:RuntimeException] onnxruntime::BFCArena::AllocateRawInternal Failed to allocate memory for requested buffer of size 4294967296"),
            nameof(ConstructFailureKind.OutOfMemory)
        },
        {
            "cuBLAS",
            new Exception("CUBLAS failure 3: CUBLAS_STATUS_ALLOC_FAILED ; GPU=0 ; hostname=HOST ; expr=cublasCreate(&cublas_handle_);"),
            nameof(ConstructFailureKind.OutOfMemory)
        },
        {
            "DirectML's E_OUTOFMEMORY, in the format of the observed DirectML failures",
            new Exception(
                "[ErrorCode:RuntimeException] onnxruntime.dll!00007FFC9F64B428: (caller: 00007FFC9F64B484) Exception(3) tid(9264) 8007000E Not enough memory resources are available to complete this operation."),
            nameof(ConstructFailureKind.OutOfMemory)
        },
        {
            "E_OUTOFMEMORY by HRESULT alone",
            new COMException("Allocation failed.", unchecked((int)0x8007000E)),
            nameof(ConstructFailureKind.OutOfMemory)
        },
        {
            "the CPU provider's host memory",
            new OutOfMemoryException(),
            nameof(ConstructFailureKind.OutOfMemory)
        },

        // Provider unavailable.
        {
            "observed: DirectML with no adapter at the index",
            new Exception(
                @"[ErrorCode:RuntimeException] E:\_work\1\s\onnxruntime\core\providers\dml\dml_provider_factory.cc(513)\onnxruntime.dll!00007FFCA6CFB148: (caller: 00007FFCA6CFB46A) Exception(1) tid(dab0) 887A0002 The object was not found. If calling IDXGIFactory::EnumAdaptes, there is no adapter with the specified ordinal."),
            nameof(ConstructFailureKind.ProviderUnavailable)
        },
        {
            "observed: the CUDA provider in the DirectML build",
            new EntryPointNotFoundException(
                "Unable to find an entry point named 'OrtSessionOptionsAppendExecutionProvider_CUDA' in DLL 'onnxruntime'."),
            nameof(ConstructFailureKind.ProviderUnavailable)
        },
        {
            "ONNX Runtime unable to load the CUDA provider's library",
            new Exception(
                @"[ErrorCode:RuntimeException] onnxruntime::ProviderLibrary::Get [ONNXRuntimeError] : 1 : FAIL : LoadLibrary failed with error 126 """" when trying to load ""C:\app\onnxruntime_providers_cuda.dll"""),
            nameof(ConstructFailureKind.ProviderUnavailable)
        },
        {
            "a missing native library, inside a type initializer",
            new TypeInitializationException(
                "Microsoft.ML.OnnxRuntime.NativeMethods",
                new DllNotFoundException(
                    "Unable to load DLL 'onnxruntime' or one of its dependencies: The specified module could not be found. (0x8007007E)")),
            nameof(ConstructFailureKind.ProviderUnavailable)
        },
        {
            "Windows ML without the provider registered",
            new ArgumentException(
                "No registered provider 'NvTensorRTRTXExecutionProvider'. Available: CPUExecutionProvider, DmlExecutionProvider.",
                "providerName"),
            nameof(ConstructFailureKind.ProviderUnavailable)
        },
        {
            "observed: OpenVINO asked for a device it does not have; the device goes only to ONNX Runtime's log",
            new Exception("[ErrorCode:Fail] Failed to load provider OpenVINO"),
            nameof(ConstructFailureKind.ProviderUnavailable)
        },
        {
            "a session package on a platform it ships no runtime for",
            new PlatformNotSupportedException(
                "FrameFlow.Inference.OpenVino runs on Windows x64 only: Intel's OpenVINO build of ONNX Runtime ships no native runtime for linux-x64."),
            nameof(ConstructFailureKind.ProviderUnavailable)
        },

        // Unknown.
        {
            "observed: a model file that does not exist",
            new Exception(
                "[ErrorCode:NoSuchFile] Load model from C:/nonexistent/model.onnx failed:Load model C:/nonexistent/model.onnx failed. File doesn't exist"),
            nameof(ConstructFailureKind.Unknown)
        },
        {
            "observed: bytes that are not a model",
            new Exception("[ErrorCode:InvalidProtobuf] Failed to load model because protobuf parsing failed."),
            nameof(ConstructFailureKind.Unknown)
        },
        {
            "observed: OpenVINO taking a model only in part, with CPU fallback refused",
            new Exception(
                "[ErrorCode:Fail] This session contains graph nodes that are assigned to the default CPU EP, but fallback to CPU EP has been explicitly disabled by the user."),
            nameof(ConstructFailureKind.Unknown)
        },
        {
            "a device-lost code inside a longer hex run, such as an address",
            new Exception(@"onnxruntime.dll!00000000887A0005: (caller: 00007FFC9F64B484) something failed"),
            nameof(ConstructFailureKind.Unknown)
        },
        {
            "text that names no cause",
            new InvalidOperationException("CUDA not available on this host"),
            nameof(ConstructFailureKind.Unknown)
        },

        // Words in a quoted path or name are not the failure.
        {
            "ONNX Runtime unable to load a library whose path holds a failure's words",
            new Exception(
                @"[ErrorCode:RuntimeException] onnxruntime::ProviderLibrary::Get [ONNXRuntimeError] : 1 : FAIL : LoadLibrary failed with error 126 """" when trying to load ""C:\out of memory\887A0005\onnxruntime_providers_cuda.dll"""),
            nameof(ConstructFailureKind.ProviderUnavailable)
        },
        {
            "a missing library whose path holds a failure's words",
            new DllNotFoundException(@"Unable to load DLL 'C:\out of memory\cudnn64_9.dll' or one of its dependencies."),
            nameof(ConstructFailureKind.ProviderUnavailable)
        },
        {
            "apostrophes inside words quote nothing",
            new Exception("the provider's allocator reported out of memory and can't continue"),
            nameof(ConstructFailureKind.OutOfMemory)
        },

        // Several causes: device loss wins, then out of memory.
        {
            "out of memory and device lost together",
            new AggregateException(
                new OutOfMemoryException(),
                new COMException("Device removed.", unchecked((int)0x887A0005))),
            nameof(ConstructFailureKind.DeviceLost)
        },
        {
            "out of memory wrapping a missing library",
            new Exception("CUDA failure 2: out of memory", new DllNotFoundException("cudnn64_9.dll")),
            nameof(ConstructFailureKind.OutOfMemory)
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void AFailure_IsClassifiedByWhatItSays(string because, Exception failure, string expected) =>
        Assert.True(
            ConstructFailure.Classify(failure).ToString() == expected,
            $"{because}: expected {expected}, got {ConstructFailure.Classify(failure)}.");

    [Theory]
    [InlineData(nameof(ConstructFailureKind.DeviceLost), true)]
    [InlineData(nameof(ConstructFailureKind.ProviderUnavailable), true)]
    [InlineData(nameof(ConstructFailureKind.OutOfMemory), false)]
    [InlineData(nameof(ConstructFailureKind.Unknown), false)]
    public void OnlyALastingFailure_RulesAProviderOut(string kind, bool rulesOut) =>
        Assert.Equal(rulesOut, ConstructFailure.RulesOut(Enum.Parse<ConstructFailureKind>(kind)));
}
