using System.Runtime.InteropServices;
using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>
/// <see cref="OnnxModel.Negate"/> as an image model over the whole frame. Its result is the
/// negated input tensor, so a test can check it exactly against the tensor written.
/// </summary>
internal sealed class NegateModel(DmlInferenceSession session, ImageToTensorOptions input) : IImageModel<float[]>
{
    public IInferenceSession Session => session;

    public string InputName => "x";

    public ImageToTensorOptions Input => input;

    public RotatedRect CropFor(IVideoFrame frame) => RotatedRect.Whole(frame);

    public float[] Decode(IReadOnlyDictionary<string, ICpuTensor> outputs, TensorTransform transform, IVideoFrame frame) =>
        MemoryMarshal.Cast<byte, float>(outputs["y"].Bytes.Span).ToArray();
}
