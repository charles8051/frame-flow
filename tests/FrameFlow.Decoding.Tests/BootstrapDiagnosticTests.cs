using System.Runtime.CompilerServices;
using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Regression guards that verify FFmpeg bootstrap state and low-level interop correctness.
/// These tests are in the same collection as DemuxSessionIntegrationTests to share the
/// FfmpegBootstrapFixture and run in the same process, providing an early signal when native
/// interop breaks.
/// </summary>
/// <remarks>
/// These tests were introduced to diagnose and guard against issue A-1:
/// <list type="bullet">
///   <item>Root cause 1: <c>avcodec_get_name</c> was declared with
///     <c>[return: MarshalAs(UnmanagedType.LPUTF8Str)]</c>, which causes the runtime to
///     call <c>CoTaskMemFree</c> on a statically-allocated FFmpeg string, corrupting the
///     heap and triggering a native process crash.</item>
///   <item>Root cause 2: <c>FFAvUtil.AvErrorEof</c> was defined as <c>0xBFB5B0BB</c>;
///     the correct value is <c>0xDFB9B0BB</c> (FFERRTAG('E','O','F',' ')), which caused
///     <c>av_read_frame</c> EOF to be treated as an error.</item>
/// </list>
/// </remarks>
public sealed class BootstrapDiagnosticTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public BootstrapDiagnosticTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void FfmpegBootstrap_ShouldBeSuccessful()
    {
        Assert.True(
            _fixture.IsBootstrapped,
            "FfmpegBootstrapFixture.IsBootstrapped is false — FFmpeg was not loaded. "
                + "Integration tests will fail or crash if this is false. "
                + $"FFmpeg library dir: {TestEnvironment.FindFfmpegLibraryDirectory() ?? "(not found)"}"
        );
    }

    [Fact]
    public void FfmpegLibraryDirectory_ShouldBeDetectable()
    {
        var dir = TestEnvironment.FindFfmpegLibraryDirectory();
        Assert.NotNull(dir);
        Assert.True(
            System.IO.Directory.Exists(dir),
            $"FFmpeg library directory does not exist: {dir}"
        );
    }

    [Fact]
    public void AvformatOpenInput_OnRealFile_DoesNotCrash()
    {
        // Regression: verifies avformat_open_input succeeds on a real corpus file
        // and that avformat_close_input nulls the context pointer.
        if (!_fixture.IsBootstrapped)
            return;

        var file = TestEnvironment.GetCorpusFile("test-subsecond.mp4");
        if (file is null)
            return;

        nint ctx = nint.Zero;
        int result = FFAvFormat.avformat_open_input(ref ctx, file, nint.Zero, nint.Zero);
        Assert.True(result >= 0, $"avformat_open_input failed with code {result}");
        Assert.NotEqual(nint.Zero, ctx);

        FFAvFormat.avformat_close_input(ref ctx);
        Assert.Equal(nint.Zero, ctx);
    }

    [Fact]
    public void AvformatFindStreamInfo_OnRealFile_DoesNotCrash()
    {
        // Regression: verifies avformat_find_stream_info succeeds and reports streams.
        if (!_fixture.IsBootstrapped)
            return;

        var file = TestEnvironment.GetCorpusFile("test-subsecond.mp4");
        if (file is null)
            return;

        nint ctx = nint.Zero;
        int openResult = FFAvFormat.avformat_open_input(ref ctx, file, nint.Zero, nint.Zero);
        Assert.True(openResult >= 0, $"avformat_open_input failed with code {openResult}");
        Assert.NotEqual(nint.Zero, ctx);

        try
        {
            int findResult = FFAvFormat.avformat_find_stream_info(ctx, nint.Zero);
            Assert.True(
                findResult >= 0,
                $"avformat_find_stream_info failed with code {findResult}"
            );
        }
        finally
        {
            FFAvFormat.avformat_close_input(ref ctx);
        }
    }

    [Fact]
    public unsafe void BuildMediaInfo_NbStreams_ReadsCorrectly()
    {
        // Regression: verifies AVFormatContext.nb_streams reads the correct value via
        // typed struct access (FFmpeg.AutoGen.Abstractions.AVFormatContext).
        if (!_fixture.IsBootstrapped)
            return;

        var file = TestEnvironment.GetCorpusFile("test-subsecond.mp4");
        if (file is null)
            return;

        nint ctx = nint.Zero;
        FFAvFormat.avformat_open_input(ref ctx, file, nint.Zero, nint.Zero);
        FFAvFormat.avformat_find_stream_info(ctx, nint.Zero);
        try
        {
            ref AVFormatContext fmtCtx = ref Unsafe.AsRef<AVFormatContext>((void*)ctx);
            uint nbStreams = fmtCtx.nb_streams;
            Assert.True(nbStreams > 0, $"nb_streams = {nbStreams}, expected > 0");
        }
        finally
        {
            FFAvFormat.avformat_close_input(ref ctx);
        }
    }

    [Fact]
    public unsafe void BuildMediaInfo_StreamsPtr_ReadsCorrectly()
    {
        // Regression: verifies AVFormatContext.streams pointer reads correctly via
        // typed struct access and that stream[0] is a non-null pointer.
        if (!_fixture.IsBootstrapped)
            return;

        var file = TestEnvironment.GetCorpusFile("test-subsecond.mp4");
        if (file is null)
            return;

        nint ctx = nint.Zero;
        FFAvFormat.avformat_open_input(ref ctx, file, nint.Zero, nint.Zero);
        FFAvFormat.avformat_find_stream_info(ctx, nint.Zero);
        try
        {
            ref AVFormatContext fmtCtx = ref Unsafe.AsRef<AVFormatContext>((void*)ctx);
            AVStream** streams = fmtCtx.streams;
            Assert.True((nint)streams != nint.Zero, "streams pointer is null");

            AVStream* stream0 = streams[0];
            Assert.True((nint)stream0 != nint.Zero, "stream[0] pointer is null");
        }
        finally
        {
            FFAvFormat.avformat_close_input(ref ctx);
        }
    }

    [Fact]
    public unsafe void BuildMediaInfo_StreamFields_ReadsCorrectly()
    {
        // Regression: verifies AVStream.index, AVStream.codecpar, AVCodecParameters.codec_type
        // all read correctly via typed struct access.
        if (!_fixture.IsBootstrapped)
            return;

        var file = TestEnvironment.GetCorpusFile("test-subsecond.mp4");
        if (file is null)
            return;

        nint ctx = nint.Zero;
        FFAvFormat.avformat_open_input(ref ctx, file, nint.Zero, nint.Zero);
        FFAvFormat.avformat_find_stream_info(ctx, nint.Zero);
        try
        {
            ref AVFormatContext fmtCtx = ref Unsafe.AsRef<AVFormatContext>((void*)ctx);
            uint nbStreams = fmtCtx.nb_streams;
            AVStream** streams = fmtCtx.streams;

            for (uint i = 0; i < nbStreams; i++)
            {
                AVStream* stream = streams[i];
                int streamIdx = stream->index;
                Assert.True(streamIdx >= 0, $"stream[{i}].index = {streamIdx}, expected >= 0");

                AVCodecParameters* codecPar = stream->codecpar;
                Assert.True((nint)codecPar != nint.Zero, $"stream[{i}].codecpar is null");

                int mediaType = (int)codecPar->codec_type;
                Assert.True(mediaType >= 0, $"stream[{i}].codec_type = {mediaType}");
            }
        }
        finally
        {
            FFAvFormat.avformat_close_input(ref ctx);
        }
    }

    [Fact]
    public unsafe void BuildMediaInfo_CodecName_DoesNotCrash()
    {
        // Regression: guards against the avcodec_get_name crash (issue A-1).
        // The native function returns a static string — it must NOT be freed by the
        // marshaler. We fixed this by returning nint and calling PtrToStringUTF8 manually.
        if (!_fixture.IsBootstrapped)
            return;

        var file = TestEnvironment.GetCorpusFile("test-subsecond.mp4");
        if (file is null)
            return;

        nint ctx = nint.Zero;
        FFAvFormat.avformat_open_input(ref ctx, file, nint.Zero, nint.Zero);
        FFAvFormat.avformat_find_stream_info(ctx, nint.Zero);
        try
        {
            ref AVFormatContext fmtCtx = ref Unsafe.AsRef<AVFormatContext>((void*)ctx);
            uint nbStreams = fmtCtx.nb_streams;
            AVStream** streams = fmtCtx.streams;

            for (uint i = 0; i < nbStreams; i++)
            {
                AVStream* stream = streams[i];
                AVCodecParameters* codecPar = stream->codecpar;
                if (codecPar == null)
                    continue;

                int codecId = (int)codecPar->codec_id;
                string name = FFAvCodec.avcodec_get_name(codecId);
                Assert.False(
                    string.IsNullOrEmpty(name),
                    $"avcodec_get_name returned null/empty for codec id {codecId}"
                );
            }
        }
        finally
        {
            FFAvFormat.avformat_close_input(ref ctx);
        }
    }

    [Fact]
    public void BuildMediaInfo_OnRealFile_DoesNotCrash()
    {
        // Regression: verifies the full BuildMediaInfo path after find_stream_info
        // does not crash (catches struct field access or string marshaling bugs early).
        if (!_fixture.IsBootstrapped)
            return;

        var file = TestEnvironment.GetCorpusFile("test-subsecond.mp4");
        if (file is null)
            return;

        nint ctx = nint.Zero;
        int openResult = FFAvFormat.avformat_open_input(ref ctx, file, nint.Zero, nint.Zero);
        Assert.True(openResult >= 0, $"avformat_open_input failed with code {openResult}");

        try
        {
            int findResult = FFAvFormat.avformat_find_stream_info(ctx, nint.Zero);
            Assert.True(
                findResult >= 0,
                $"avformat_find_stream_info failed with code {findResult}"
            );

            var info = DemuxSession.BuildMediaInfo(ctx);
            Assert.NotNull(info);
        }
        finally
        {
            FFAvFormat.avformat_close_input(ref ctx);
        }
    }

    [Fact]
    public void AvPacketAlloc_AllocatesAndFrees()
    {
        // Regression: verifies av_packet_alloc/free works and sets pointer to null on free.
        if (!_fixture.IsBootstrapped)
            return;

        nint pkt = FFAvCodec.av_packet_alloc();
        Assert.NotEqual(nint.Zero, pkt);
        FFAvCodec.av_packet_free(ref pkt);
        Assert.Equal(nint.Zero, pkt);
    }

    [Fact]
    public async Task DemuxSessionFactory_CanOpenRealFile_WithoutCrash()
    {
        // Regression: verifies DemuxSessionFactory.OpenAsync completes without crashing
        // or throwing on a valid corpus file.
        if (!_fixture.IsBootstrapped)
        {
            return;
        }

        var file = TestEnvironment.GetCorpusFile("test-subsecond.mp4");
        if (file is null)
        {
            return;
        }

        var factory = new DemuxSessionFactory();
        Exception? caught = null;
        IDemuxSession? session = null;

        try
        {
            session = await factory.OpenAsync(MediaSource.FromFile(file));
        }
        catch (Exception ex)
        {
            caught = ex;
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync();
        }

        Assert.Null(caught);
        Assert.NotNull(session);
    }
}
