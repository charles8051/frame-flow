// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Inference;

/// <summary>
/// The device side of <see cref="ImageToTensor"/> as an inference operator uses it: which frames it
/// reads, the write, and the tensor as an <see cref="IDeviceInputSession"/> binds it.
/// <c>D3D12ImageToTensor</c> is one.
/// </summary>
public interface IDeviceImageToTensor
{
    /// <summary>
    /// The tensor it writes, and how a frame fills it. A stage that cannot write the options'
    /// <see cref="ImageToTensorOptions.Dtype"/> refuses them when it is built.
    /// </summary>
    ImageToTensorOptions Options { get; }

    /// <summary>
    /// The most frames it holds at once, counting the one being written, until the GPU has read
    /// them. The operator declares it, so a player sizes a hardware pool for it.
    /// </summary>
    int MaxHeldFrames { get; }

    /// <summary>True when <paramref name="frame"/> is in GPU memory this stage can read.</summary>
    bool CanWrite(IVideoFrame frame);

    /// <summary>
    /// Writes <paramref name="crop"/> of <paramref name="frame"/> into the tensor and returns the map
    /// from tensor coordinates back to the frame's.
    /// </summary>
    TensorTransform Write(IVideoFrame frame, RotatedRect crop);

    /// <summary>The tensor as of the last <see cref="Write"/>.</summary>
    DeviceTensor DeviceTensor { get; }
}
