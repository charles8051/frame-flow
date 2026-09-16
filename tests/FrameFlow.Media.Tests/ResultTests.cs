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

    // -----------------------------------------------------------------------
    // Result<T>.TryGetValue — the non-throwing accessor
    //
    // The difference from Value is the nullable flow, not the absence of the
    // throw: the false branch may pass `error` on without a null check, and
    // the true branch may read `value` without one. Forward() below is the
    // compile-time half of that claim.
    // -----------------------------------------------------------------------

    // Result.Fail(PlaybackError) takes a non-null argument, and `error` is
    // known non-null on this branch only because of [NotNullWhen(false)] on
    // TryGetValue's error parameter. Drop that attribute and this line becomes
    // CS8604.
    private static Result Forward<T>(Result<T> result) =>
        result.TryGetValue(out _, out var error) ? Result.Ok() : Result.Fail(error);

    [Fact]
    public void Generic_TryGetValue_OnOk_YieldsTheValueAndNoError()
    {
        var result = Result<int>.Ok(42);

        Assert.True(result.TryGetValue(out var value, out var error));
        Assert.Equal(42, value);
        Assert.Null(error);
    }

    [Fact]
    public void Generic_TryGetValue_OnFail_YieldsTheErrorAndTheDefaultValue()
    {
        var result = Result<string>.Fail(ErrorCategory.Io, "file not found");

        Assert.False(result.TryGetValue(out var value, out var error));
        Assert.Null(value);
        Assert.NotNull(error);
        Assert.Equal(ErrorCategory.Io, error.Category);
        Assert.Equal("file not found", error.Message);
    }

    [Fact]
    public void Generic_TryGetValue_OnDefault_YieldsTheDefaultError()
    {
        // Same state the Value property has to guard: no factory produced it,
        // so there is no stored error to hand back.
        Result<string> result = default;

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.NotNull(error);
        Assert.Equal(ErrorCategory.InvalidOperation, error.Category);
    }

    [Fact]
    public void Generic_TryGetValue_OnOkCarryingNull_StillSucceeds()
    {
        // Why the value parameter is [MaybeNullWhen(false)] and not
        // [NotNullWhen(true)]: T is unconstrained, so null is a legitimate
        // success value and a non-null promise would not hold.
        var result = Result<string?>.Ok(null);

        Assert.True(result.TryGetValue(out var value, out var error));
        Assert.Null(value);
        Assert.Null(error);
    }

    [Fact]
    public void Forwarding_A_Failure_KeepsTheError()
    {
        var error = new PlaybackError(ErrorCategory.Network, "timed out");

        var forwarded = Forward(Result<int>.Fail(error));

        Assert.False(forwarded.IsSuccess);
        Assert.Same(error, forwarded.Error);
    }

    [Fact]
    public void Forwarding_A_Success_Succeeds()
    {
        Assert.True(Forward(Result<int>.Ok(1)).IsSuccess);
    }

    // -----------------------------------------------------------------------
    // Deconstruct — ergonomics only
    //
    // It carries no flow between its outputs, so these assert the values and
    // nothing about nullability.
    // -----------------------------------------------------------------------

    [Fact]
    public void Deconstruct_Ok_YieldsSuccessAndNoError()
    {
        var (isSuccess, error) = Result.Ok();

        Assert.True(isSuccess);
        Assert.Null(error);
    }

    [Fact]
    public void Deconstruct_Fail_YieldsFailureAndTheError()
    {
        var (isSuccess, error) = Result.Fail(ErrorCategory.Source, "no such stream");

        Assert.False(isSuccess);
        Assert.NotNull(error);
        Assert.Equal(ErrorCategory.Source, error.Category);
        Assert.Equal("no such stream", error.Message);
    }

    [Fact]
    public void Generic_Deconstruct_Ok_YieldsSuccessTheValueAndNoError()
    {
        var (isSuccess, value, error) = Result<int>.Ok(7);

        Assert.True(isSuccess);
        Assert.Equal(7, value);
        Assert.Null(error);
    }

    [Fact]
    public void Generic_Deconstruct_Fail_YieldsFailureTheDefaultValueAndTheError()
    {
        var (isSuccess, value, error) = Result<string>.Fail(ErrorCategory.Decode, "corrupt");

        Assert.False(isSuccess);
        Assert.Null(value);
        Assert.NotNull(error);
        Assert.Equal(ErrorCategory.Decode, error.Category);
    }

    [Fact]
    public void Deconstruct_Default_YieldsFailureAndTheDefaultError()
    {
        var (isSuccess, error) = default(Result);

        Assert.False(isSuccess);
        Assert.NotNull(error);
        Assert.Equal(ErrorCategory.InvalidOperation, error.Category);
    }
}
