using FrameFlow.Media.Core;

namespace FrameFlow.Media.Tests;

/// <summary>
/// The one mapping from a media failure to an <see cref="ErrorCategory"/> (#755, #757). Every
/// stage is crossed with every code class the mapping distinguishes, and the expected category is
/// written out here rather than derived, so a changed rule fails a named row.
/// </summary>
public sealed class MediaErrorClassificationTests
{
    // The values FFmpeg's headers define. Written as literals so a wrong tag computation in the
    // classifier cannot agree with itself.
    private const int Enoent = -2;
    private const int Eio = -5;
    private const int Enomem = -12;
    private const int Eacces = -13;
    private const int Eperm = -1;
    private const int InvalidData = -1094995529; // AVERROR_INVALIDDATA, FFERRTAG('I','N','D','A')
    private const int Bug = -558323010; // AVERROR_BUG, FFERRTAG('B','U','G','!')
    private const int Bug2 = -541545794; // AVERROR_BUG2, FFERRTAG('B','U','G',' ')
    private const int StreamNotFound = -1381258232; // AVERROR_STREAM_NOT_FOUND, FFERRTAG(0xF8,'S','T','R')
    private const int DemuxerNotFound = -1296385272; // AVERROR_DEMUXER_NOT_FOUND, FFERRTAG(0xF8,'D','E','M')
    private const int EndOfFile = -541478725; // AVERROR_EOF, FFERRTAG('E','O','F',' ')

    private const ErrorCategory Io = ErrorCategory.Io;
    private const ErrorCategory Source = ErrorCategory.Source;
    private const ErrorCategory Decode = ErrorCategory.Decode;
    private const ErrorCategory System = ErrorCategory.System;

    [Fact]
    public void TheNamedCodes_MatchFfmpegsValues()
    {
        Assert.Equal(InvalidData, MediaErrorClassification.InvalidData);
        Assert.Equal(Bug, MediaErrorClassification.Bug);
        Assert.Equal(Bug2, MediaErrorClassification.Bug2);
        Assert.Equal(StreamNotFound, MediaErrorClassification.StreamNotFound);
        Assert.Equal(Enoent, MediaErrorClassification.NoSuchFile);
        Assert.Equal(Eio, MediaErrorClassification.IoError);
        Assert.Equal(Enomem, MediaErrorClassification.OutOfMemory);
        Assert.Equal(Eacces, MediaErrorClassification.AccessDenied);
    }

    public static TheoryData<int, MediaFailureStage, ErrorCategory> Table()
    {
        var rows = new TheoryData<int, MediaFailureStage, ErrorCategory>();

        //                                Open    Probe   Streams Seek    Read    Send    Receive
        Row(rows, Enoent,          [Io,     Io,     Io,     Io,     Io,     Io,     Io]);
        Row(rows, Eacces,          [Io,     Io,     Io,     Io,     Io,     Io,     Io]);
        Row(rows, Eio,             [Io,     Io,     Io,     Io,     Io,     Io,     Io]);
        Row(rows, Enomem,          [System, System, System, System, System, System, System]);
        Row(rows, Bug,             [System, System, System, System, System, System, System]);
        Row(rows, Bug2,            [System, System, System, System, System, System, System]);
        Row(rows, InvalidData,     [Source, Source, Source, Decode, Decode, Decode, Decode]);
        Row(rows, StreamNotFound,  [Source, Source, Source, Source, Decode, Decode, Decode]);
        Row(rows, DemuxerNotFound, [Source, Source, Source, Source, Decode, Decode, Decode]);
        Row(rows, EndOfFile,       [Source, Source, Source, Source, Decode, Decode, Decode]);
        Row(rows, Eperm,           [Source, Source, Source, Source, Decode, Decode, Decode]);
        Row(rows, 0,               [System, System, System, System, System, System, System]);
        Row(rows, 1,               [System, System, System, System, System, System, System]);

        return rows;
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void Categorize_MapsEachCodeAtEachStage(int code, MediaFailureStage stage, ErrorCategory expected) =>
        Assert.Equal(expected, MediaErrorClassification.Categorize(code, stage));

    [Fact]
    public void TheTable_CoversEveryStage()
    {
        var covered = Table().Select(row => (MediaFailureStage)row[1]).Distinct().Order();
        Assert.Equal(Enum.GetValues<MediaFailureStage>().Order(), covered);
    }

    [Fact]
    public void Categorize_RefusesAnUndefinedStage() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaErrorClassification.Categorize(InvalidData, (MediaFailureStage)99)
        );

    [Fact]
    public void AMediaException_TakesItsCategoryFromItsCodeAndStage()
    {
        var atOpen = new MediaException("open", InvalidData, MediaFailureStage.Open);
        var midStream = new MediaException("read", InvalidData, MediaFailureStage.DemuxRead);

        Assert.Equal(Source, atOpen.Category);
        Assert.Equal(Decode, midStream.Category);
        Assert.Equal(InvalidData, midStream.ErrorCode);
        Assert.Equal(MediaFailureStage.DemuxRead, midStream.Stage);
        Assert.IsAssignableFrom<InvalidOperationException>(midStream);
    }

    [Fact]
    public void CategorizeException_ReadsAMediaExceptionsCategory_SoThrownAndReturnedAgree()
    {
        var missing = new MediaException("missing", Enoent, MediaFailureStage.Open);

        Assert.Equal(missing.Category, MediaErrorClassification.Categorize(missing, System));
        Assert.Equal(Io, MediaErrorClassification.Categorize(missing, System));
    }

    [Fact]
    public void CategorizeException_FindsAMediaExceptionInsideAWrapper()
    {
        var corrupt = new MediaException("corrupt", InvalidData, MediaFailureStage.SendPacket);
        var wrapped = new InvalidOperationException("node faulted", new AggregateException(corrupt));

        Assert.Equal(Decode, MediaErrorClassification.Categorize(wrapped, System));
        Assert.Same(corrupt, MediaErrorClassification.FindMediaFailure(wrapped));
    }

    [Fact]
    public void CategorizeException_TakesTheOutermostClassifiedException()
    {
        var corrupt = new MediaException("corrupt", InvalidData, MediaFailureStage.ReceiveFrame);
        var io = new IOException("read failed", corrupt);

        Assert.Equal(Io, MediaErrorClassification.Categorize(io, System));
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void CategorizeException_MapsIoFailuresToIo(Type type)
    {
        var exception = (Exception)Activator.CreateInstance(type)!;
        Assert.Equal(Io, MediaErrorClassification.Categorize(exception, System));
    }

    [Fact]
    public void CategorizeException_MapsOutOfMemoryToSystem() =>
        Assert.Equal(System, MediaErrorClassification.Categorize(new OutOfMemoryException(), Source));

    [Theory]
    [InlineData(ErrorCategory.System)]
    [InlineData(ErrorCategory.Source)]
    public void CategorizeException_FallsBack_ForAFaultItDoesNotRecognise(ErrorCategory fallback)
    {
        Assert.Equal(fallback, MediaErrorClassification.Categorize(new InvalidOperationException(), fallback));
        Assert.Equal(fallback, MediaErrorClassification.Categorize(null, fallback));
    }

    [Fact]
    public void APlaybackError_ExposesTheMediaFailureBehindIt()
    {
        var corrupt = new MediaException("corrupt", InvalidData, MediaFailureStage.Open);
        var error = new PlaybackError(corrupt.Category, "item failed", new InvalidOperationException("wrap", corrupt));

        Assert.Same(corrupt, error.MediaFailure);
        Assert.Null(new PlaybackError(System, "player fault", new InvalidOperationException()).MediaFailure);
        Assert.Null(new PlaybackError(System, "no inner").MediaFailure);
    }

    private static void Row(
        TheoryData<int, MediaFailureStage, ErrorCategory> rows,
        int code,
        ErrorCategory[] byStage
    )
    {
        MediaFailureStage[] stages =
        [
            MediaFailureStage.Open,
            MediaFailureStage.Probe,
            MediaFailureStage.StreamResolution,
            MediaFailureStage.Seek,
            MediaFailureStage.DemuxRead,
            MediaFailureStage.SendPacket,
            MediaFailureStage.ReceiveFrame,
        ];
        for (int i = 0; i < stages.Length; i++)
            rows.Add(code, stages[i], byStage[i]);
    }
}
