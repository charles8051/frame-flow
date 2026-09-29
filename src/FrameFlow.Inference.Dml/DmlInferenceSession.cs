// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using FrameFlow.Graph;
using FrameFlow.Inference.Dml.Core;
using FrameFlow.Inference.Dml.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.Dml;

/// <summary>
/// ONNX Runtime inference session configured for the DirectML execution
/// provider, accepting <see cref="ICpuTensor"/> inputs and outputs. The
/// DML EP handles host→device staging through D3D12 upload buffers.
/// Built with <see cref="OnDevice(string, nint, nint, ILogger{DmlInferenceSession}?)"/>, it
/// also takes <see cref="DeviceTensor"/> inputs in place on the caller's device.
/// Sibling to <c>FrameFlow.Inference.Cuda.CudaInferenceSession</c>
/// per ADR-0049 §3.
/// </summary>
/// <remarks>
/// <para>
/// The host-memory binding pipeline (the dictionary-form
/// <see cref="OrtInferenceSessionBase.Run(IReadOnlyDictionary{string, ICpuTensor}, IReadOnlyDictionary{string, ICpuTensor})"/>,
/// the single-IO convenience overload, and the tensor-binding helpers)
/// lives in <see cref="OrtInferenceSessionBase"/>, shared verbatim with
/// the CUDA EP per ADR-0049 §3. This class adds the DirectML
/// session-options configuration and the device-input binding.
/// </para>
/// <para>
/// <b>Adapter selection.</b> The constructors use the default DirectML adapter.
/// <see cref="OnDevice(string, nint, nint, ILogger{DmlInferenceSession}?)"/> runs on the device
/// the caller passes, which selects its adapter.
/// </para>
/// <para>
/// <b>Free dimensions.</b> <see cref="DmlInferenceSessionOptions.FreeDimensions"/> fixes a model's
/// named dynamic dimensions when it loads. DirectML runs a pinned shape faster, and compiles it
/// faster on the first run (#472).
/// </para>
/// <para>
/// <b>Device inputs.</b> A session built with <see cref="OnDevice(string, nint, nint, ILogger{DmlInferenceSession}?)"/>
/// runs DirectML on the caller's <c>ID3D12Device</c> and <c>ID3D12CommandQueue</c>, and
/// <see cref="Run(IReadOnlyDictionary{string, DeviceTensor}, IReadOnlyDictionary{string, ICpuTensor})"/>
/// binds a D3D12 buffer on that device as a model input with no copy. Its queue waits on the
/// tensor's ready fence on the GPU first, so the stage that wrote it may use another queue. A
/// session from the constructors runs on a device ONNX Runtime created, and binds no device input.
/// </para>
/// <para>
/// <b>DirectML.dll.</b> The package carries its own, because the one Windows ships is too old for
/// current ONNX Runtime on some Windows 10 builds. It sits beside ONNX Runtime's
/// <c>onnxruntime.dll</c>, at the app root or under <c>runtimes/win-x64/native</c>, and ONNX
/// Runtime's DirectML provider loads it from there before Windows' own (#433). Nothing needs
/// installing or adding to PATH.
/// </para>
/// <para>
/// <b>Threading.</b> A single session is safe for sequential Run
/// calls. Concurrent calls against one session are not supported in
/// V1.
/// </para>
/// <para>
/// <b>GPU device loss is not recoverable in-process.</b> After a
/// Windows TDR, <c>AppendExecutionProvider_DML</c> fails permanently
/// for the lifetime of the process with <c>887A0006</c>
/// (<c>DXGI_ERROR_DEVICE_HUNG</c>). This is not something FrameFlow
/// can work around: ORT already builds a fresh <c>IDXGIFactory4</c>,
/// adapter, and <c>ID3D12Device</c> on every registration attempt, and
/// it is the underlying <c>D3D12CreateDevice</c> that fails. Owning the
/// device ourselves, as <see cref="OnDevice(string, nint, nint, ILogger{DmlInferenceSession}?)"/>
/// does, calls the same failing API one frame higher up. Recovery is a process
/// restart. See
/// <c>docs/investigations/2026-08-14-dml-in-process-tdr-recovery.md</c>
/// before re-opening this — the analysis is done and the answer is no.
/// </para>
/// </remarks>
public sealed partial class DmlInferenceSession : OrtInferenceSessionBase, IDeviceInputSession
{
    private readonly DeviceBinding? _device;

    /// <summary>Loads a model from <paramref name="modelPath"/> and configures the DirectML EP.</summary>
    public DmlInferenceSession(string modelPath)
        : this(modelPath, DefaultOptions, logger: null) { }

    /// <inheritdoc cref="DmlInferenceSession(string)" />
    public DmlInferenceSession(string modelPath, ILogger<DmlInferenceSession>? logger)
        : this(modelPath, DefaultOptions, logger) { }

    /// <summary>
    /// Loads a model from <paramref name="modelPath"/> as <paramref name="options"/> says and
    /// configures the DirectML EP.
    /// </summary>
    public DmlInferenceSession(string modelPath, DmlInferenceSessionOptions options)
        : this(modelPath, options, logger: null) { }

    /// <inheritdoc cref="DmlInferenceSession(string, DmlInferenceSessionOptions)" />
    // CA2000: the SessionOptions built here is owned by the base, which
    // disposes it in OrtInferenceSessionBase.Dispose(). The analyzer
    // can't see the ownership handoff across the base initializer.
#pragma warning disable CA2000
    public DmlInferenceSession(
        string modelPath, DmlInferenceSessionOptions options, ILogger<DmlInferenceSession>? logger)
        : base(modelPath, BuildSessionOptions(options, static o => o.AppendExecutionProvider_DML()))
    {
        LogFreeDimensionsLeft(options, logger);
    }
#pragma warning restore CA2000

    /// <summary>Loads a model from <paramref name="modelBytes"/> and configures the DirectML EP.</summary>
    public DmlInferenceSession(byte[] modelBytes)
        : this(modelBytes, DefaultOptions, logger: null) { }

    /// <inheritdoc cref="DmlInferenceSession(byte[])" />
    public DmlInferenceSession(byte[] modelBytes, ILogger<DmlInferenceSession>? logger)
        : this(modelBytes, DefaultOptions, logger) { }

    /// <summary>
    /// Loads a model from <paramref name="modelBytes"/> as <paramref name="options"/> says and
    /// configures the DirectML EP.
    /// </summary>
    public DmlInferenceSession(byte[] modelBytes, DmlInferenceSessionOptions options)
        : this(modelBytes, options, logger: null) { }

    /// <inheritdoc cref="DmlInferenceSession(byte[], DmlInferenceSessionOptions)" />
    // CA2000: see the string-path overload above — the base owns and
    // disposes the SessionOptions.
#pragma warning disable CA2000
    public DmlInferenceSession(
        byte[] modelBytes, DmlInferenceSessionOptions options, ILogger<DmlInferenceSession>? logger)
        : base(modelBytes, BuildSessionOptions(options, static o => o.AppendExecutionProvider_DML()))
    {
        LogFreeDimensionsLeft(options, logger);
    }
#pragma warning restore CA2000

    private DmlInferenceSession(
        string modelPath, DeviceBinding device, DmlInferenceSessionOptions options, ILogger<DmlInferenceSession>? logger)
        : base(modelPath, device.Options)
    {
        _device = device;
        LogFreeDimensionsLeft(options, logger);
    }

    private DmlInferenceSession(
        byte[] modelBytes, DeviceBinding device, DmlInferenceSessionOptions options, ILogger<DmlInferenceSession>? logger)
        : base(modelBytes, device.Options)
    {
        _device = device;
        LogFreeDimensionsLeft(options, logger);
    }

    /// <summary>
    /// Loads a model from <paramref name="modelPath"/> and runs DirectML on
    /// <paramref name="device"/> and <paramref name="commandQueue"/>, so the session can take
    /// <see cref="DeviceTensor"/> inputs on that device.
    /// </summary>
    /// <param name="modelPath">The model file.</param>
    /// <param name="device">An <c>ID3D12Device*</c>. The session holds no reference to it beyond DirectML's own.</param>
    /// <param name="commandQueue">An <c>ID3D12CommandQueue*</c> of type compute or direct on <paramref name="device"/>.</param>
    /// <param name="logger">Unused without options.</param>
    public static DmlInferenceSession OnDevice(
        string modelPath, nint device, nint commandQueue, ILogger<DmlInferenceSession>? logger = null) =>
        OnDevice(modelPath, device, commandQueue, DefaultOptions, logger);

    /// <inheritdoc cref="OnDevice(string, nint, nint, ILogger{DmlInferenceSession}?)" />
    /// <param name="modelBytes">The model.</param>
    /// <param name="device">An <c>ID3D12Device*</c>. The session holds no reference to it beyond DirectML's own.</param>
    /// <param name="commandQueue">An <c>ID3D12CommandQueue*</c> of type compute or direct on <paramref name="device"/>.</param>
    /// <param name="logger">Unused without options.</param>
    public static DmlInferenceSession OnDevice(
        byte[] modelBytes, nint device, nint commandQueue, ILogger<DmlInferenceSession>? logger = null) =>
        OnDevice(modelBytes, device, commandQueue, DefaultOptions, logger);

    /// <inheritdoc cref="OnDevice(string, nint, nint, ILogger{DmlInferenceSession}?)" />
    /// <param name="modelPath">The model file.</param>
    /// <param name="device">An <c>ID3D12Device*</c>. The session holds no reference to it beyond DirectML's own.</param>
    /// <param name="commandQueue">An <c>ID3D12CommandQueue*</c> of type compute or direct on <paramref name="device"/>.</param>
    /// <param name="options">How the model loads.</param>
    /// <param name="logger">Where the session reports input dimensions left free after loading.</param>
    public static DmlInferenceSession OnDevice(
        string modelPath,
        nint device,
        nint commandQueue,
        DmlInferenceSessionOptions options,
        ILogger<DmlInferenceSession>? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        ArgumentNullException.ThrowIfNull(options);
        var binding = DeviceBinding.Create(device, commandQueue, options);
        try
        {
            return new DmlInferenceSession(modelPath, binding, options, logger);
        }
        catch
        {
            binding.Release(disposeOptions: true);
            throw;
        }
    }

    /// <inheritdoc cref="OnDevice(string, nint, nint, ILogger{DmlInferenceSession}?)" />
    /// <param name="modelBytes">The model.</param>
    /// <param name="device">An <c>ID3D12Device*</c>. The session holds no reference to it beyond DirectML's own.</param>
    /// <param name="commandQueue">An <c>ID3D12CommandQueue*</c> of type compute or direct on <paramref name="device"/>.</param>
    /// <param name="options">How the model loads.</param>
    /// <param name="logger">Where the session reports input dimensions left free after loading.</param>
    public static DmlInferenceSession OnDevice(
        byte[] modelBytes,
        nint device,
        nint commandQueue,
        DmlInferenceSessionOptions options,
        ILogger<DmlInferenceSession>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(modelBytes);
        ArgumentNullException.ThrowIfNull(options);
        var binding = DeviceBinding.Create(device, commandQueue, options);
        try
        {
            return new DmlInferenceSession(modelBytes, binding, options, logger);
        }
        catch
        {
            binding.Release(disposeOptions: true);
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// True for a D3D12 tensor on this session's device, when the session was built with
    /// <see cref="OnDevice(string, nint, nint, ILogger{DmlInferenceSession}?)"/>.
    /// </remarks>
    public bool CanBind(in DeviceTensor tensor) =>
        _device is not null
        && tensor.Kind == DeviceTensorKind.D3D12
        && tensor.Buffer != 0
        && tensor.Device != 0
        && D3D12Native.Identity(tensor.Device) == _device.DeviceIdentity;

    /// <inheritdoc />
    public void Run(IReadOnlyDictionary<string, DeviceTensor> inputs, IReadOnlyDictionary<string, ICpuTensor> outputs)
    {
        using var run = BeginRun();
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        if (_device is null)
        {
            throw new InvalidOperationException(
                "This session runs on a device ONNX Runtime created, so it binds no device input. "
                    + "Build it with DmlInferenceSession.OnDevice.");
        }

        ValidateNames(inputs.Keys, InputNames, "input");
        foreach (var (name, tensor) in inputs)
        {
            if (!CanBind(tensor))
            {
                throw new ArgumentException(
                    $"Input '{name}' is a {tensor.Kind} tensor this session cannot bind: it must be a D3D12 "
                        + "buffer on the session's device.",
                    nameof(inputs));
            }

            // ORT would refuse a mismatch too, but only once the buffer is wrapped and bound.
            string? mismatch = DeviceInputFit.Mismatch(
                InputShapes[IndexOfInput(name)],
                Session.InputMetadata[name].ElementDataType,
                MapDType(tensor.Dtype),
                tensor);
            if (mismatch is not null)
                throw new ArgumentException($"Input '{name}' does not fit the model: {mismatch}.", nameof(inputs));
        }

        // The stage that wrote a buffer may be on another queue; DirectML's work on this one starts
        // after every write it reads.
        foreach (var (fence, value) in ReadyWaits.For(inputs.Values))
            D3D12Native.QueueWait(_device.Queue, fence, value);

        var bound = new List<(string Name, OrtValue Value)>(inputs.Count);
        var allocations = new List<nint>(inputs.Count);
        try
        {
            foreach (var (name, tensor) in inputs)
            {
                nint allocation = OrtDmlApi.CreateAllocation(tensor.Buffer);
                allocations.Add(allocation);
                var value = OrtValue.CreateTensorValueWithData(
                    _device.Memory,
                    MapDType(tensor.Dtype),
                    ToLongShape(tensor.Shape),
                    allocation,
                    tensor.Shape.ByteCount(tensor.Dtype));
                bound.Add((name, value));
            }

            RunWithHostOutputs(
                NoHostInputs,
                outputs,
                binding =>
                {
                    foreach (var (name, value) in bound)
                        binding.BindInput(name, value);
                });
        }
        finally
        {
            foreach (var (_, value) in bound)
                value.Dispose();
            foreach (nint allocation in allocations)
                OrtDmlApi.FreeAllocation(allocation);
        }
    }

    private int IndexOfInput(string name)
    {
        for (int i = 0; i < InputNames.Count; i++)
        {
            if (InputNames[i] == name)
                return i;
        }

        throw new ArgumentException($"The model has no input '{name}'.", nameof(name));
    }

    /// <summary>
    /// Reports each input dimension the overrides left free. A misspelt name leaves its dimension
    /// free and changes nothing else, so this is where it shows.
    /// </summary>
    private void LogFreeDimensionsLeft(DmlInferenceSessionOptions options, ILogger<DmlInferenceSession>? logger)
    {
        if (logger is null || options.FreeDimensions.Count == 0)
            return;

        for (int i = 0; i < InputNames.Count; i++)
        {
            var names = Session.InputMetadata[InputNames[i]].SymbolicDimensions;
            var free = new List<string>();
            for (int d = 0; d < InputShapes[i].Count; d++)
            {
                if (InputShapes[i][d] < 0)
                    free.Add(d < names.Length && names[d].Length > 0 ? names[d] : $"#{d}");
            }

            if (free.Count > 0)
            {
                LogFreeDimensionsLeft(
                    logger, InputNames[i], string.Join(", ", free), string.Join(", ", options.FreeDimensions.Keys));
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Input '{Input}' still has free dimensions [{Free}] after the overrides [{Overrides}]. An override whose name the model does not use is ignored.")]
    private static partial void LogFreeDimensionsLeft(ILogger logger, string input, string free, string overrides);

    /// <inheritdoc />
    protected override void DisposeProviderResources() => _device?.Release(disposeOptions: false);

    private static readonly IReadOnlyDictionary<string, ICpuTensor> NoHostInputs =
        new Dictionary<string, ICpuTensor>();

    private static readonly DmlInferenceSessionOptions DefaultOptions = new();

    private static SessionOptions BuildSessionOptions(
        DmlInferenceSessionOptions sessionOptions, Action<SessionOptions> appendProvider)
    {
        ArgumentNullException.ThrowIfNull(sessionOptions);
        var options = new SessionOptions();
        try
        {
            // DirectML EP needs the graph optimizer set to BASIC; the
            // EP's pattern matcher requires conv/matmul/etc. to be in
            // their canonical shape before DML rewrites.
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC;
            options.EnableMemoryPattern = false;   // required by DML EP
            foreach (var (name, size) in sessionOptions.FreeDimensions)
                options.AddFreeDimensionOverrideByName(name, size);
            appendProvider(options);
            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }

    /// <summary>
    /// What a device-bound session holds beyond ORT's session: the options that put DirectML on the
    /// caller's queue, a reference to that queue for its waits, the DirectML device, and the memory
    /// info that types a DirectML allocation.
    /// </summary>
    private sealed class DeviceBinding
    {
        private readonly nint _dmlDevice;

        private DeviceBinding(SessionOptions options, nint queue, nint dmlDevice, nint deviceIdentity, OrtMemoryInfo memory)
        {
            Options = options;
            Queue = queue;
            _dmlDevice = dmlDevice;
            DeviceIdentity = deviceIdentity;
            Memory = memory;
        }

        public SessionOptions Options { get; }

        public nint Queue { get; }

        public nint DeviceIdentity { get; }

        public OrtMemoryInfo Memory { get; }

        public static DeviceBinding Create(nint device, nint commandQueue, DmlInferenceSessionOptions sessionOptions)
        {
            if (device == 0)
                throw new ArgumentNullException(nameof(device));
            if (commandQueue == 0)
                throw new ArgumentNullException(nameof(commandQueue));

            nint identity = D3D12Native.Identity(device);
            if (D3D12Native.DeviceIdentityOf(commandQueue) != identity)
                throw new ArgumentException("The command queue belongs to another device.", nameof(commandQueue));

            nint dmlDevice = D3D12Native.CreateDmlDevice(device);
            SessionOptions? options = null;
            OrtMemoryInfo? memory = null;
            try
            {
                options = BuildSessionOptions(sessionOptions, o => OrtDmlApi.AppendDirectML(o, dmlDevice, commandQueue));
                memory = new OrtMemoryInfo("DML", OrtAllocatorType.DeviceAllocator, 0, OrtMemType.Default);
                Marshal.AddRef(commandQueue);
                return new DeviceBinding(options, commandQueue, dmlDevice, identity, memory);
            }
            catch
            {
                memory?.Dispose();
                options?.Dispose();
                Marshal.Release(dmlDevice);
                throw;
            }
        }

        /// <summary>
        /// Drops this binding's references. The session disposes the options once it owns them, so
        /// only a session that failed to load passes <paramref name="disposeOptions"/>.
        /// </summary>
        public void Release(bool disposeOptions)
        {
            Memory.Dispose();
            if (disposeOptions)
                Options.Dispose();
            Marshal.Release(Queue);
            Marshal.Release(_dmlDevice);
        }
    }
}
