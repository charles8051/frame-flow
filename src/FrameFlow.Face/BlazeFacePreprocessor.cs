// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Inference;
using FrameFlow.Media;

namespace FrameFlow.Face;

/// <summary>
/// CPU-side BlazeFace input preprocessing: a <see cref="FaceRoi"/> of a
/// BGRA / RGBA source frame becomes a normalized RGB tensor of shape
/// <c>[1, 3, S, S]</c> in CHW layout (S = <see cref="InputSize"/>).
/// </summary>
/// <remarks>
/// <para>
/// Two things differ from <c>Yolov8Preprocessor</c>:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Normalization is <c>[-1, 1]</c></b>, not <c>[0, 1]</c> —
/// <c>pixel / 127.5 − 1</c>. BlazeFace was trained on this range; feeding
/// it <c>/255</c> data quietly wrecks detection.
/// </description></item>
/// <item><description>
/// <b>It crops a ROI</b> rather than resizing the whole frame. The
/// stretched resize samples only inside the <see cref="FaceRoi"/>, so the
/// model sees just the person region and its normalized outputs map
/// linearly back via <see cref="FaceRoi.ToSource"/>.
/// </description></item>
/// </list>
/// <para>
/// Like the YOLO preprocessor this uses a stretched (non-letterboxed)
/// resize; face detection tolerates modest aspect distortion, and the
/// ROI is typically close to square.
/// </para>
/// <para>
/// The pixel work is <see cref="ImageToTensor"/>'s: the ROI, stretched,
/// nearest-neighbour, <c>[-1, 1]</c>, RGB, in the model's layout.
/// </para>
/// </remarks>
public sealed class BlazeFacePreprocessor
{
    private readonly ImageToTensorOptions _options;

    /// <summary>The input tensor, and how a frame fills it.</summary>
    internal ImageToTensorOptions Options => _options;

    /// <summary>Model input image side length in pixels.</summary>
    public int InputSize { get; }

    /// <summary>Memory layout the tensor is written in.</summary>
    public BlazeFaceInputLayout Layout { get; }

    /// <summary>Total elements in the input tensor (3 · S · S).</summary>
    public int InputElementCount => 3 * InputSize * InputSize;

    /// <summary>Builds a preprocessor for a square model input of <paramref name="inputSize"/> px.</summary>
    public BlazeFacePreprocessor(int inputSize = 128, BlazeFaceInputLayout layout = BlazeFaceInputLayout.Nchw)
    {
        if (inputSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputSize), inputSize, "InputSize must be positive.");
        }
        InputSize = inputSize;
        Layout = layout;
        _options = new ImageToTensorOptions(inputSize, inputSize)
        {
            Sampling = ImageSampling.Nearest,
            Normalization = TensorNormalization.MinusOneToOne,
            Layout = layout == BlazeFaceInputLayout.Nhwc ? TensorLayout.Nhwc : TensorLayout.Nchw,
        };
    }

    /// <summary>
    /// Crops <paramref name="roi"/> from <paramref name="frame"/>, resizes
    /// it to the model input, normalizes to <c>[-1,1]</c>, and writes the
    /// tensor into <paramref name="destination"/> (≥
    /// <see cref="InputElementCount"/> elements). The <paramref name="roi"/>
    /// is returned to the caller to hand to the postprocessor unchanged.
    /// A ROI that spills past the frame's edge samples the edge pixel.
    /// </summary>
    /// <exception cref="ArgumentException">The ROI has no area.</exception>
    /// <exception cref="NotSupportedException">The frame is not Bgra32 or Rgba32.</exception>
    /// <exception cref="InvalidOperationException">The frame is not on the CPU.</exception>
    public void Preprocess(IVideoFrame frame, FaceRoi roi, Span<float> destination)
    {
        ArgumentNullException.ThrowIfNull(frame);

        ImageToTensor.Write(
            frame, RotatedRect.FromBounds(roi.X, roi.Y, roi.Width, roi.Height), _options, destination);
    }
}
