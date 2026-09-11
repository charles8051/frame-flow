namespace FrameFlow.Media.Tests;

public sealed class ResultTests
{
    // -----------------------------------------------------------------------
    // Result — the IsSuccess/Error invariant
    //
    // These assert at runtime what [MemberNotNullWhen(false, nameof(Error))]
    // asserts at compile time. The attribute is taken on trust by the
    // compiler, so the only thing that keeps it honest is a test that walks
    // every state the struct can be in — including default, which no factory
    // method produces but which a struct field or array element does.
    //
    // The file is also the compile-time half of the proof. Every
    // `result.Error.` below is written without `?.`, and xUnit annotates
    // Assert.False with [DoesNotReturnIf(true)], so the null-state analysis
    // reaches those lines. Removing the attribute from Result.cs turns four
    // of them into CS8602.
    // -----------------------------------------------------------------------

    [Fact]
    public void Ok_IsSuccess_WithNoError()
    {
        var result = Result.Ok();

        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Fail_IsNotSuccess_AndCarriesTheError()
    {
        var inner = new InvalidOperationException("boom");

        var result = Result.Fail(ErrorCategory.Decode, "corrupt frame", inner);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCategory.Decode, result.Error.Category);
        Assert.Equal("corrupt frame", result.Error.Message);
        Assert.Same(inner, result.Error.Inner);
    }

    [Fact]
    public void Fail_FromPlaybackError_KeepsTheSameInstance()
    {
        var error = new PlaybackError(ErrorCategory.Network, "timed out");

        var result = Result.Fail(error);

        Assert.False(result.IsSuccess);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public void Default_IsNotSuccess_AndStillReportsAnError()
    {
        // The state the annotation would otherwise lie about: default zeroes
        // every field, so IsSuccess is false and the stored error is null.
        Result result = default;

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCategory.InvalidOperation, result.Error.Category);
    }

    // -----------------------------------------------------------------------
    // Result<T> — the same invariant, plus Value
    // -----------------------------------------------------------------------

    [Fact]
    public void Generic_Ok_CarriesTheValue_AndNoError()
    {
        var result = Result<int>.Ok(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Generic_Fail_CarriesTheError_AndThrowsOnValue()
    {
        var result = Result<string>.Fail(ErrorCategory.Io, "file not found");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCategory.Io, result.Error.Category);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Generic_Default_IsNotSuccess_ReportsAnError_AndThrowsOnValue()
    {
        // Value must keep throwing here. A reference T would otherwise hand
        // back null from a property declared to return non-null T.
        Result<string> result = default;

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCategory.InvalidOperation, result.Error.Category);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }
}
