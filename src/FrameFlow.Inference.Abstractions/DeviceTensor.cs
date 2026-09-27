// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Inference;

/// <summary>
/// A model input already in GPU memory: the buffer, the device it lives on, its shape and element
/// type, and when it is written. What a device-side stage such as <c>D3D12ImageToTensor</c> hands an
/// <see cref="IDeviceInputSession"/>.
/// </summary>
/// <remarks>
/// <para>
/// The handles are borrowed. Whoever owns the buffer keeps it, its device and its fence alive, and
/// leaves the buffer unchanged, until the run that reads it has returned.
/// </para>
/// <para>
/// A session waits for <see cref="ReadyFence"/> to reach <see cref="ReadyValue"/> on the GPU before
/// it reads the buffer, so the stage that wrote it may use another queue. A <see cref="ReadyFence"/>
/// of zero means the buffer is already written.
/// </para>
/// <para>
/// For <see cref="DeviceTensorKind.D3D12"/> the buffer is in <c>D3D12_RESOURCE_STATE_UNORDERED_ACCESS</c>.
/// </para>
/// </remarks>
/// <param name="Kind">The GPU API the handles belong to.</param>
/// <param name="Buffer">The buffer holding the tensor, from its first byte.</param>
/// <param name="Device">The device the buffer lives on.</param>
/// <param name="Shape">The tensor's shape.</param>
/// <param name="Dtype">The tensor's element type.</param>
/// <param name="ReadyFence">A fence that reaches <paramref name="ReadyValue"/> once the buffer is written, or zero.</param>
/// <param name="ReadyValue">The value <paramref name="ReadyFence"/> reaches once the buffer is written.</param>
public readonly record struct DeviceTensor(
    DeviceTensorKind Kind,
    nint Buffer,
    nint Device,
    TensorShape Shape,
    DType Dtype,
    nint ReadyFence,
    ulong ReadyValue);
