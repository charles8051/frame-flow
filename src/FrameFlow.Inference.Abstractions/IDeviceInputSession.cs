// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Inference;

/// <summary>
/// An <see cref="IInferenceSession"/> that can also take its inputs straight from GPU memory, so a
/// tensor written on the device reaches the model without a copy through the CPU.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IInferenceSession"/> stays host-memory only. A session implements this when its
/// execution provider can bind a device buffer, and <see cref="CanBind"/> says whether it can bind a
/// particular one: the same GPU API and the same device.
/// </para>
/// <para>
/// Outputs stay host tensors. A run returns once they are written, and by then the session has
/// finished reading its device inputs.
/// </para>
/// </remarks>
public interface IDeviceInputSession : IInferenceSession
{
    /// <summary>
    /// True when this session can take <paramref name="tensor"/> as an input: its execution provider
    /// runs on <paramref name="tensor"/>'s device, through its GPU API.
    /// </summary>
    bool CanBind(in DeviceTensor tensor);

    /// <summary>
    /// Runs the model on <paramref name="inputs"/>, read in place on the device, and writes the
    /// results into <paramref name="outputs"/>.
    /// </summary>
    /// <param name="inputs">Map of input name to device tensor. Each must pass <see cref="CanBind"/>.</param>
    /// <param name="outputs">Map of output name to pre-allocated host tensor.</param>
    void Run(IReadOnlyDictionary<string, DeviceTensor> inputs, IReadOnlyDictionary<string, ICpuTensor> outputs);
}
