using FrameFlow.Decoding.Internal;
using FrameFlow.Media;
using FrameFlow.Native.Interop;
namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Locates test corpus files and detects FFmpeg availability for decoding integration tests.
/// </summary>
internal static class TestEnvironment
{
    private static readonly Lazy<string> CachedRepoRoot = new(FindRepoRoot);
    private static readonly Lazy<bool> CachedHasFfmpegLibraries = new(DetectFfmpegSharedLibraries);

    /// <summary>Path to tests/corpus/files/.</summary>
    internal static string CorpusDir =>
        Path.Combine(CachedRepoRoot.Value, "tests", "corpus", "files");

    /// <summary>
    /// <see langword="true"/> when FFmpeg shared libraries are loadable in this environment.
    /// Integration tests that call FFmpeg P/Invoke functions must check this before running.
    /// </summary>
    internal static bool HasFfmpegSharedLibraries => CachedHasFfmpegLibraries.Value;

    /// <summary>
    /// <see langword="true"/> when the corpus directory contains at least one media file.
    /// </summary>
    internal static bool HasCorpusFiles =>
        Directory.Exists(CorpusDir) && Directory.EnumerateFiles(CorpusDir).Any();

    /// <summary>Returns the full path to a named corpus file, or null when absent.</summary>
    internal static string? GetCorpusFile(string name)
    {
        var path = Path.Combine(CorpusDir, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Returns the directory that contains the FFmpeg shared libraries, or
    /// <see langword="null"/> when no library directory can be located.
    /// </summary>
    internal static string? FindFfmpegLibraryDirectory()
    {
        var repoRoot = CachedRepoRoot.Value;
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        var archLabel = arch == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

        string rid;
        if (OperatingSystem.IsWindows())
            rid = $"win-{archLabel}";
        else if (OperatingSystem.IsMacOS())
            rid = $"osx-{archLabel}";
        else
            rid = $"linux-{archLabel}";

        var nativeDir = Path.Combine(repoRoot, "runtimes", rid, "native");
        var libName =
            OperatingSystem.IsWindows() ? "avutil-61.dll"
            : OperatingSystem.IsMacOS() ? "libavutil.61.dylib"
            : "libavutil.so.61";

        if (File.Exists(Path.Combine(nativeDir, libName)))
            return nativeDir;

        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            if (File.Exists(Path.Combine(dir, libName)))
                return dir;
        }

        return null;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "FrameFlow.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        return Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")
        );
    }

    private static bool DetectFfmpegSharedLibraries()
    {
        var repoRoot = CachedRepoRoot.Value;
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        var archLabel = arch == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

        string rid;
        if (OperatingSystem.IsWindows())
            rid = $"win-{archLabel}";
        else if (OperatingSystem.IsMacOS())
            rid = $"osx-{archLabel}";
        else
            rid = $"linux-{archLabel}";

        var nativeDir = Path.Combine(repoRoot, "runtimes", rid, "native");
        var libName =
            OperatingSystem.IsWindows() ? "avutil-61.dll"
            : OperatingSystem.IsMacOS() ? "libavutil.61.dylib"
            : "libavutil.so.61";

        if (File.Exists(Path.Combine(nativeDir, libName)))
            return true;

        // Also check PATH
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            if (File.Exists(Path.Combine(dir, libName)))
                return true;
        }

        return false;
    }
}

/// <summary>
/// XUnit fact that is skipped when FFmpeg shared libraries are not available.
/// </summary>
internal sealed class RequiresFfmpegFactAttribute : FactAttribute
{
    public RequiresFfmpegFactAttribute()
    {
        if (!TestEnvironment.HasFfmpegSharedLibraries)
            Skip =
                "FFmpeg shared libraries not available. "
                + "Run scripts/fetch-ffmpeg.cs or install FFmpeg with shared libraries.";
    }
}

/// <summary>
/// XUnit fact that is skipped when FFmpeg shared libraries or corpus files are unavailable.
/// </summary>
internal sealed class RequiresFfmpegAndCorpusFactAttribute : FactAttribute
{
    public RequiresFfmpegAndCorpusFactAttribute()
    {
        if (!TestEnvironment.HasFfmpegSharedLibraries)
        {
            Skip = "FFmpeg shared libraries not available.";
            return;
        }

        if (!TestEnvironment.HasCorpusFiles)
            Skip = "Test corpus not generated. Run scripts/generate-test-corpus.cs first.";
    }
}

/// <summary>
/// As <see cref="RequiresFfmpegAndCorpusFactAttribute"/>, and skipped unless a decoder for
/// <c>codecId</c> advertises a hardware config for a backend that initialised here. Hardware
/// support is per codec, so the gate asks about the codec, not just the device.
/// </summary>
internal sealed class RequiresHardwareDecodeFactAttribute : FactAttribute
{
    /// <param name="codecId">The FFmpeg <c>AVCodecID</c>; <c>27</c> is H.264, <c>172</c> HEVC.</param>
    /// <param name="fixedPool">
    /// Also skip where every initialised backend's pool grows (VideoToolbox), which has no budget
    /// to test.
    /// </param>
    public RequiresHardwareDecodeFactAttribute(int codecId, bool fixedPool = false)
    {
        if (!TestEnvironment.HasFfmpegSharedLibraries)
        {
            Skip = "FFmpeg shared libraries not available.";
            return;
        }

        if (!TestEnvironment.HasCorpusFiles)
        {
            Skip = "Test corpus not generated. Run scripts/generate-test-corpus.cs first.";
            return;
        }

        var capabilities = FfmpegBootstrapFixture.ReadCapabilities();
        if (!capabilities.Available.Any(b => b.Initialized))
        {
            Skip = "No hardware decode backend initialised on this machine.";
            return;
        }

        if (!VideoDecoder.HasHardwareCandidate(codecId, capabilities))
        {
            Skip =
                $"A hardware backend initialised, but no decoder for codec {codecId} "
                + "advertises a hardware config for it on this machine.";
            return;
        }

        if (
            fixedPool
            && capabilities
                .Available.Where(b => b.Initialized)
                .All(b => DecodePoolGuard.SpareSurfaces(b.Kind) is null)
        )
        {
            Skip = "Every hardware backend here has a growable pool, which has no budget.";
        }
    }
}

/// <summary>
/// As <see cref="RequiresHardwareDecodeFactAttribute"/>, for one backend: skipped unless
/// <c>backend</c> decodes the first frame of <c>clip</c> on hardware here.
/// </summary>
/// <remarks>
/// The gate decodes rather than asking whether a config exists, because a device that
/// initialises can still decode in software. A Vulkan device without
/// <c>VK_KHR_video_decode_queue</c> opens, advertises a config and falls back (#74). The answer
/// is cached for each backend and clip.
/// </remarks>
internal sealed class RequiresHardwareDecodeBackendFactAttribute : FactAttribute
{
    private static readonly Dictionary<(HardwareDecodeBackendKind, string), string?> Probes = [];

    /// <param name="backend">The backend the test decodes on.</param>
    /// <param name="clip">The corpus file the test decodes.</param>
    public RequiresHardwareDecodeBackendFactAttribute(HardwareDecodeBackendKind backend, string clip)
    {
        if (!TestEnvironment.HasFfmpegSharedLibraries)
        {
            Skip = "FFmpeg shared libraries not available.";
            return;
        }

        if (!TestEnvironment.HasCorpusFiles)
        {
            Skip = "Test corpus not generated. Run scripts/generate-test-corpus.cs first.";
            return;
        }

        lock (Probes)
        {
            if (!Probes.TryGetValue((backend, clip), out var reason))
            {
                reason = WhyItCannotDecode(backend, clip);
                Probes[(backend, clip)] = reason;
            }
            Skip = reason;
        }
    }

    /// <summary>Why <paramref name="backend"/> does not decode <paramref name="clip"/>, or null.</summary>
    private static string? WhyItCannotDecode(HardwareDecodeBackendKind backend, string clip)
    {
        if (TestEnvironment.GetCorpusFile(clip) is not { } file)
            return $"Corpus file '{clip}' not present.";

        var only = new HardwareDecodeCapabilities(
            FfmpegBootstrapFixture
                .ReadCapabilities()
                .Available.Where(b => b.Kind == backend && b.Initialized)
                .ToList()
        );
        if (only.Available.Count == 0)
            return $"No {backend} device initialised on this machine.";

        return Task.Run(async () =>
            {
                await using var demux = (DemuxSession)
                    await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(file));
                int stream = demux.MediaInfo.VideoStreams[0].StreamIndex;
                VideoDecoder decoder;
                try
                {
                    decoder = VideoDecoder.Open(
                        demux.FormatContextPtr,
                        stream,
                        new HardwareDecodeOptions
                        {
                            Mode = HardwareDecodeMode.Required,
                            PreferredBackends = [backend],
                        },
                        only,
                        loggerFactory: null
                    );
                }
                catch (HardwareDecodeUnavailableException)
                {
                    return $"No decoder for {clip} could be bound to {backend} here.";
                }

                await using (decoder)
                {
                    await QueueAllAsync(demux, stream, decoder);
                    await using var frames = decoder.DecodeAsync().GetAsyncEnumerator();
                    if (!await frames.MoveNextAsync())
                        return $"{backend} decoded no frame from {clip}.";
                    frames.Current.Dispose();
                    return decoder.HardwareBackend == backend
                        ? null
                        : $"A {backend} device opened but decoded {clip} in software, as a "
                            + "device without a decode queue does (#74).";
                }
            })
            .GetAwaiter()
            .GetResult();
    }

    private static async Task QueueAllAsync(DemuxSession demux, int stream, VideoDecoder decoder)
    {
        nint read = FFAvCodec.av_packet_alloc();
        try
        {
            while (FFAvFormat.av_read_frame(demux.FormatContextPtr, read) >= 0)
            {
                if (new AvPacketAccessor(read).StreamIndex == stream)
                {
                    nint clone = FFAvCodec.av_packet_alloc();
                    FFAvCodec.av_packet_ref(clone, read);
                    await decoder.SendPacketAsync(clone);
                }

                FFAvCodec.av_packet_unref(read);
            }
        }
        finally
        {
            FFAvCodec.av_packet_free(ref read);
        }

        decoder.CompletePacketQueue();
    }
}

/// <summary>
/// As <see cref="RequiresFfmpegAndCorpusFactAttribute"/>, plus a named corpus file
/// that the default corpus does not contain.
/// </summary>
/// <remarks>
/// The point is that the run says so. The repo-wide pattern for a missing fixture is
/// <c>if (file is null) return;</c> inside the test body, which xunit reports as
/// <em>passed</em> — so a green suite reads as coverage whether or not the fixture
/// was there. For a fixture that is opt-in by design, that is the difference between
/// a test nobody runs and a test nobody knows nobody runs.
/// </remarks>
internal sealed class RequiresCorpusFileFactAttribute : FactAttribute
{
    public RequiresCorpusFileFactAttribute(string fileName, string howToGenerate)
    {
        if (!TestEnvironment.HasFfmpegSharedLibraries)
        {
            Skip = "FFmpeg shared libraries not available.";
            return;
        }

        if (TestEnvironment.GetCorpusFile(fileName) is null)
            Skip = $"Corpus file '{fileName}' not present. {howToGenerate}";
    }
}
