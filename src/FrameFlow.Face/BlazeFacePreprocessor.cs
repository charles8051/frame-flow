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
/// <b>It crops a ROI</b> rather than resizing the whole frame, so the
/// model sees just the person region.
/// </description></item>
/// </list>
/// <para>
/// <b>It letterboxes</b>, as MediaPipe's detector does: the ROI keeps its
/// aspect and is centred in the square input between black bars, and
/// anything past the frame's edge is black too. A stretched non-square ROI
/// gives every box the ROI's aspect instead of the face's (#473).
/// <see cref="Preprocess"/> returns the mapping back to the frame for
/// <see cref="BlazeFacePostprocessor.Decode(ReadOnlySpan{float}, ReadOnlySpan{float}, TensorTransform)"/>.
/// </para>
/// <para>
/// The pixel work is <see cref="ImageToTensor"/>'s: the ROI, letterboxed,
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
        _options = OptionsFor(inputSize, layout);
    }

    /// <summary>The input tensor for a square model input of <paramref name="inputSize"/> px, and how a frame fills it.</summary>
    internal static ImageToTensorOptions OptionsFor(int inputSize, BlazeFaceInputLayout layout) =>
        new(inputSize, inputSize)
        {
            Fit = ImageFit.Letterbox,
            Border = ImageBorder.Pad,
            PadValue = 0,
            Sampling = ImageSampling.Nearest,
            Normalization = TensorNormalization.MinusOneToOne,
            Layout = layout == BlazeFaceInputLayout.Nhwc ? TensorLayout.Nhwc : TensorLayout.Nchw,
        };

    /// <summary>
    /// Crops <paramref name="roi"/> from <paramref name="frame"/>, letterboxes
    /// it into the model input, normalizes to <c>[-1,1]</c>, and writes the
    /// tensor into <paramref name="destination"/> (≥
    /// <see cref="InputElementCount"/> elements). A ROI that spills past the
    /// frame's edge reads black there.
    /// </summary>
    /// <returns>The mapping from the model's normalized input coordinates to frame pixels.</returns>
    /// <exception cref="ArgumentException">The ROI has no area.</exception>
    /// <exception cref="NotSupportedException">The frame is not Bgra32 or Rgba32.</exception>
    /// <exception cref="InvalidOperationException">The frame is not on the CPU.</exception>
    public TensorTransform Preprocess(IVideoFrame frame, FaceRoi roi, Span<float> destination)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return ImageToTensor.Write(frame, roi.ToCrop(), _options, destination);
    }
}
