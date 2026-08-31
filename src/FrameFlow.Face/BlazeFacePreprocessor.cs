// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

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
/// </remarks>
public sealed class BlazeFacePreprocessor
{
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
    }

    /// <summary>
    /// Crops <paramref name="roi"/> from <paramref name="frame"/>, resizes
    /// it to the model input, normalizes to <c>[-1,1]</c>, and writes the
    /// CHW tensor into <paramref name="destination"/> (≥
    /// <see cref="InputElementCount"/> elements). The <paramref name="roi"/>
    /// is returned to the caller to hand to the postprocessor unchanged.
    /// </summary>
    public void Preprocess(IVideoFrame frame, FaceRoi roi, Span<float> destination)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (destination.Length < InputElementCount)
        {
            throw new ArgumentException(
                $"Destination span has {destination.Length} elements; BlazeFace input requires at "
                    + $"least {InputElementCount}.",
                nameof(destination));
        }

        if (frame.Format is not (PixelFormat.Bgra32 or PixelFormat.Rgba32))
        {
            throw new NotSupportedException(
                $"BlazeFace preprocessor expects Bgra32 or Rgba32 input frames; got {frame.Format}.");
        }

        var cpu = frame.AsCpu()
            ?? throw new InvalidOperationException(
                "BlazeFace preprocessor expects CPU-resident frames; AsCpu() returned null.");

        CropResizeAndNormalize(
            sourceBytes: cpu.PlaneY.Span,
            sourceWidth: frame.Width,
            sourceHeight: frame.Height,
            sourceStride: cpu.StrideY,
            isBgra: frame.Format == PixelFormat.Bgra32,
            roi: roi,
            destination: destination);
    }

    /// <summary>
    /// Inline ROI-crop + stretched resize + BGRA/RGBA → RGB normalization
    /// to <c>[-1,1]</c> + HWC → CHW transpose. Sample coordinates are
    /// clamped to the frame, so a ROI that spills past the edge (common
    /// when a person box hugs the border) samples the edge pixel instead
    /// of reading out of bounds.
    /// </summary>
    private void CropResizeAndNormalize(
        ReadOnlySpan<byte> sourceBytes,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        bool isBgra,
        FaceRoi roi,
        Span<float> destination)
    {
        int size = InputSize;
        int channelStride = size * size;
        bool chw = Layout == BlazeFaceInputLayout.Nchw;
        // CHW: R plane | G plane | B plane. HWC: interleaved R,G,B per pixel.
        int rOffset = 0;
        int gOffset = chw ? channelStride : 1;
        int bOffset = chw ? channelStride * 2 : 2;
        int pixelStep = chw ? 1 : 3;

        int rByte = isBgra ? 2 : 0;
        int gByte = 1;
        int bByte = isBgra ? 0 : 2;

        for (int dy = 0; dy < size; dy++)
        {
            // Map destination row to a source y inside the ROI, then clamp.
            float srcYf = roi.Y + (dy + 0.5f) / size * roi.Height;
            int sy = Clamp((int)srcYf, 0, sourceHeight - 1);
            int srcRowOffset = sy * sourceStride;

            for (int dx = 0; dx < size; dx++)
            {
                float srcXf = roi.X + (dx + 0.5f) / size * roi.Width;
                int sx = Clamp((int)srcXf, 0, sourceWidth - 1);
                int srcPixelOffset = srcRowOffset + sx * 4;

                int baseIndex = (dy * size + dx) * pixelStep;
                destination[rOffset + baseIndex] = Normalize(sourceBytes[srcPixelOffset + rByte]);
                destination[gOffset + baseIndex] = Normalize(sourceBytes[srcPixelOffset + gByte]);
                destination[bOffset + baseIndex] = Normalize(sourceBytes[srcPixelOffset + bByte]);
            }
        }
    }

    /// <summary><c>byte [0,255] → [-1, 1]</c>.</summary>
    private static float Normalize(byte value) => value / 127.5f - 1.0f;

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
}
