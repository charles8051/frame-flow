// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Inference;

namespace FrameFlow.Yolo;

/// <summary>
/// Shape contract for a YOLOv8/v11-family detection model: square input
/// side, class count, and class names. Everything the
/// <see cref="Yolov8Preprocessor"/> and <see cref="Yolov8Postprocessor"/>
/// need to stop hardcoding the stock 640×640 / 80-class COCO export
/// (ADR-0050 §1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Anchor count is derived, not stored.</b> A YOLOv8/v11 detect head
/// produces predictions at strides 8/16/32, so the total anchor count is
/// <c>(S/8)² + (S/16)² + (S/32)²</c> for input side <c>S</c> — 8400 at
/// 640, 3549 at 416, 2100 at 320. <see cref="InputSize"/> must therefore
/// be a multiple of 32.
/// </para>
/// <para>
/// <b>Precision is out of scope here.</b> Per ADR-0050 §3 Tier A, the
/// detector's tensor path is FP32 host I/O; FP16-internal
/// (<c>keep_io_types=True</c>) and INT8-dynamic models keep FP32 graph
/// I/O and run on that path unchanged, so they need no descriptor field.
/// True FP16 I/O (Tier B) is a separate, opt-in change.
/// </para>
/// </remarks>
public sealed record YoloModelDescriptor
{
    /// <summary>Square model input side in pixels (multiple of 32).</summary>
    public int InputSize { get; }

    /// <summary>Number of detection classes the head predicts.</summary>
    public int ClassCount { get; }

    /// <summary>Class names, indexed by class id. Count equals <see cref="ClassCount"/>.</summary>
    public IReadOnlyList<string> ClassNames { get; }

    /// <summary>Predictions per frame: <c>(S/8)² + (S/16)² + (S/32)²</c>.</summary>
    public int AnchorCount { get; }

    /// <summary>Input tensor element count: <c>3 · S · S</c> (NCHW, N=1).</summary>
    public int InputElementCount => 3 * InputSize * InputSize;

    /// <summary>Output channels: 4 box coords + <see cref="ClassCount"/> scores.</summary>
    public int OutputChannelCount => 4 + ClassCount;

    /// <summary>Output tensor element count: <c>(4 + C) · A</c>.</summary>
    public int OutputElementCount => OutputChannelCount * AnchorCount;

    public YoloModelDescriptor(int inputSize, int classCount, IReadOnlyList<string>? classNames = null)
    {
        if (inputSize <= 0 || inputSize % 32 != 0)
        {
            throw new ArgumentException(
                $"InputSize must be a positive multiple of 32 (YOLOv8/v11 stride structure); got {inputSize}.",
                nameof(inputSize));
        }
        if (classCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(classCount), classCount, "ClassCount must be positive.");
        }
        if (classNames is not null && classNames.Count != classCount)
        {
            throw new ArgumentException(
                $"classNames has {classNames.Count} entries but classCount is {classCount}.",
                nameof(classNames));
        }

        InputSize = inputSize;
        ClassCount = classCount;
        ClassNames = classNames ?? GenerateGenericNames(classCount);
        AnchorCount = AnchorsFor(inputSize);
    }

    /// <summary>The stock yolov8n / yolo11n export: 640×640, 80 COCO classes.</summary>
    public static YoloModelDescriptor CocoDefault { get; } =
        new(640, CocoClasses.Count, CocoClasses.Names);

    /// <summary>YOLOv8/v11 anchor count for a square input side (multiple of 32).</summary>
    public static int AnchorsFor(int inputSize)
    {
        int s8 = inputSize / 8, s16 = inputSize / 16, s32 = inputSize / 32;
        return s8 * s8 + s16 * s16 + s32 * s32;
    }

    /// <summary>
    /// Infers a descriptor from a loaded session's static input/output
    /// shapes (ADR-0050 §2). Expects input <c>[1,3,S,S]</c> and a
    /// transposed detect head <c>[1, 4+C, A]</c> with
    /// <c>A == AnchorsFor(S)</c>. Throws with a descriptive message on a
    /// dynamic, non-square, or non-YOLOv8/v11-shaped model (ADR-0050 §5) —
    /// callers with such models pass an explicit descriptor instead.
    /// </summary>
    public static YoloModelDescriptor FromSession(IInferenceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.InputShapes.Count == 0 || session.OutputShapes.Count == 0)
        {
            throw new InvalidOperationException(
                "Session exposes no input/output shapes; pass an explicit YoloModelDescriptor.");
        }

        var input = session.InputShapes[0];
        if (input.Count != 4 || input[1] != 3)
        {
            throw new InvalidOperationException(
                $"Expected a [1,3,S,S] input; got [{string.Join(",", input)}]. "
                    + "Pass an explicit YoloModelDescriptor for non-standard inputs.");
        }
        long h = input[2], w = input[3];
        if (h <= 0 || w <= 0 || h != w)
        {
            throw new InvalidOperationException(
                $"Input is dynamic or non-square ([{string.Join(",", input)}]); "
                    + "auto-inference needs a static square input. Pass an explicit YoloModelDescriptor.");
        }
        int inputSize = (int)h;
        if (inputSize % 32 != 0)
        {
            throw new InvalidOperationException(
                $"Input side {inputSize} is not a multiple of 32; not a YOLOv8/v11 detect model?");
        }

        var output = session.OutputShapes[0];
        if (output.Count != 3 || output[0] != 1)
        {
            throw new InvalidOperationException(
                $"Expected a transposed detect head [1,4+C,A]; got [{string.Join(",", output)}]. "
                    + "yolov10 / NMS-free / seg / pose heads are not supported (ADR-0050 §5 / non-goals).");
        }
        long channels = output[1], anchors = output[2];
        int classCount = (int)channels - 4;
        if (classCount <= 0)
        {
            throw new InvalidOperationException(
                $"Output channel count {channels} ≤ 4; cannot be a 4-box + C-class head.");
        }
        int expected = AnchorsFor(inputSize);
        if (anchors != expected)
        {
            throw new InvalidOperationException(
                $"Output anchor count {anchors} does not match the {expected} expected for a "
                    + $"{inputSize}px YOLOv8/v11 head. Output may be laid out [1,A,4+C] (non-transposed) "
                    + "or this is a different architecture; pass an explicit descriptor if you know the layout.");
        }

        var names = classCount == CocoClasses.Count ? CocoClasses.Names : GenerateGenericNames(classCount);
        return new YoloModelDescriptor(inputSize, classCount, names);
    }

    /// <summary>
    /// Non-throwing variant of <see cref="FromSession"/>: returns
    /// <see langword="false"/> with a descriptive <paramref name="error"/> when
    /// the model is not a FrameFlow-compatible head, instead of throwing. Shares
    /// the exact contract the detector enforces at load time, so build/CI
    /// tooling (e.g. <c>scripts/mint_yolo.py validate</c>) and the detector
    /// agree on what "compatible" means.
    /// </summary>
    public static bool TryDescribe(
        IInferenceSession session,
        out YoloModelDescriptor? descriptor,
        out string? error
    )
    {
        try
        {
            descriptor = FromSession(session);
            error = null;
            return true;
        }
        catch (Exception ex)
            when (ex is InvalidOperationException or ArgumentException or ArgumentNullException)
        {
            descriptor = null;
            error = ex.Message;
            return false;
        }
    }

    private static IReadOnlyList<string> GenerateGenericNames(int count)
    {
        var names = new string[count];
        for (int i = 0; i < count; i++)
            names[i] = $"class_{i}";
        return names;
    }
}
