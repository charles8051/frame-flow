using FrameFlow.Yolo;
using Xunit;

namespace FrameFlow.Yolo.Tests;

/// <summary>
/// Covers the ADR-0050 §4 class allow-list. Uses a 32px descriptor — the
/// smallest valid input — so the output tensor is just (4+C)·21 floats and
/// can be hand-built. Layout is channel-major: index = channel·anchors + anchor.
/// </summary>
public sealed class Yolov8PostprocessorTests
{
    private const int Size = 32;          // -> 21 anchors ((32/8)²+(32/16)²+(32/32)²)
    private const int Classes = 3;
    private static readonly string[] Names = ["a", "b", "c"];

    private static YoloModelDescriptor Descriptor() => new(Size, Classes, Names);

    // One detectable anchor (#0): a valid box + per-class scores. All other
    // anchors are left at score 0 (below the 0.25 threshold) so exactly one
    // candidate survives and the chosen class is unambiguous.
    private static float[] OneAnchorOutput(float s0, float s1, float s2)
    {
        var d = Descriptor();
        int a = d.AnchorCount; // 21
        var o = new float[d.OutputElementCount]; // (4+3)*21
        o[0 * a + 0] = 16f; // cx
        o[1 * a + 0] = 16f; // cy
        o[2 * a + 0] = 8f;  // w
        o[3 * a + 0] = 8f;  // h
        o[4 * a + 0] = s0;  // class 0 score
        o[5 * a + 0] = s1;  // class 1 score
        o[6 * a + 0] = s2;  // class 2 score
        return o;
    }

    [Fact]
    public void Decode_Unfiltered_PicksArgmaxOverAllClasses()
    {
        var post = new Yolov8Postprocessor(Descriptor());
        var dets = post.Decode(OneAnchorOutput(0.90f, 0.95f, 0.10f), 1f, 1f);

        var det = Assert.Single(dets);
        Assert.Equal(1, det.ClassId); // class 1 (0.95) is the global max
    }

    [Fact]
    public void Decode_FilteredToClass0_IgnoresHigherScoringOtherClass()
    {
        // class 1 scores higher (0.95) but is filtered out; class 0 wins.
        var post = new Yolov8Postprocessor(Descriptor(), classFilter: [0]);
        var dets = post.Decode(OneAnchorOutput(0.90f, 0.95f, 0.10f), 1f, 1f);

        var det = Assert.Single(dets);
        Assert.Equal(0, det.ClassId);
        Assert.Equal(0.90f, det.Confidence, 3);
    }

    [Fact]
    public void Decode_FilteredToLowScoringClass_DropsBelowThreshold()
    {
        // Only class 2 considered; its 0.10 score is below the 0.25 default.
        var post = new Yolov8Postprocessor(Descriptor(), classFilter: [2]);
        var dets = post.Decode(OneAnchorOutput(0.90f, 0.95f, 0.10f), 1f, 1f);

        Assert.Empty(dets);
    }

    [Fact]
    public void DecodedClasses_DefaultsToAllClasses()
    {
        var post = new Yolov8Postprocessor(Descriptor());
        Assert.Equal(new[] { 0, 1, 2 }, post.DecodedClasses);
    }

    [Fact]
    public void DecodedClasses_DedupesAndSortsFilter()
    {
        var post = new Yolov8Postprocessor(Descriptor(), classFilter: [2, 0, 0]);
        Assert.Equal(new[] { 0, 2 }, post.DecodedClasses);
    }

    [Fact]
    public void Ctor_ThrowsOnOutOfRangeFilterId()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new Yolov8Postprocessor(Descriptor(), classFilter: [Classes]));
}
