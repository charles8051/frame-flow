using System.Runtime.InteropServices;
using FrameFlow.Inference.Cuda;
using Xunit;

namespace FrameFlow.Inference.Cuda.Tests;

/// <summary>
/// Unit tests for the pure Linux side of the CUDA bootstrap
/// (<see cref="LinuxCudaResolution"/>). Every case drives a fake loader over a
/// synthetic table of loadable names, so the verdict a Linux consumer gets is
/// reproduced on every OS with no CUDA install, no GPU and no real
/// <c>dlopen</c>.
/// </summary>
public sealed class LinuxCudaResolutionTests
{
    private const string AppLocal = "/srv/app/runtimes/linux-x64/native";

    private static readonly string[] RequiredSonames =
    [
        "libcuda.so.1",
        "libcudart.so.12",
        "libcublasLt.so.12",
        "libcublas.so.12",
        "libcufft.so.11",
        "libcurand.so.10",
        "libcudnn.so.9",
    ];

    /// <summary>
    /// A fake loader: a name loads when it is in <paramref name="loadable"/>.
    /// Every name asked for is recorded, in order, in <see cref="Calls"/>.
    /// </summary>
    private sealed class FakeLoader(IEnumerable<string> loadable)
    {
        private readonly HashSet<string> _loadable = new(loadable, StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        public NativeLoadAttempt Load(string nameOrPath)
        {
            Calls.Add(nameOrPath);
            return _loadable.Contains(nameOrPath)
                ? NativeLoadAttempt.Success
                : NativeLoadAttempt.Failed($"{nameOrPath}: cannot open shared object file");
        }
    }

    private static HashSet<string> Files(params string[] names) => new(names, StringComparer.Ordinal);

    private static string AppLocalPath(string soname) => AppLocal + "/" + soname;

    // ── Platform classification and the app-local RID ────────────────────

    [Theory]
    [InlineData(true, false, Architecture.X64, "Windows")]
    [InlineData(true, false, Architecture.Arm64, "Windows")]
    [InlineData(false, true, Architecture.X64, "LinuxX64")]
    [InlineData(false, true, Architecture.Arm64, "Unsupported")]
    [InlineData(false, false, Architecture.X64, "Unsupported")]
    [InlineData(false, false, Architecture.Arm64, "Unsupported")]
    public void ClassifyPlatform_LinuxX64IsProbed_NotReportedUnsupported(
        bool isWindows,
        bool isLinux,
        Architecture arch,
        string expected
    ) => Assert.Equal(expected, LinuxCudaResolution.ClassifyPlatform(isWindows, isLinux, arch).ToString());

    [Theory]
    [InlineData(true, false, Architecture.X64, "win-x64")]
    [InlineData(true, false, Architecture.Arm64, "win-arm64")]
    [InlineData(false, true, Architecture.X64, "linux-x64")]
    [InlineData(false, true, Architecture.Arm64, "linux-arm64")]
    [InlineData(false, false, Architecture.X64, null)]
    [InlineData(false, true, Architecture.X86, null)]
    public void AppLocalRid_FollowsTheRunningPlatform(
        bool isWindows,
        bool isLinux,
        Architecture arch,
        string? expected
    ) => Assert.Equal(expected, LinuxCudaResolution.AppLocalRid(isWindows, isLinux, arch));

    // ── Loader search path only ──────────────────────────────────────────

    [Fact]
    public void EverythingOnTheLoaderPath_NothingMissing_OnlyRequiredLibrariesAskedFor()
    {
        var loader = new FakeLoader(RequiredSonames);

        var verdict = LinuxCudaResolution.Resolve(null, Files(), loader.Load);

        Assert.Equal(MissingNativeComponent.None, verdict.Missing);
        Assert.Empty(verdict.Failed);
        Assert.Equal(RequiredSonames, verdict.Loaded.Select(l => l.Library.Soname));
        Assert.All(verdict.Loaded, l => Assert.Equal(CudaLibrarySource.LoaderSearchPath, l.Source));
        // Optional cuDNN sub-libraries are libcudnn.so.9's own business on
        // the loader path; the resolver does not ask for them by soname.
        Assert.Equal(RequiredSonames, loader.Calls);
    }

    [Fact]
    public void NothingLoadable_DriverToolkitAndCudnnAllMissing()
    {
        var verdict = LinuxCudaResolution.Resolve(null, Files(), new FakeLoader([]).Load);

        Assert.Equal(
            MissingNativeComponent.GpuDriver
                | MissingNativeComponent.CudaToolkit
                | MissingNativeComponent.CuDnn,
            verdict.Missing
        );
        Assert.Empty(verdict.Loaded);
        Assert.Equal(RequiredSonames, verdict.Failed.Select(f => f.Library.Soname));
    }

    [Fact]
    public void MissingDriver_IsReportedWithTheLoadersMessage()
    {
        var loader = new FakeLoader(RequiredSonames.Where(s => s != "libcuda.so.1"));

        var verdict = LinuxCudaResolution.Resolve(null, Files(), loader.Load);

        Assert.Equal(MissingNativeComponent.GpuDriver, verdict.Missing);
        Assert.Equal(
            "libcuda.so.1: cannot open shared object file",
            verdict.FirstErrorFor(MissingNativeComponent.GpuDriver)
        );
        Assert.Null(verdict.FirstErrorFor(MissingNativeComponent.CudaToolkit));
    }

    [Theory]
    [InlineData("libcudart.so.12")]
    [InlineData("libcublasLt.so.12")]
    [InlineData("libcublas.so.12")]
    [InlineData("libcufft.so.11")]
    [InlineData("libcurand.so.10")]
    public void AnyOneCudaRuntimeLibraryMissing_MarksTheToolkitMissing(string absent)
    {
        var loader = new FakeLoader(RequiredSonames.Where(s => s != absent));

        var verdict = LinuxCudaResolution.Resolve(null, Files(), loader.Load);

        Assert.Equal(MissingNativeComponent.CudaToolkit, verdict.Missing);
        Assert.Equal(absent, Assert.Single(verdict.Failed).Library.Soname);
    }

    [Fact]
    public void MissingCudnn_MarksCudnnMissing()
    {
        var loader = new FakeLoader(RequiredSonames.Where(s => s != "libcudnn.so.9"));

        var verdict = LinuxCudaResolution.Resolve(null, Files(), loader.Load);

        Assert.Equal(MissingNativeComponent.CuDnn, verdict.Missing);
    }

    // ── App-local runtimes/linux-x64/native ──────────────────────────────

    [Fact]
    public void AppLocalCopy_WinsOverTheLoaderPath_AndIsLoadedByAbsolutePath()
    {
        var loader = new FakeLoader([.. RequiredSonames, AppLocalPath("libcudart.so.12")]);

        var verdict = LinuxCudaResolution.Resolve(
            AppLocal,
            Files("libcudart.so.12"),
            loader.Load
        );

        Assert.Equal(MissingNativeComponent.None, verdict.Missing);
        var cudart = verdict.Find("libcudart.so.12")!.Value;
        Assert.Equal(CudaLibrarySource.AppLocal, cudart.Source);
        Assert.Equal(AppLocalPath("libcudart.so.12"), cudart.LoadedFrom);
        // Once the app-local copy is in, the soname is not asked for.
        Assert.DoesNotContain("libcudart.so.12", loader.Calls);
    }

    [Fact]
    public void AppLocalDriver_IsNeverLoaded_TheDriverComesFromTheSystem()
    {
        var loader = new FakeLoader([.. RequiredSonames, AppLocalPath("libcuda.so.1")]);

        var verdict = LinuxCudaResolution.Resolve(AppLocal, Files("libcuda.so.1"), loader.Load);

        Assert.DoesNotContain(AppLocalPath("libcuda.so.1"), loader.Calls);
        Assert.Equal(CudaLibrarySource.LoaderSearchPath, verdict.Find("libcuda.so.1")!.Value.Source);
    }

    [Fact]
    public void AppLocalFilesAreIgnored_WhenThereIsNoAppLocalDirectory()
    {
        var loader = new FakeLoader(RequiredSonames);

        LinuxCudaResolution.Resolve(null, Files("libcudart.so.12"), loader.Load);

        Assert.DoesNotContain(loader.Calls, c => c.Contains('/', StringComparison.Ordinal));
    }

    [Fact]
    public void AppLocalCopyThatFailsToLoad_FallsBackToTheLoaderPath()
    {
        // The app-local file is listed but the loader refuses it (say, a
        // truncated download): the system copy is used instead.
        var loader = new FakeLoader(RequiredSonames);

        var verdict = LinuxCudaResolution.Resolve(AppLocal, Files("libcudnn.so.9"), loader.Load);

        Assert.Equal(MissingNativeComponent.None, verdict.Missing);
        Assert.Equal(CudaLibrarySource.LoaderSearchPath, verdict.Find("libcudnn.so.9")!.Value.Source);
        Assert.Empty(verdict.Failed);
        Assert.Equal(
            [AppLocalPath("libcudnn.so.9"), .. RequiredSonames],
            loader.Calls
        );
    }

    [Fact]
    public void AppLocalOptionalLibrary_IsPreloaded_AndItsFailureIsReportedButNotMissing()
    {
        var loader = new FakeLoader([.. RequiredSonames, AppLocalPath("libcudnn_ops.so.9")]);

        var verdict = LinuxCudaResolution.Resolve(
            AppLocal,
            Files("libcudnn_ops.so.9", "libcudnn_cnn.so.9"),
            loader.Load
        );

        Assert.Equal(MissingNativeComponent.None, verdict.Missing);
        Assert.Equal(CudaLibrarySource.AppLocal, verdict.Find("libcudnn_ops.so.9")!.Value.Source);
        var failure = Assert.Single(verdict.Failed);
        Assert.Equal("libcudnn_cnn.so.9", failure.Library.Soname);
        Assert.False(failure.Library.Required);
        // Optional libraries are never asked for by soname.
        Assert.DoesNotContain("libcudnn_cnn.so.9", loader.Calls);
    }

    [Fact]
    public void AppLocalCopyWhoseDependencyComesLater_LoadsOnARetryPass()
    {
        // "first" needs "second" in the process before it loads. The table
        // lists "first" before "second", so the first pass fails it; the
        // second pass, after "second" loaded, succeeds.
        var first = new CudaNativeLibrary("libfirst.so.1", MissingNativeComponent.CuDnn, Required: true);
        var second = new CudaNativeLibrary("libsecond.so.1", MissingNativeComponent.CuDnn, Required: true);
        var inProcess = new HashSet<string>(StringComparer.Ordinal);
        NativeLoadAttempt Load(string path)
        {
            if (path == AppLocalPath(first.Soname) && !inProcess.Contains(second.Soname))
                return NativeLoadAttempt.Failed("libsecond.so.1: cannot open shared object file");
            if (!path.StartsWith(AppLocal, StringComparison.Ordinal))
                return NativeLoadAttempt.Failed("not on the loader path");
            inProcess.Add(path[(AppLocal.Length + 1)..]);
            return NativeLoadAttempt.Success;
        }

        var verdict = LinuxCudaResolution.Resolve(
            AppLocal,
            Files(first.Soname, second.Soname),
            Load,
            libraries: [first, second]
        );

        Assert.Equal(MissingNativeComponent.None, verdict.Missing);
        Assert.Equal([second.Soname, first.Soname], verdict.Loaded.Select(l => l.Library.Soname));
        Assert.All(verdict.Loaded, l => Assert.Equal(CudaLibrarySource.AppLocal, l.Source));
    }

    [Fact]
    public void AppLocalCublasLt_LoadsBecauseAppLocalNvJitLinkIsPreloaded()
    {
        // An app-local libcublasLt.so.12 that needs libnvJitLink.so.12, with
        // neither on the loader path. Loaded by absolute path, cublasLt does
        // not search its own directory, so nvJitLink has to be in the
        // process first.
        var inProcess = new HashSet<string>(StringComparer.Ordinal);
        var onLoaderPath = RequiredSonames.Where(s => s != "libcublasLt.so.12").ToHashSet();
        NativeLoadAttempt Load(string path)
        {
            if (!path.StartsWith(AppLocal, StringComparison.Ordinal))
                return onLoaderPath.Contains(path) ? NativeLoadAttempt.Success : NativeLoadAttempt.Failed("not found");
            var soname = path[(AppLocal.Length + 1)..];
            if (soname == "libcublasLt.so.12" && !inProcess.Contains("libnvJitLink.so.12"))
                return NativeLoadAttempt.Failed("libnvJitLink.so.12: cannot open shared object file");
            inProcess.Add(soname);
            return NativeLoadAttempt.Success;
        }

        var verdict = LinuxCudaResolution.Resolve(
            AppLocal,
            Files("libcublasLt.so.12", "libnvJitLink.so.12"),
            Load
        );

        Assert.Equal(MissingNativeComponent.None, verdict.Missing);
        Assert.Equal(CudaLibrarySource.AppLocal, verdict.Find("libcublasLt.so.12")!.Value.Source);
    }

    // ── Libraries already in the process ─────────────────────────────────

    [Fact]
    public void SonameAlreadyLoadedFromElsewhere_IsReportedAsThatCopy_AndTheAppLocalCopyIsNotLoaded()
    {
        // Another component loaded the system libcudart.so.12 first. The
        // provider's dependency binds to that copy, so the app-local one must
        // not be loaded or reported.
        var loader = new FakeLoader([.. RequiredSonames, AppLocalPath("libcudart.so.12")]);

        var verdict = LinuxCudaResolution.Resolve(
            AppLocal,
            Files("libcudart.so.12"),
            loader.Load,
            inProcess: new Dictionary<string, string> { ["libcudart.so.12"] = "/usr/lib/x86_64-linux-gnu" }
        );

        var cudart = verdict.Find("libcudart.so.12")!.Value;
        Assert.Equal(CudaLibrarySource.AlreadyInProcess, cudart.Source);
        Assert.Equal("/usr/lib/x86_64-linux-gnu/libcudart.so.12", cudart.LoadedFrom);
        Assert.DoesNotContain(AppLocalPath("libcudart.so.12"), loader.Calls);
        Assert.DoesNotContain("libcudart.so.12", loader.Calls);
        Assert.Equal(MissingNativeComponent.None, verdict.Missing);
    }

    [Fact]
    public void SonameAlreadyLoadedFromAppLocal_IsReportedAsAppLocal_WithoutLoadingAgain()
    {
        var loader = new FakeLoader(RequiredSonames);

        var verdict = LinuxCudaResolution.Resolve(
            AppLocal + "/",
            Files("libcudnn.so.9"),
            loader.Load,
            inProcess: new Dictionary<string, string> { ["libcudnn.so.9"] = AppLocal }
        );

        Assert.Equal(CudaLibrarySource.AppLocal, verdict.Find("libcudnn.so.9")!.Value.Source);
        Assert.DoesNotContain(loader.Calls, c => c.EndsWith("libcudnn.so.9", StringComparison.Ordinal));
    }

    [Fact]
    public void InProcessDirectories_ReadsEveryTabledLibraryFromMaps()
    {
        var directories = LinuxCudaResolution.InProcessDirectories(Maps);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["libcudart.so.12"] = "/usr/local/cuda-12.9/targets/x86_64-linux/lib",
                ["libcublasLt.so.12"] = "/usr/local/cuda-12.9/targets/x86_64-linux/lib",
                ["libcudnn.so.9"] = "/opt/my libs/cudnn/lib",
            },
            directories
        );
    }

    [Fact]
    public void AppLocalPaths_AreJoinedWithSlash_OnEveryHost()
    {
        Assert.Equal("/a/b/libx.so.1", LinuxCudaResolution.JoinLinuxPath("/a/b", "libx.so.1"));
        Assert.Equal("/a/b/libx.so.1", LinuxCudaResolution.JoinLinuxPath("/a/b/", "libx.so.1"));
    }

    // ── Where a library loaded by soname came from: /proc/self/maps ──────

    private static readonly string[] Maps =
    [
        "7f1c2a000000-7f1c2a021000 r--p 00000000 08:01 1311 /usr/lib/x86_64-linux-gnu/libc.so.6",
        "7f1c2b000000-7f1c2b1f0000 r--p 00000000 08:01 2201 /usr/local/cuda-12.9/targets/x86_64-linux/lib/libcublasLt.so.12.9.1.4",
        "7f1c2c000000-7f1c2c010000 r--p 00000000 08:01 2202 /usr/local/cuda-12.9/targets/x86_64-linux/lib/libcudart.so.12.9.79",
        "7f1c2d000000-7f1c2d010000 r--p 00000000 08:01 3301 /opt/my libs/cudnn/lib/libcudnn.so.9.8.0 (deleted)",
        "7f1c2e000000-7f1c2e001000 rw-p 00000000 00:00 0 ",
        "7ffd1c000000-7ffd1c021000 rw-p 00000000 00:00 0 [stack]",
    ];

    [Fact]
    public void DirectoryOfLoaded_MatchesTheVersionedRealFile()
    {
        Assert.Equal(
            "/usr/local/cuda-12.9/targets/x86_64-linux/lib",
            LinuxCudaResolution.DirectoryOfLoaded(Maps, "libcudart.so.12")
        );
    }

    [Fact]
    public void DirectoryOfLoaded_DoesNotConfuseCublasWithCublasLt()
    {
        Assert.Null(LinuxCudaResolution.DirectoryOfLoaded(Maps, "libcublas.so.12"));
        Assert.Equal(
            "/usr/local/cuda-12.9/targets/x86_64-linux/lib",
            LinuxCudaResolution.DirectoryOfLoaded(Maps, "libcublasLt.so.12")
        );
    }

    [Fact]
    public void DirectoryOfLoaded_KeepsSpacesAndDropsTheDeletedMarker()
    {
        Assert.Equal(
            "/opt/my libs/cudnn/lib",
            LinuxCudaResolution.DirectoryOfLoaded(Maps, "libcudnn.so.9")
        );
    }

    [Fact]
    public void DirectoryOfLoaded_ExactSonameAtTheRoot()
    {
        Assert.Equal(
            "/",
            LinuxCudaResolution.DirectoryOfLoaded(
                ["7f00-7f01 r--p 00000000 08:01 1 /libcuda.so.1"],
                "libcuda.so.1"
            )
        );
    }
}
