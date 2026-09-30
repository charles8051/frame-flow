#!/usr/bin/env dotnet
#:property TargetFramework=net10.0

// Consumes the packed inference packages the way an app does, from their
// .nupkg files rather than project references, and runs them.
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
//
// The script packs what the apps need itself, not the whole solution:
// FrameFlow.Native refuses to pack without every platform's FFmpeg binaries.
//
//   dotnet run scripts/package-smoke.cs                  # builds Release
//   dotnet run scripts/package-smoke.cs -- --no-build    # after a Release build, as CI does
//
// Exits non-zero, naming the app, when any check fails.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

bool noBuild = args.Contains("--no-build");
string repo = FindRepoRoot();
string model = Path.Combine(repo, "tests", "FrameFlow.Inference.Dml.Tests", "OnnxModel.cs");
string root = Path.Combine(Path.GetTempPath(), "frameflow-package-smoke");
string packages = Path.Combine(root, "packages");
string globalPackages = Path.Combine(root, "nuget-packages");
if (Directory.Exists(packages))
    Directory.Delete(packages, recursive: true);
Directory.CreateDirectory(globalPackages);

// The apps' packages and the FrameFlow packages they depend on.
string[] projects = ["FrameFlow.Graph", "FrameFlow.Media", "FrameFlow.Inference.Abstractions", "FrameFlow.Inference.Ort", "FrameFlow.Inference.Cpu"];
bool windowsX64 = OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
if (OperatingSystem.IsWindows())
    projects = [.. projects, "FrameFlow.Inference.Dml"];
if (windowsX64)
    projects = [.. projects, "FrameFlow.Inference.OpenVino"];
foreach (var project in projects)
{
    string[] pack = ["pack", Path.Combine(repo, "src", project), "-c", "Release", "-p:FrameFlowLocalFeedDisable=true", "-o", packages];
    var (packed, output) = Dotnet(repo, noBuild ? [.. pack, "--no-build"] : pack);
    if (packed != 0)
    {
        Console.Error.WriteLine(output);
        Console.Error.WriteLine($"package smoke: packing {project} failed");
        return 1;
    }
}

// FrameFlow packages come from that folder only, so a published release cannot stand in for the
// one under test.
string version = VersionOf(packages, "FrameFlow.Inference.Cpu");

// A local rebuild on the same commit repacks the same version, which a warm package folder would
// serve from cache.
foreach (var stale in Directory.EnumerateDirectories(globalPackages, "frameflow.*"))
    Directory.Delete(stale, recursive: true);

int failures = 0;
failures += Run("cpu", "FrameFlow.Inference.Cpu", CpuProgram);
if (OperatingSystem.IsWindows())
    failures += Run("dml", "FrameFlow.Inference.Dml", DmlProgram);
if (windowsX64)
    failures += Run("openvino", "FrameFlow.Inference.OpenVino", OpenVinoProgram);

Console.WriteLine(failures == 0 ? "package smoke: all apps passed" : $"package smoke: {failures} app(s) failed");
return failures == 0 ? 0 : 1;

int Run(string name, string package, string program)
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
    File.WriteAllText(Path.Combine(dir, $"{name}-smoke.csproj"), $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="{package}" Version="[{version}]" />
            <Compile Include="{model}" Link="OnnxModel.cs" />
          </ItemGroup>
        </Project>
        """);
    File.WriteAllText(Path.Combine(dir, "Program.cs"), program);

    Console.WriteLine($"== {name}: {package} {version}");
    var (exit, output) = Dotnet(dir, "run", "-c", "Release");
    Console.WriteLine(output.TrimEnd());
    if (exit == 0)
        return 0;

    Console.Error.WriteLine($"{name}: failed (exit {exit})");
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
}
