// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Inference;

/// <summary>
/// A model that turns one frame into a result, split where an inference operator needs it split:
/// the input tensor and the part of the frame that fills it, the session, and how the outputs
/// become a result. The split lets the operator fill the input on the CPU or on the GPU.
/// </summary>
/// <typeparam name="TResult">What one run produces, such as a list of detections.</typeparam>
/// <remarks>
/// The operator calls a model from one thread at a time. Outputs are allocated once from the
/// session's static output shapes, as 32-bit floats, and reused on every run. The input on the CPU
/// path is allocated once too, with the element type <see cref="ImageToTensorOptions.Dtype"/> names.
/// </remarks>
public interface IImageModel<TResult>
{
    /// <summary>
    /// The session the model runs on. When it is an <see cref="IDeviceInputSession"/>, the operator
    /// can hand it a tensor written on the GPU.
    /// </summary>
    IInferenceSession Session { get; }

    /// <summary>The name of the session input the image fills.</summary>
    string InputName { get; }

    /// <summary>The input tensor, and how a frame fills it.</summary>
    ImageToTensorOptions Input { get; }

    /// <summary>The region of <paramref name="frame"/> the model reads.</summary>
    RotatedRect CropFor(IVideoFrame frame);

    /// <summary>
    /// Turns one run's outputs into a result. <paramref name="transform"/> maps tensor coordinates
    /// back to <paramref name="frame"/>'s. The outputs are reused on the next run, so the result
    /// copies what it keeps.
    /// </summary>
    TResult Decode(IReadOnlyDictionary<string, ICpuTensor> outputs, TensorTransform transform, IVideoFrame frame);
}
