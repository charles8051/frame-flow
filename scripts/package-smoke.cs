#!/usr/bin/env dotnet
#:property TargetFramework=net10.0

// Consumes the packed packages the way an app does, from their .nupkg files
// rather than project references, and runs them.
//
// A project reference copies files a package's consumer never gets, so the
// in-repo tests say nothing about the published package. This script builds a
// throwaway app against each package and runs it:
//
//   - FrameFlow.Inference.Cpu, on every OS: a CPU session runs a one-node
//     model and returns the exact result (#437).
//   - FrameFlow.Inference.Dml, on Windows: the package's DirectML.dll, not
//     Windows' own, which is too old on some Windows 10 builds (#433). The app
//     is built without a runtime identifier, so the package's DirectML.dll stays
//     under runtimes/win-x64/native, and ONNX Runtime's DirectML provider finds
//     it there only because it sits beside onnxruntime.dll. The app checks that
//     layout. Where a DirectX 12 adapter lets a session open, it also checks the
//     DirectML.dll loaded is that one; without one, as on a hosted runner, ONNX
//     Runtime fails before it loads DirectML.dll at all.
//   - FrameFlow.Inference.OpenVino, on Windows x64: Intel's natives under
//     runtimes/win-x64/native, where ONNX Runtime loads the OpenVINO provider
//     beside onnxruntime.dll and the provider loads OpenVINO, its plugins and
//     TBB. The app checks that layout, that the package's targets kept TBB's
//     debug DLLs and onnxruntime.lib out of it and out of deps.json, and that
//     OpenVINO's CPU device runs the model (#523).
//   - FrameFlow.Decoding, FrameFlow.Encoding and FrameFlow.Native.Runtime, on
//     Windows and Linux: the app bootstraps FFmpeg from the packages' libraries,
//     writes a short H.264 MP4 from synthetic BGRA frames with Mp4VideoWriter
//     (libopenh264), and decodes it back to BGRA with the software VideoDecoder,
//     checking the frame count and size. It runs twice: from its build output,
//     where the libraries sit under runtimes/<rid>/native, and from a
//     framework-dependent `dotnet publish -r <host rid>`, which flattens them into
//     the app folder. Not on macOS: the osx-arm64 FFmpeg FrameFlow ships carries
//     no software H.264 encoder, only VideoToolbox.
//
// The script packs what the apps need itself, not the whole solution. It packs
// FrameFlow.Native.Runtime with whichever RIDs scripts/fetch-ffmpeg.cs staged,
// which in CI is the host's alone. FrameFlow.Native's pack guard wants the win-x64
// libraries that package bundles, which only a Windows x64 host has staged, so
// elsewhere it packs without them; they are win-x64's, so no other app loads them.
//
//   dotnet run scripts/package-smoke.cs                  # builds Release
//   dotnet run scripts/package-smoke.cs -- --no-build    # after a Release build, as CI does
//   dotnet run scripts/package-smoke.cs -- --only media  # one app: cpu, dml, openvino or media
//
// Exits non-zero, naming the app, when any check fails.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

bool noBuild = args.Contains("--no-build");
int onlyAt = Array.IndexOf(args, "--only");
string? only = onlyAt >= 0 && onlyAt + 1 < args.Length ? args[onlyAt + 1] : null;
string repo = FindRepoRoot();
string onnxModel = Path.Combine(repo, "tests", "FrameFlow.Inference.Dml.Tests", "OnnxModel.cs");
string root = Path.Combine(Path.GetTempPath(), "frameflow-package-smoke");
string packages = Path.Combine(root, "packages");
string globalPackages = Path.Combine(root, "nuget-packages");
if (Directory.Exists(packages))
    Directory.Delete(packages, recursive: true);
Directory.CreateDirectory(globalPackages);

// The packages folder outlives the run, so ONNX Runtime and OpenVINO are not downloaded
// every time. The FrameFlow packages in it must not. MinVer gives two branches at the
// same height the same version, and a restore that finds a version already in the
// folder uses it without looking at the feed, so a persistent machine, such as a
// self-hosted runner, would test an earlier run's package. NuGet lowercases the ids.
foreach (var cached in Directory.GetDirectories(globalPackages, "frameflow.*"))
    Directory.Delete(cached, recursive: true);

// Which apps run here.
bool windowsX64 = OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
bool cpu = Wants("cpu");
bool dml = OperatingSystem.IsWindows() && Wants("dml");
bool openVino = windowsX64 && Wants("openvino");
bool media = !OperatingSystem.IsMacOS() && Wants("media");

// The apps' packages and the FrameFlow packages they depend on.
var projects = new List<string>();
if (cpu || dml || openVino)
    projects.AddRange(["FrameFlow.Graph", "FrameFlow.Media", "FrameFlow.Inference.Abstractions", "FrameFlow.Inference.Ort"]);
if (cpu)
    projects.Add("FrameFlow.Inference.Cpu");
if (dml)
    projects.Add("FrameFlow.Inference.Dml");
if (openVino)
    projects.Add("FrameFlow.Inference.OpenVino");
if (media)
    projects.AddRange(["FrameFlow.Graph", "FrameFlow.Media", "FrameFlow.Native", "FrameFlow.Decoding", "FrameFlow.Encoding"]);
if (projects.Count == 0)
{
    Console.Error.WriteLine($"package smoke: no app named '{only}' runs on this OS");
    return 1;
}

foreach (var project in projects.Distinct())
{
    string[] pack = ["pack", Path.Combine(repo, "src", project), "-c", "Release", "-p:FrameFlowLocalFeedDisable=true", "-o", packages];
    if (noBuild)
        pack = [.. pack, "--no-build"];
    if (project == "FrameFlow.Native" && !windowsX64)
        pack = [.. pack, "-p:FrameFlowAllowNativelessPack=true"];
    var (packed, output) = Dotnet(repo, pack);
    if (packed != 0)
    {
        Console.Error.WriteLine(output);
        Console.Error.WriteLine($"package smoke: packing {project} failed");
        return 1;
    }
}

// Outside the solution and never built: it is FFmpeg's libraries and nothing else. Its own
// guards fail the pack when a RID it finds is incomplete, or when it finds none.
if (media)
{
    var (packed, output) = Dotnet(repo, "pack", Path.Combine(repo, "nuget", "FrameFlow.Native.Runtime.csproj"), "-c", "Release", "-o", packages);
    if (packed != 0)
    {
        Console.Error.WriteLine(output);
        Console.Error.WriteLine("package smoke: packing FrameFlow.Native.Runtime failed (has scripts/fetch-ffmpeg.cs run?)");
        return 1;
    }
}

// FrameFlow packages come from that folder only, so a published release cannot stand in for the
// one under test.
string version = VersionOf(packages, "FrameFlow.Media");

// A local rebuild on the same commit repacks the same version, which a warm package folder would
// serve from cache.
foreach (var stale in Directory.EnumerateDirectories(globalPackages, "frameflow.*"))
    Directory.Delete(stale, recursive: true);

int failures = 0;
if (cpu)
    failures += Run("cpu", [("FrameFlow.Inference.Cpu", version)], CpuProgram, model: true);
if (dml)
    failures += Run("dml", [("FrameFlow.Inference.Dml", version)], DmlProgram, model: true);
if (openVino)
    failures += Run("openvino", [("FrameFlow.Inference.OpenVino", version)], OpenVinoProgram, model: true);
if (media)
{
    (string, string)[] references =
    [
        ("FrameFlow.Decoding", version),
        ("FrameFlow.Encoding", version),
        ("FrameFlow.Native.Runtime", VersionOf(packages, "FrameFlow.Native.Runtime")),
    ];
    failures += Run("media", references, MediaProgram, publish: true);
}

Console.WriteLine(failures == 0 ? "package smoke: all apps passed" : $"package smoke: {failures} app(s) failed");
return failures == 0 ? 0 : 1;

bool Wants(string name) => only is null || string.Equals(only, name, StringComparison.OrdinalIgnoreCase);

// Builds and runs the app. With publish, it then publishes it for the host RID and runs that
// too, so the packages' native assets are found where a RID-specific publish puts them.
int Run(string name, (string Id, string Version)[] references, string program, bool model = false, bool publish = false)
{
    string dir = Path.Combine(root, name);
    if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
    Directory.CreateDirectory(dir);

    File.WriteAllText(Path.Combine(dir, "nuget.config"), $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <config>
            <add key="globalPackagesFolder" value="{globalPackages}" />
          </config>
          <packageSources>
            <clear />
            <add key="frameflow" value="{packages}" />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
          <packageSourceMapping>
            <packageSource key="frameflow">
              <package pattern="FrameFlow.*" />
            </packageSource>
            <packageSource key="nuget.org">
              <package pattern="*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """);

    // No runtime identifier: a package's native assets then stay under runtimes/<rid>/native.
    string items = string.Join(
        Environment.NewLine,
        [
            .. references.Select(r => $"""    <PackageReference Include="{r.Id}" Version="[{r.Version}]" />"""),
            .. (model ? new[] { $"""    <Compile Include="{onnxModel}" Link="OnnxModel.cs" />""" } : Array.Empty<string>()),
        ]);
    File.WriteAllText(Path.Combine(dir, $"{name}-smoke.csproj"), $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
          <ItemGroup>
        {items}
          </ItemGroup>
        </Project>
        """);
    File.WriteAllText(Path.Combine(dir, "Program.cs"), program);

    Console.WriteLine($"== {name}: {string.Join(", ", references.Select(r => $"{r.Id} {r.Version}"))}");
    var (exit, output) = Dotnet(dir, "run", "-c", "Release", "--", "build");
    Console.WriteLine(output.TrimEnd());
    if (exit != 0)
    {
        Console.Error.WriteLine($"{name}: failed (exit {exit})");
        return 1;
    }

    if (!publish)
        return 0;

    // Framework-dependent, so no runtime pack is downloaded; the native assets flatten into the
    // app folder all the same. Run through the muxer, which finds the runtime wherever the SDK is.
    string rid = RuntimeInformation.RuntimeIdentifier;
    string published = Path.Combine(dir, "publish");
    Console.WriteLine($"== {name}: published for {rid}");
    (exit, output) = Dotnet(dir, "publish", "-c", "Release", "-r", rid, "--self-contained", "false", "-o", published);
    if (exit != 0)
    {
        Console.WriteLine(output.TrimEnd());
        Console.Error.WriteLine($"{name}: publish for {rid} failed (exit {exit})");
        return 1;
    }

    (exit, output) = Dotnet(dir, Path.Combine(published, $"{name}-smoke.dll"), "publish");
    Console.WriteLine(output.TrimEnd());
    if (exit == 0)
        return 0;

    Console.Error.WriteLine($"{name}: published app failed (exit {exit})");
    return 1;
}

static (int Exit, string Output) Dotnet(string dir, params string[] arguments)
{
    var start = new ProcessStartInfo("dotnet") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments)
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync();
    string stdout = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout + stderr.Result);
}

static string VersionOf(string folder, string id)
{
    var pattern = new Regex("^" + Regex.Escape(id) + @"\.(\d+\.\d+\.\d+.*)\.nupkg$", RegexOptions.IgnoreCase);
    var versions = Directory.EnumerateFiles(folder, "*.nupkg")
        .Select(file => pattern.Match(Path.GetFileName(file)))
        .Where(match => match.Success)
        .Select(match => match.Groups[1].Value)
        .ToList();
    return versions.Count == 1
        ? versions[0]
        : throw new InvalidOperationException($"Expected one {id} package in {folder}, found {versions.Count}.");
}

static string FindRepoRoot()
{
    for (var dir = Directory.GetCurrentDirectory(); dir is not null; dir = Path.GetDirectoryName(dir))
    {
        if (File.Exists(Path.Combine(dir, "FrameFlow.slnx")))
            return dir;
    }

    throw new InvalidOperationException("Run from inside the repository.");
}

partial class Program
{
    const string CpuProgram = """
        using FrameFlow.Graph;
        using FrameFlow.Inference;
        using FrameFlow.Inference.Dml.Tests;
        using Microsoft.ML.OnnxRuntime;
        using System.Runtime.InteropServices;

        Console.WriteLine($"ONNX Runtime {OrtEnv.Instance().GetVersionString()}");
        using var session = new CpuInferenceSession(OnnxModel.Negate(1, 4));
        var pool = new CpuTensorPool();
        var input = pool.Rent<float>(new TensorShape(1, 4));
        var output = pool.Rent<float>(new TensorShape(1, 4));
        float[] values = [1f, -2f, 3.5f, 0f];
        values.AsSpan().CopyTo(input.Span);
        session.Run(input, output);

        float[] result = MemoryMarshal.Cast<byte, float>(output.Bytes.Span).ToArray();
        Console.WriteLine($"negate [{string.Join(", ", values)}] = [{string.Join(", ", result)}]");
        return result.SequenceEqual(values.Select(v => -v)) ? 0 : 1;
        """;

    const string DmlProgram = """
        using FrameFlow.Inference.Dml;
        using FrameFlow.Inference.Dml.Tests;
        using System.Runtime.InteropServices;
        using System.Text;

        if (!OperatingSystem.IsWindows())
            return 0;

        try
        {
            using var session = new DmlInferenceSession(OnnxModel.Negate(1, 4));
            Console.WriteLine("DirectML session opened");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DirectML session not opened here ({ex.GetType().Name}); checking the DLL only");
        }

        // ONNX Runtime's DirectML provider loads DirectML.dll from onnxruntime.dll's own folder
        // before System32, so the package's copy has to be there.
        string native = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
        string expected = Path.Combine(native, "DirectML.dll");
        bool beside = File.Exists(expected) && File.Exists(Path.Combine(native, "onnxruntime.dll"));
        Console.WriteLine($"DirectML.dll beside onnxruntime.dll: {beside}");
        if (!beside)
            return 1;

        nint module = GetModuleHandleW("DirectML.dll");
        if (module == 0)
        {
            // No DirectX 12 adapter: ONNX Runtime failed before it loaded DirectML.dll.
            Console.WriteLine("DirectML.dll not loaded here; the layout is what was checked");
            return 0;
        }

        var path = new StringBuilder(1024);
        GetModuleFileNameW(module, path, path.Capacity);
        Console.WriteLine($"DirectML.dll loaded from {path}");
        return string.Equals(path.ToString(), expected, StringComparison.OrdinalIgnoreCase) ? 0 : 1;

        [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern nint GetModuleHandleW(string name);

        [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern int GetModuleFileNameW(nint module, StringBuilder path, int size);
        """;

    const string OpenVinoProgram = """
        using FrameFlow.Graph;
        using FrameFlow.Inference.Dml.Tests;
        using FrameFlow.Inference.OpenVino;
        using Microsoft.ML.OnnxRuntime;
        using System.Runtime.InteropServices;

        if (!OperatingSystem.IsWindows())
            return 0;

        // ONNX Runtime loads the OpenVINO provider from beside onnxruntime.dll, and the provider
        // loads OpenVINO, its plugins and TBB from there.
        string native = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
        string[] needed =
        [
            "onnxruntime.dll", "onnxruntime_providers_openvino.dll", "onnxruntime_providers_shared.dll",
            "openvino.dll", "openvino_intel_cpu_plugin.dll", "openvino_intel_gpu_plugin.dll",
            "openvino_onnx_frontend.dll", "tbb12.dll",
        ];
        string[] missing = [.. needed.Where(f => !File.Exists(Path.Combine(native, f)))];
        Console.WriteLine($"beside onnxruntime.dll, missing: [{string.Join(", ", missing)}]");

        // The package's targets drop what nothing loads, from the output and from deps.json.
        string[] unused =
        [
            .. Directory.EnumerateFiles(native)
                .Select(f => Path.GetFileName(f))
                .Where(f => f.EndsWith("_debug.dll", StringComparison.OrdinalIgnoreCase) || f == "onnxruntime.lib"),
        ];
        string deps = File.ReadAllText(Directory.GetFiles(AppContext.BaseDirectory, "*.deps.json").Single());
        bool depsListsUnused = deps.Contains("_debug.dll", StringComparison.OrdinalIgnoreCase) || deps.Contains("onnxruntime.lib");
        Console.WriteLine($"unused natives: [{string.Join(", ", unused)}], in deps.json: {depsListsUnused}");
        if (missing.Length > 0 || unused.Length > 0 || depsListsUnused)
            return 1;

        Console.WriteLine($"ONNX Runtime {OrtEnv.Instance().GetVersionString()}");
        using var session = new OpenVinoInferenceSession(
            OnnxModel.Negate(1, 4), new OpenVinoInferenceSessionOptions { Device = "CPU", CacheDirectory = null });
        var pool = new CpuTensorPool();
        var input = pool.Rent<float>(new TensorShape(1, 4));
        var output = pool.Rent<float>(new TensorShape(1, 4));
        float[] values = [1f, -2f, 3.5f, 0f];
        values.AsSpan().CopyTo(input.Span);
        session.Run(input, output);

        float[] result = MemoryMarshal.Cast<byte, float>(output.Bytes.Span).ToArray();
        Console.WriteLine($"OpenVINO CPU: negate [{string.Join(", ", values)}] = [{string.Join(", ", result)}]");
        return result.SequenceEqual(values.Select(v => -v)) ? 0 : 1;
        """;

    // Software only: no hardware probe, hardware decode disabled, the encoder pinned to
    // libopenh264. The argument says where the app is running from: "build" for its build output,
    // where the FFmpeg libraries stay under runtimes/<rid>/native, "publish" for a RID-specific
    // publish, which flattens them into the app folder.
    const string MediaProgram = """
        using FrameFlow.Decoding;
        using FrameFlow.Encoding;
        using FrameFlow.Media;
        using FrameFlow.Native;

        const int Width = 160;
        const int Height = 120;
        const int Fps = 15;
        const int FrameCount = 15;

        string layout = args.FirstOrDefault() ?? "build";
        string appBase = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

        // Bundled only: a system FFmpeg must not stand in for the package's.
        var bootstrap = new FrameFlowBootstrapper(
            new FrameFlowNativeOptions { ProbeSystemLibraries = false, SkipHardwareProbe = true }).Initialize();
        Console.WriteLine($"FFmpeg: {bootstrap.BinarySource} from {bootstrap.ResolvedPath}: {bootstrap.Message}");
        if (!bootstrap.IsSuccess || bootstrap.BinarySource != FfmpegBinarySource.Bundled || bootstrap.ResolvedPath is null)
            return 1;

        string resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bootstrap.ResolvedPath));
        string runtimes = Path.Combine(appBase, "runtimes");
        bool placed = layout == "publish"
            ? string.Equals(resolved, appBase, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(runtimes)
            : resolved.StartsWith(runtimes + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"{layout} layout, libraries where expected: {placed}");
        if (!placed)
            return 1;

        string path = Path.Combine(Path.GetTempPath(), $"frameflow-media-smoke-{Environment.ProcessId}.mp4");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var options = new H264EncoderOptions
            {
                EncoderName = "libopenh264",
                FrameRateNumerator = Fps,
                FrameRateDenominator = 1,
                GopSize = Fps,
                BitRate = 500_000,
            };
            await using (var writer = Mp4VideoWriter.Create(path, options))
            {
                // WriteAsync takes each frame and disposes it.
                for (int i = 0; i < FrameCount; i++)
                    await writer.WriteAsync(Synthetic(i), timeout.Token);

                await writer.CompleteAsync(timeout.Token);
                Console.WriteLine($"encoded {FrameCount} frames with {writer.EncoderInfo}");
            }

            Console.WriteLine($"wrote {new FileInfo(path).Length} bytes");

            await using var demux = (DemuxSession)await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(path), timeout.Token);
            var stream = demux.MediaInfo.VideoStreams.Single();
            Console.WriteLine($"stream: {stream.CodecName} {stream.Width}x{stream.Height}");
            if (stream.CodecName != "h264" || stream.Width != Width || stream.Height != Height)
                return 1;

            await using var decoder = VideoDecoder.Open(
                demux.FormatContextPtr,
                stream.StreamIndex,
                new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled },
                HardwareDecodeCapabilities.Empty,
                loggerFactory: null);
            await using var pipeline = new DecodingPipeline(demux, decoder, audioDecoder: null);

            // Frames are released as they come, so the decoder never waits on one held here.
            var collect = Task.Run(async () =>
            {
                var seen = new List<(PixelFormat Format, int Width, int Height)>();
                await foreach (var frame in decoder.DecodeAsync(timeout.Token))
                {
                    using (frame)
                        seen.Add((frame.Format, frame.Width, frame.Height));
                }
                return seen;
            });
            await pipeline.RunDemuxPumpAsync(timeout.Token);
            await pipeline.FinalizeDecodersAsync(timeout.Token);
            var decoded = await collect;

            int matching = decoded.Count(f => f.Format == PixelFormat.Bgra32 && f.Width == Width && f.Height == Height);
            Console.WriteLine($"decoded {decoded.Count} frames, {matching} of them Bgra32 {Width}x{Height}");
            return decoded.Count == FrameCount && matching == FrameCount ? 0 : 1;
        }
        finally
        {
            File.Delete(path);
        }

        static IVideoFrame Synthetic(int index) =>
            CpuVideoFrame.Create(
                PixelFormat.Bgra32,
                Width,
                Height,
                TimeSpan.FromSeconds(index / (double)Fps),
                TimeSpan.FromSeconds(1.0 / Fps),
                index,
                static (planes, index) =>
                {
                    var px = planes.Y;
                    for (int y = 0; y < planes.Height; y++)
                    {
                        for (int x = 0; x < planes.Width; x++)
                        {
                            int i = (y * planes.StrideY) + (x * 4);
                            px[i + 0] = (byte)(x + (index * 4));
                            px[i + 1] = (byte)(y + (index * 2));
                            px[i + 2] = (byte)(x + y + (index * 6));
                            px[i + 3] = 0xFF;
                        }
                    }
                });
        """;
}
