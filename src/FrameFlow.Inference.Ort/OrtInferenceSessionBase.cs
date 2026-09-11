// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using System.Collections.ObjectModel;
using FrameFlow.Graph;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FrameFlow.Inference;

/// <summary>
/// Shared host→ORT staging base for the execution-provider session
/// wrappers (<c>FrameFlow.Inference.Cuda.CudaInferenceSession</c>,
/// <c>FrameFlow.Inference.Dml.DmlInferenceSession</c>, future
/// <c>FrameFlow.Inference.TensorRT</c>). Per ADR-0049 §3 the binding
/// pipeline is uniform across EPs — "every wrapper binds host memory
/// via <see cref="ICpuTensor"/>; per-EP differences are confined to
/// bootstrap and session-options configuration." This base owns that
/// uniform pipeline so each EP subclass is reduced to exactly its
/// <c>BuildSessionOptions()</c> factory (plus, for CUDA, its
/// device-pointer escape hatch).
/// </summary>
/// <remarks>
/// <para>
/// <b>Host-memory binding.</b> Consumers call the dictionary-form
/// <see cref="Run(IReadOnlyDictionary{string, ICpuTensor}, IReadOnlyDictionary{string, ICpuTensor})"/>
/// with <see cref="ICpuTensor"/> inputs and pre-allocated
/// <see cref="ICpuTensor"/> outputs. The EP stages host→device at the
/// inference boundary (CUDA EP via <c>cudaMemcpyAsync</c>, DirectML EP
/// via D3D12 upload buffers, TensorRT EP via CUDA staging).
/// </para>
/// <para>
/// <b>Threading.</b> A single session is safe for sequential Run
/// calls. Concurrent calls against one session are not supported in
/// V1 — the binding lifecycle here is per-call and not yet re-entrant.
/// Multiple consumers hold their own sessions.
/// </para>
/// <para>
/// <b>Session options.</b> The derived EP supplies a fully-configured
/// <see cref="SessionOptions"/> to the protected constructor. The
/// options must be built before construction (the base creates the
/// <see cref="InferenceSession"/> from them), so each EP exposes a
/// static <c>BuildSessionOptions()</c> factory and passes its result
/// through the <c>base(...)</c> initializer rather than overriding an
/// instance method that cannot run before <c>this</c> exists.
/// </para>
/// </remarks>
public abstract class OrtInferenceSessionBase : IInferenceSession
{
    private readonly InferenceSession _session;
    private readonly SessionOptions _sessionOptions;
    private readonly RunOptions _runOptions;

    /// <summary>
    /// CPU memory info used to type host-memory <see cref="OrtValue"/>s
    /// bound from <see cref="ICpuTensor"/> inputs / outputs.
    /// </summary>
    protected readonly OrtMemoryInfo CpuMemoryInfo;

    private bool _disposed;

    /// <summary>Names of the model's inputs, in declaration order.</summary>
    public IReadOnlyList<string> InputNames { get; }

    /// <summary>Names of the model's outputs, in declaration order.</summary>
    public IReadOnlyList<string> OutputNames { get; }

    /// <inheritdoc />
    public IReadOnlyList<IReadOnlyList<long>> InputShapes { get; }

    /// <inheritdoc />
    public IReadOnlyList<IReadOnlyList<long>> OutputShapes { get; }

    /// <summary>
    /// The underlying ORT session. Exposed to derived EPs that need
    /// session-level operations beyond the shared host-binding path
    /// (e.g. the CUDA device-pointer escape hatch's IoBinding loop).
    /// </summary>
    protected InferenceSession Session => _session;

    /// <summary>The shared per-call run options. Exposed to derived EPs.</summary>
    protected RunOptions RunOptions => _runOptions;

    /// <summary>True once <see cref="Dispose"/> has run.</summary>
    protected bool IsDisposed => _disposed;

    /// <summary>
    /// Loads a model from <paramref name="modelPath"/> using the
    /// EP-configured <paramref name="sessionOptions"/>.
    /// </summary>
    protected OrtInferenceSessionBase(string modelPath, SessionOptions sessionOptions)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        ArgumentNullException.ThrowIfNull(sessionOptions);
        _sessionOptions = sessionOptions;
        _session = new InferenceSession(modelPath, _sessionOptions);
        _runOptions = new RunOptions();
        CpuMemoryInfo = OrtMemoryInfo.DefaultInstance;
        InputNames = new ReadOnlyCollection<string>([.. _session.InputNames]);
        OutputNames = new ReadOnlyCollection<string>([.. _session.OutputNames]);
        InputShapes = BuildShapes(_session.InputMetadata, InputNames);
        OutputShapes = BuildShapes(_session.OutputMetadata, OutputNames);
    }

    /// <summary>
    /// Loads a model from <paramref name="modelBytes"/> using the
    /// EP-configured <paramref name="sessionOptions"/>.
    /// </summary>
    protected OrtInferenceSessionBase(byte[] modelBytes, SessionOptions sessionOptions)
    {
        ArgumentNullException.ThrowIfNull(modelBytes);
        ArgumentNullException.ThrowIfNull(sessionOptions);
        _sessionOptions = sessionOptions;
        _session = new InferenceSession(modelBytes, _sessionOptions);
        _runOptions = new RunOptions();
        CpuMemoryInfo = OrtMemoryInfo.DefaultInstance;
        InputNames = new ReadOnlyCollection<string>([.. _session.InputNames]);
        OutputNames = new ReadOnlyCollection<string>([.. _session.OutputNames]);
        InputShapes = BuildShapes(_session.InputMetadata, InputNames);
        OutputShapes = BuildShapes(_session.OutputMetadata, OutputNames);
    }

    /// <summary>
    /// Runs the model with the supplied <see cref="ICpuTensor"/>
    /// inputs and writes outputs into the supplied
    /// <see cref="ICpuTensor"/> outputs. The EP stages host→device
    /// internally.
    /// </summary>
    public void Run(
        IReadOnlyDictionary<string, ICpuTensor> inputs,
        IReadOnlyDictionary<string, ICpuTensor> outputs
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);

        ValidateNames(inputs.Keys, InputNames, "input");
        ValidateNames(outputs.Keys, OutputNames, "output");

        using var binding = _session.CreateIoBinding();
        var capacity = inputs.Count + outputs.Count;
        var boundValues = new List<OrtValue>(capacity);
        var pins = new List<MemoryHandle>(capacity);

        try
        {
            // CA2000: BindCpuTensor transfers the value to boundValues, which
            // the finally below disposes. The add happens inside BindPinned,
            // one frame down, and the analyzer does not follow it there.
#pragma warning disable CA2000
            foreach (var (name, tensor) in inputs)
            {
                var value = BindCpuTensor(tensor, boundValues, pins);
                binding.BindInput(name, value);
            }
            foreach (var (name, tensor) in outputs)
            {
                var value = BindCpuTensor(tensor, boundValues, pins);
                binding.BindOutput(name, value);
            }
#pragma warning restore CA2000

            _session.RunWithBinding(_runOptions, binding);
        }
        finally
        {
            foreach (var value in boundValues)
                value.Dispose();
            // After the OrtValues: they hold the addresses these pins protect.
            foreach (var pin in pins)
                pin.Dispose();
        }
    }

    /// <summary>
    /// Convenience overload for single-input / single-output models.
    /// </summary>
    public void Run(ICpuTensor input, ICpuTensor output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (InputNames.Count != 1 || OutputNames.Count != 1)
        {
            throw new InvalidOperationException(
                $"Model has {InputNames.Count} input(s) and {OutputNames.Count} output(s); "
                    + "use the dictionary-form Run() for multi-input / multi-output models."
            );
        }
        Run(
            new Dictionary<string, ICpuTensor> { [InputNames[0]] = input },
            new Dictionary<string, ICpuTensor> { [OutputNames[0]] = output }
        );
    }

    /// <summary>
    /// Binds one host tensor as an <see cref="OrtValue"/> over its own
    /// buffer, registering both the value and the pin that keeps that
    /// buffer in place with the caller's lifetime lists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The pin must outlive the value.</b>
    /// <c>OrtValue.CreateTensorValueWithData</c> does not copy and does not
    /// own the buffer — it stores the raw address and dereferences it later,
    /// inside <c>RunWithBinding</c>. Keeping the buffer valid until then is
    /// the caller's obligation.
    /// </para>
    /// <para>
    /// A <c>fixed</c> block cannot discharge that obligation: it releases at
    /// its closing brace, which is before this method even returns.
    /// <see cref="ICpuTensor.Bytes"/> is backed by a pooled <c>byte[]</c> —
    /// an ordinary movable managed array — so between binding and running,
    /// the allocations made by the remaining binds are enough to trigger a
    /// compacting GC that relocates an already-bound buffer and leaves ORT
    /// reading freed memory. So the pin is taken here and released by
    /// <see cref="Run(IReadOnlyDictionary{string, ICpuTensor}, IReadOnlyDictionary{string, ICpuTensor})"/>
    /// once the run has completed.
    /// </para>
    /// </remarks>
    private OrtValue BindCpuTensor(
        ICpuTensor tensor,
        List<OrtValue> boundValues,
        List<MemoryHandle> pins
    )
    {
        var elementType = MapDType(tensor.Dtype);
        var shape = ToLongShape(tensor.Shape);

        return BindPinned(
            tensor,
            boundValues,
            pins,
            address =>
                OrtValue.CreateTensorValueWithData(
                    CpuMemoryInfo,
                    elementType,
                    shape,
                    address,
                    tensor.ByteCount
                ),
            static value => value.Dispose()
        );
    }

    /// <summary>
    /// The whole of a bind except the ORT call: pin, register, build the
    /// value over the pinned address, register that too.
    /// </summary>
    /// <remarks>
    /// Generic over the value so the sequence can be driven in a test with
    /// no <c>InferenceSession</c> — constructing a real <see cref="OrtValue"/>
    /// needs the native runtime, and the property worth pinning down is that
    /// the address handed to <paramref name="createValue"/> is the one the
    /// registered pin protects. That is what the bug got wrong, and it is
    /// checkable without ORT.
    /// </remarks>
    internal static TValue BindPinned<TValue>(
        ICpuTensor tensor,
        ICollection<TValue> boundValues,
        ICollection<MemoryHandle> pins,
        Func<IntPtr, TValue> createValue,
        Action<TValue> disposeValue
    )
    {
        ArgumentNullException.ThrowIfNull(boundValues);
        ArgumentNullException.ThrowIfNull(createValue);
        ArgumentNullException.ThrowIfNull(disposeValue);

        // Pinned and registered before the value exists, so a throw below
        // still leaves the pin owned by the caller's finally.
        var address = PinAndRegister(tensor, pins);

        var value = createValue(address);
        try
        {
            boundValues.Add(value);
        }
        catch
        {
            // Ownership never reached the caller's list; nothing else will
            // free the native value.
            disposeValue(value);
            throw;
        }
        return value;
    }

    /// <summary>
    /// Pins <paramref name="tensor"/>, hands the pin to
    /// <paramref name="pins"/>, and returns the address to give ORT — which
    /// is the pinned address itself, read off the registered handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the ownership handoff the bug got wrong, isolated so it can
    /// be tested without an <c>InferenceSession</c>. The address is derived
    /// from the same handle that was just registered, so an address can
    /// only escape this method if the pin protecting it is already owned by
    /// the caller.
    /// </para>
    /// <para>
    /// The pin is disposed if registration throws. <c>Run</c> preallocates
    /// its lists to the exact number of binds, so today they cannot grow,
    /// but that rests on <c>IReadOnlyDictionary.Count</c> agreeing with what
    /// enumeration yields — the caller's type to choose, not ours to assume.
    /// The registries are <see cref="ICollection{T}"/> so this path is
    /// reachable from a test.
    /// </para>
    /// </remarks>
    internal static IntPtr PinAndRegister(ICpuTensor tensor, ICollection<MemoryHandle> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);

        var pin = PinForBinding(tensor);
        try
        {
            pins.Add(pin);
        }
        catch
        {
            pin.Dispose();
            throw;
        }

        unsafe
        {
            return (IntPtr)pin.Pointer;
        }
    }

    /// <summary>
    /// Pins one host tensor's buffer and hands the pin back to the caller,
    /// who owns it. The address ORT is given is <c>Pointer</c> on the
    /// returned handle, so the pin and the address cannot be separated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Returning the pin is the point.</b> The bug this replaced took
    /// the address inside a <c>fixed</c> block, which releases at its
    /// closing brace — before the method returned, and long before ORT
    /// dereferenced the address. A <c>fixed</c> block cannot be expressed
    /// through this signature: there is no handle for it to return.
    /// </para>
    /// <para>
    /// The buffer must also be at least <see cref="FrameFlow.Graph.ITensor.ByteCount"/>
    /// long, because that is the length ORT is told it may read.
    /// <c>CpuTensor</c> establishes this at construction, but
    /// <see cref="ICpuTensor"/> is an interface and an implementation that
    /// reports more than it exposes would have ORT read off the end of a
    /// pinned buffer. Checked here rather than trusted.
    /// </para>
    /// </remarks>
    internal static MemoryHandle PinForBinding(ICpuTensor tensor)
    {
        ArgumentNullException.ThrowIfNull(tensor);

        var bytes = tensor.Bytes;
        if (bytes.Length < tensor.ByteCount)
        {
            throw new ArgumentException(
                $"Tensor exposes {bytes.Length} bytes but reports a ByteCount "
                    + $"of {tensor.ByteCount}; ORT would be told it may read "
                    + "past the end of the pinned buffer.",
                nameof(tensor)
            );
        }

        return bytes.Pin();
    }

    /// <summary>
    /// Converts a <see cref="TensorShape"/> (int dims) to the
    /// <see cref="long"/>[] shape ORT's <see cref="OrtValue"/> APIs
    /// require. Pure — exposed <c>internal</c> for direct unit testing of
    /// the host→ORT staging contract both EPs share through this base.
    /// </summary>
    internal static long[] ToLongShape(TensorShape shape)
    {
        var dims = new long[shape.Rank];
        for (int i = 0; i < shape.Rank; i++)
            dims[i] = shape[i];
        return dims;
    }

    /// <summary>
    /// Maps a FrameFlow <see cref="DType"/> to its ONNX Runtime
    /// <see cref="TensorElementType"/>. Pure; throws
    /// <see cref="NotSupportedException"/> for a dtype with no ORT
    /// mapping. Exposed <c>internal</c> for direct unit testing.
    /// </summary>
    internal static TensorElementType MapDType(DType dtype) =>
        dtype switch
        {
            DType.Float32 => TensorElementType.Float,
            DType.Float16 => TensorElementType.Float16,
            DType.BFloat16 => TensorElementType.BFloat16,
            DType.Float64 => TensorElementType.Double,
            DType.Int8 => TensorElementType.Int8,
            DType.UInt8 => TensorElementType.UInt8,
            DType.Int16 => TensorElementType.Int16,
            DType.UInt16 => TensorElementType.UInt16,
            DType.Int32 => TensorElementType.Int32,
            DType.UInt32 => TensorElementType.UInt32,
            DType.Int64 => TensorElementType.Int64,
            DType.UInt64 => TensorElementType.UInt64,
            DType.Bool => TensorElementType.Bool,
            _ => throw new NotSupportedException(
                $"DType {dtype} has no ONNX TensorElementType mapping."
            ),
        };

    /// <summary>
    /// Validates that every supplied input / output name is declared by
    /// the model. Shared by the host-binding <see cref="Run(IReadOnlyDictionary{string, ICpuTensor}, IReadOnlyDictionary{string, ICpuTensor})"/>
    /// path and by EP-specific binding paths (e.g. the CUDA
    /// device-pointer escape hatch), so the validation rule has a
    /// single source. <c>protected internal</c> rather than
    /// <c>protected</c>: the <c>protected</c> part keeps it callable by
    /// the EP subclasses in their own assemblies (e.g. CudaInferenceSession's
    /// device-pointer escape hatch), and the <c>internal</c> part makes it
    /// directly unit-testable via InternalsVisibleTo. Pure.
    /// </summary>
    protected internal static void ValidateNames(
        IEnumerable<string> supplied,
        IReadOnlyList<string> expected,
        string kind
    )
    {
        var expectedSet = new HashSet<string>(expected, StringComparer.Ordinal);
        foreach (var name in supplied)
        {
            if (!expectedSet.Contains(name))
            {
                throw new ArgumentException(
                    $"Unknown {kind} name '{name}'. Model {kind}s: [{string.Join(", ", expected)}].",
                    paramName: kind + "s"
                );
            }
        }
    }

    /// <summary>
    /// Thin shell over <see cref="ConvertDims"/>: pulls each name's
    /// <see cref="NodeMetadata.Dimensions"/> (the ORT-typed seam) and
    /// converts it to the public <c>long</c> shape. The ORT type lookup
    /// stays here; the pure dimension transform is in
    /// <see cref="ConvertDims"/>.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<long>> BuildShapes(
        IReadOnlyDictionary<string, NodeMetadata> metadata,
        IReadOnlyList<string> names
    )
    {
        var shapes = new IReadOnlyList<long>[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            // NodeMetadata.Dimensions is int[] with -1 for dynamic dims.
            shapes[i] = ConvertDims(metadata[names[i]].Dimensions);
        }
        return new ReadOnlyCollection<IReadOnlyList<long>>(shapes);
    }

    /// <summary>
    /// Converts one ORT metadata dimension array (<c>int[]</c>, with
    /// <c>-1</c> marking a dynamic dimension) to the immutable
    /// <c>long</c> shape FrameFlow surfaces on
    /// <see cref="IInferenceSession.InputShapes"/> /
    /// <see cref="IInferenceSession.OutputShapes"/>. The <c>-1</c>
    /// dynamic-dim marker is preserved verbatim. Pure; exposed
    /// <c>internal</c> for direct unit testing of the shape-building
    /// contract.
    /// </summary>
    internal static IReadOnlyList<long> ConvertDims(int[] dims)
    {
        var shape = new long[dims.Length];
        for (int d = 0; d < dims.Length; d++)
            shape[d] = dims[d];
        return new ReadOnlyCollection<long>(shape);
    }

    /// <summary>
    /// Disposes the <see cref="CpuMemoryInfo"/> for this session. The
    /// base default is a no-op because <see cref="OrtMemoryInfo.DefaultInstance"/>
    /// is a shared ORT singleton — disposing it corrupts the instance
    /// for any subsequently-created session. EPs that build a
    /// session-owned (non-singleton) memory info override this to
    /// dispose it.
    /// </summary>
    protected virtual void DisposeCpuMemoryInfo()
    {
        // OrtMemoryInfo.DefaultInstance is a shared ORT singleton — do not dispose it.
        // Disposing it here corrupts the instance for any subsequently-created session.
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Disposal order preserved verbatim from the pre-refactor EP
        // sessions: run options, then the EP-specific CPU-memory-info
        // hook (see DisposeCpuMemoryInfo), then the session, then the
        // session options.
        _runOptions.Dispose();
        DisposeCpuMemoryInfo();
        _session.Dispose();
        _sessionOptions.Dispose();
        // No-op for the current finalizer-free sealed EPs; present so a
        // future derived type that adds a finalizer need not re-implement
        // IDisposable (CA1816). Behaviorally inert today.
        GC.SuppressFinalize(this);
    }
}
