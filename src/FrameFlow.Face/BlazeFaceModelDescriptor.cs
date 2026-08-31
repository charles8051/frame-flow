// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Inference;

namespace FrameFlow.Face;

/// <summary>
/// Shape + decode contract for a BlazeFace-family detector: square input
/// side, box/score tensor shapes, the coordinate scales, and the
/// generated SSD anchor table. The face analogue of
/// <c>FrameFlow.Yolo.YoloModelDescriptor</c> — everything the
/// <see cref="BlazeFacePreprocessor"/> and <see cref="BlazeFacePostprocessor"/>
/// need so no BlazeFace constant is hardcoded in the hot path.
/// </summary>
/// <remarks>
/// <para>
/// BlazeFace has <b>two</b> outputs — a box-regressor tensor
/// <c>[1, N, 16]</c> and a score tensor <c>[1, N, 1]</c> — where
/// <c>N</c> is the anchor count. Unlike YOLO's single transposed head,
/// the boxes are offsets relative to <see cref="Anchors"/>, so the
/// descriptor owns the anchor table.
/// </para>
/// <para>
/// The 16 box coords are, in order (BlazeFace uses
/// <c>reverse_output_order</c>, i.e. x before y): <c>x, y, w, h</c> then
/// six <c>(x, y)</c> keypoints. The <c>*_scale</c> values equal the input
/// side for the face models, so they are derived rather than stored
/// separately.
/// </para>
/// </remarks>
public sealed record BlazeFaceModelDescriptor
{
    /// <summary>Square model input side in pixels (128 front / 256 back).</summary>
    public int InputSize { get; }

    /// <summary>Number of SSD boxes the model predicts (== <see cref="Anchors"/> count).</summary>
    public int NumBoxes { get; }

    /// <summary>Values per box row: 4 box coords + <see cref="NumKeypoints"/>·2.</summary>
    public int NumCoords => 4 + NumKeypoints * 2;

    /// <summary>Keypoints regressed per face. BlazeFace: 6.</summary>
    public int NumKeypoints { get; }

    /// <summary>Memory layout of the model's input tensor.</summary>
    public BlazeFaceInputLayout InputLayout { get; }

    /// <summary>The SSD prior-box table the regressor offsets decode against.</summary>
    public IReadOnlyList<SsdAnchor> Anchors { get; }

    /// <summary>Coordinate divisor for decode; equals <see cref="InputSize"/> for the face models.</summary>
    public float CoordinateScale => InputSize;

    /// <summary>Input tensor element count: <c>3 · S · S</c> (NCHW, N=1).</summary>
    public int InputElementCount => 3 * InputSize * InputSize;

    /// <summary>Box-regressor tensor element count: <c>N · 16</c>.</summary>
    public int BoxElementCount => NumBoxes * NumCoords;

    /// <summary>Score tensor element count: <c>N · 1</c>.</summary>
    public int ScoreElementCount => NumBoxes;

    /// <summary>
    /// Builds a descriptor from an anchor <paramref name="options"/> set.
    /// The box count is taken from the generated table, so it can't drift
    /// from the anchors the decoder uses.
    /// </summary>
    public BlazeFaceModelDescriptor(
        SsdAnchorOptions options,
        int numKeypoints = FaceDetection.KeypointCount,
        BlazeFaceInputLayout inputLayout = BlazeFaceInputLayout.Nchw)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.InputSizeWidth != options.InputSizeHeight)
        {
            throw new ArgumentException(
                $"BlazeFace descriptor expects a square input; got "
                    + $"{options.InputSizeWidth}×{options.InputSizeHeight}.",
                nameof(options));
        }
        if (numKeypoints <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numKeypoints), numKeypoints, "NumKeypoints must be positive.");
        }

        InputSize = options.InputSizeWidth;
        NumKeypoints = numKeypoints;
        InputLayout = inputLayout;
        Anchors = SsdAnchorGenerator.Generate(options);
        NumBoxes = Anchors.Count;
    }

    /// <summary>
    /// Anchor config for the stock MediaPipe <b>front-facing</b> /
    /// short-range face model. Pinned to the published
    /// <c>face_detection_front</c> / <c>blaze_face_short_range</c> graph
    /// (Apache-2.0); the generated table matches that model's shipped
    /// anchor CSV row-for-row.
    /// </summary>
    public static SsdAnchorOptions Front128Options { get; } = new()
    {
        InputSizeWidth = 128,
        InputSizeHeight = 128,
        MinScale = 0.1484375f,
        MaxScale = 0.75f,
        NumLayers = 4,
        Strides = [8, 16, 16, 16],
        AspectRatios = [1.0f],
        InterpolatedScaleAspectRatio = 1.0f,
        FixedAnchorSize = true,
    };

    /// <summary>
    /// The stock front-facing model with an <b>NCHW</b> <c>[1,3,128,128]</c>
    /// input: 128×128, 896 boxes, 6 keypoints.
    /// </summary>
    public static BlazeFaceModelDescriptor Front128 { get; } =
        new(Front128Options, inputLayout: BlazeFaceInputLayout.Nchw);

    /// <summary>
    /// The stock front-facing model with the MediaPipe-native <b>NHWC</b>
    /// <c>[1,128,128,3]</c> input (e.g. the Unity
    /// <c>blaze_face_short_range.onnx</c> export). Identical anchors and
    /// outputs to <see cref="Front128"/>; only the input layout differs.
    /// </summary>
    public static BlazeFaceModelDescriptor Front128Nhwc { get; } =
        new(Front128Options, inputLayout: BlazeFaceInputLayout.Nhwc);

    /// <summary>
    /// Picks the matching front-model descriptor for a loaded session by
    /// reading its input layout (<c>[1,3,128,128]</c> → <see cref="Front128"/>,
    /// <c>[1,128,128,3]</c> → <see cref="Front128Nhwc"/>) and validates the
    /// two BlazeFace outputs. Throws on anything else (mirrors
    /// <c>YoloModelDescriptor.FromSession</c>, ADR-0050 §2/§5).
    /// </summary>
    public static BlazeFaceModelDescriptor FromSession(IInferenceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.InputShapes.Count != 1)
        {
            throw new InvalidOperationException(
                $"BlazeFace expects a single input; session has {session.InputShapes.Count}.");
        }

        var input = session.InputShapes[0];
        var descriptor = input switch
        {
            [1, 3, 128, 128] => Front128,
            [1, 128, 128, 3] => Front128Nhwc,
            _ => throw new InvalidOperationException(
                $"Input [{string.Join(",", input)}] is not a 128² BlazeFace input "
                    + "([1,3,128,128] NCHW or [1,128,128,3] NHWC). Pass an explicit descriptor."),
        };
        descriptor.ValidateSession(session);
        return descriptor;
    }

    /// <summary>
    /// Validates that a loaded session matches this descriptor's expected
    /// I/O shape: one <c>[1,3,S,S]</c> input, and two outputs — a
    /// <c>[1,N,16]</c> box tensor and a <c>[1,N,1]</c> score tensor, with
    /// <c>N == NumBoxes</c>. Throws with a descriptive message on a
    /// mismatch (mirrors <c>YoloModelDescriptor.FromSession</c>'s
    /// fail-loud contract, ADR-0050 §5). Output <i>order</i> is not
    /// assumed — the box tensor is identified by its trailing dimension
    /// (16), the score tensor by (1) — because community ONNX exports
    /// disagree on which output comes first.
    /// </summary>
    public void ValidateSession(IInferenceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.InputShapes.Count != 1)
        {
            throw new InvalidOperationException(
                $"BlazeFace expects a single input; session has {session.InputShapes.Count}.");
        }
        var input = session.InputShapes[0];
        bool inputOk = InputLayout switch
        {
            BlazeFaceInputLayout.Nchw =>
                input.Count == 4 && input[1] == 3 && input[2] == InputSize && input[3] == InputSize,
            BlazeFaceInputLayout.Nhwc =>
                input.Count == 4 && input[3] == 3 && input[1] == InputSize && input[2] == InputSize,
            _ => false,
        };
        if (!inputOk)
        {
            string expected = InputLayout == BlazeFaceInputLayout.Nchw
                ? $"[1,3,{InputSize},{InputSize}] (NCHW)"
                : $"[1,{InputSize},{InputSize},3] (NHWC)";
            throw new InvalidOperationException(
                $"Expected input {expected}; got [{string.Join(",", input)}].");
        }

        if (session.OutputShapes.Count != 2)
        {
            throw new InvalidOperationException(
                $"BlazeFace expects two outputs (boxes + scores); session has "
                    + $"{session.OutputShapes.Count}. yolo-style single-head or landmark models "
                    + "are a different architecture.");
        }

        var (boxIdx, scoreIdx) = IdentifyOutputs(session.OutputShapes);
        var box = session.OutputShapes[boxIdx];
        var score = session.OutputShapes[scoreIdx];
        if (box[1] != NumBoxes || score[1] != NumBoxes)
        {
            throw new InvalidOperationException(
                $"Box/score tensors carry {box[1]}/{score[1]} anchors but this descriptor's table "
                    + $"has {NumBoxes}. The model's anchor config does not match Front128 — supply a "
                    + "matching SsdAnchorOptions.");
        }
    }

    /// <summary>
    /// Returns the output indices of the box tensor (trailing dim ==
    /// <see cref="NumCoords"/>) and the score tensor (trailing dim 1),
    /// identifying them by shape rather than declaration order.
    /// </summary>
    public (int BoxIndex, int ScoreIndex) IdentifyOutputs(IReadOnlyList<IReadOnlyList<long>> outputShapes)
    {
        ArgumentNullException.ThrowIfNull(outputShapes);
        if (outputShapes.Count != 2)
        {
            throw new InvalidOperationException(
                $"Expected two outputs; got {outputShapes.Count}.");
        }

        int boxIdx = -1, scoreIdx = -1;
        for (int i = 0; i < 2; i++)
        {
            var shape = outputShapes[i];
            if (shape.Count != 3 || shape[0] != 1)
            {
                throw new InvalidOperationException(
                    $"Expected each output shaped [1,N,*]; got [{string.Join(",", shape)}].");
            }
            long trailing = shape[2];
            if (trailing == NumCoords)
                boxIdx = i;
            else if (trailing == 1)
                scoreIdx = i;
        }

        if (boxIdx < 0 || scoreIdx < 0)
        {
            throw new InvalidOperationException(
                $"Could not identify box ([1,N,{NumCoords}]) and score ([1,N,1]) outputs among "
                    + $"[{string.Join(" | ", outputShapes.Select(s => "[" + string.Join(",", s) + "]"))}].");
        }
        return (boxIdx, scoreIdx);
    }
}
