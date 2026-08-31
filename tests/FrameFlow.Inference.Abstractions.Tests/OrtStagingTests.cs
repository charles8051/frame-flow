using FrameFlow.Graph;
using FrameFlow.Inference;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// Unit tests for the pure host→ORT staging transforms that
/// <see cref="OrtInferenceSessionBase"/> shares verbatim with both EP
/// wrappers (CUDA + DirectML) per ADR-0049 §3:
/// <see cref="OrtInferenceSessionBase.ToLongShape"/>,
/// <see cref="OrtInferenceSessionBase.MapDType"/>,
/// <see cref="OrtInferenceSessionBase.ValidateNames"/>, and
/// <see cref="OrtInferenceSessionBase.ConvertDims"/>.
/// </summary>
/// <remarks>
/// These exercise the staging *contract* only — no real
/// <c>InferenceSession</c> is constructed (that needs GPU/DML natives + a
/// model). They reference the managed-only ORT assembly for
/// <see cref="TensorElementType"/>, which carries no native payload, so
/// the suite runs in CI with no GPU / CUDA / DML / model dependency.
/// </remarks>
public sealed class OrtStagingTests
{
    // ── ToLongShape: int dims → long[] ───────────────────────────────────

    [Fact]
    public void ToLongShape_ConvertsEachDimPreservingOrder()
    {
        var shape = new TensorShape(1, 3, 640, 480);

        var dims = OrtInferenceSessionBase.ToLongShape(shape);

        Assert.Equal(new long[] { 1, 3, 640, 480 }, dims);
    }

    [Fact]
    public void ToLongShape_SingleDim()
    {
        var dims = OrtInferenceSessionBase.ToLongShape(new TensorShape(8400));
        Assert.Equal(new long[] { 8400 }, dims);
    }

    // ── MapDType: every supported DType → TensorElementType ──────────────

    [Theory]
    [InlineData(DType.Float32, TensorElementType.Float)]
    [InlineData(DType.Float16, TensorElementType.Float16)]
    [InlineData(DType.BFloat16, TensorElementType.BFloat16)]
    [InlineData(DType.Float64, TensorElementType.Double)]
    [InlineData(DType.Int8, TensorElementType.Int8)]
    [InlineData(DType.UInt8, TensorElementType.UInt8)]
    [InlineData(DType.Int16, TensorElementType.Int16)]
    [InlineData(DType.UInt16, TensorElementType.UInt16)]
    [InlineData(DType.Int32, TensorElementType.Int32)]
    [InlineData(DType.UInt32, TensorElementType.UInt32)]
    [InlineData(DType.Int64, TensorElementType.Int64)]
    [InlineData(DType.UInt64, TensorElementType.UInt64)]
    [InlineData(DType.Bool, TensorElementType.Bool)]
    public void MapDType_MapsEachSupportedDType(DType dtype, TensorElementType expected)
    {
        Assert.Equal(expected, OrtInferenceSessionBase.MapDType(dtype));
    }

    [Fact]
    public void MapDType_CoversEveryDTypeEnumMember()
    {
        // Guard: if a DType is added to FrameFlow.Graph without a matching
        // ORT mapping, this fails — every member must map (none throw).
        foreach (DType dtype in Enum.GetValues<DType>())
        {
            var mapped = OrtInferenceSessionBase.MapDType(dtype);
            Assert.True(Enum.IsDefined(mapped), $"DType {dtype} mapped to undefined element type.");
        }
    }

    [Fact]
    public void MapDType_UnsupportedValue_Throws()
    {
        // An out-of-range DType cast (no enum member) has no mapping.
        var bogus = (DType)9999;
        Assert.Throws<NotSupportedException>(() => OrtInferenceSessionBase.MapDType(bogus));
    }

    // ── ValidateNames: name-mismatch rejection ───────────────────────────

    [Fact]
    public void ValidateNames_AllSuppliedNamesDeclared_DoesNotThrow()
    {
        var expected = new[] { "images", "orig_target_sizes" };
        OrtInferenceSessionBase.ValidateNames(
            supplied: new[] { "images", "orig_target_sizes" },
            expected: expected,
            kind: "input"
        );
    }

    [Fact]
    public void ValidateNames_EmptySupplied_DoesNotThrow()
    {
        OrtInferenceSessionBase.ValidateNames(
            supplied: Array.Empty<string>(),
            expected: new[] { "input" },
            kind: "input"
        );
    }

    [Fact]
    public void ValidateNames_UnknownName_ThrowsWithKindParamAndModelNames()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            OrtInferenceSessionBase.ValidateNames(
                supplied: new[] { "input", "typo" },
                expected: new[] { "input", "boxes" },
                kind: "input"
            )
        );

        Assert.Equal("inputs", ex.ParamName); // "input" + "s"
        Assert.Contains("typo", ex.Message);
        Assert.Contains("input", ex.Message); // names the model inputs
        Assert.Contains("boxes", ex.Message);
    }

    [Fact]
    public void ValidateNames_OutputKind_UsesOutputsParamName()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            OrtInferenceSessionBase.ValidateNames(
                supplied: new[] { "wrong_output" },
                expected: new[] { "output" },
                kind: "output"
            )
        );

        Assert.Equal("outputs", ex.ParamName);
        Assert.Contains("wrong_output", ex.Message);
    }

    [Fact]
    public void ValidateNames_IsCaseSensitive_Ordinal()
    {
        // ORT names are matched by ordinal — a case difference is a mismatch.
        Assert.Throws<ArgumentException>(() =>
            OrtInferenceSessionBase.ValidateNames(
                supplied: new[] { "Images" },
                expected: new[] { "images" },
                kind: "input"
            )
        );
    }

    // ── ConvertDims: shape building incl. dynamic (-1) dims ───────────────

    [Fact]
    public void ConvertDims_StaticDims_ConvertedInOrder()
    {
        IReadOnlyList<long> shape = OrtInferenceSessionBase.ConvertDims(new[] { 1, 84, 8400 });
        Assert.Equal(new long[] { 1, 84, 8400 }, shape);
    }

    [Fact]
    public void ConvertDims_DynamicDim_MinusOnePreservedVerbatim()
    {
        // NodeMetadata.Dimensions uses -1 for a dynamic axis (e.g. dynamic
        // batch). The transform must pass -1 through unchanged.
        IReadOnlyList<long> shape = OrtInferenceSessionBase.ConvertDims(new[] { -1, 3, 224, 224 });
        Assert.Equal(new long[] { -1, 3, 224, 224 }, shape);
    }

    [Fact]
    public void ConvertDims_AllDynamic()
    {
        IReadOnlyList<long> shape = OrtInferenceSessionBase.ConvertDims(new[] { -1, -1 });
        Assert.Equal(new long[] { -1, -1 }, shape);
    }

    [Fact]
    public void ConvertDims_ResultIsReadOnly()
    {
        // The returned shape is surfaced on IInferenceSession.InputShapes /
        // OutputShapes and must not be externally mutable.
        IReadOnlyList<long> shape = OrtInferenceSessionBase.ConvertDims(new[] { 2, 2 });
        Assert.IsNotType<long[]>(shape);
        Assert.IsType<System.Collections.ObjectModel.ReadOnlyCollection<long>>(shape);
    }
}
