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
//   - FrameFlow.Inference.Dml, on Windows: the DirectML.dll in the process is
//     the package's, not Windows' own, which is too old on some Windows 10
//     builds (#433). The app is built without a runtime identifier, so the
//     package's DirectML.dll stays under runtimes/win-x64/native, where ONNX
//     Runtime finds it only because it sits beside onnxruntime.dll. A session is
//     attempted too; a host with no DirectX 12 adapter may refuse it, and that
//     is not what this checks.
//
// Pack first, then point the script at the folder:
//
//   dotnet pack FrameFlow.slnx -c Release -p:FrameFlowLocalFeedDisable=true -o <dir>
//   dotnet run scripts/package-smoke.cs -- --packages <dir>
//
// Exits non-zero, naming the app, when any check fails.

using System.Diagnostics;
using System.Text.RegularExpressions;

string? packagesArg = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--packages")
        packagesArg = args[i + 1];
}

if (packagesArg is null || !Directory.Exists(packagesArg))
{
    Console.Error.WriteLine("Usage: dotnet run scripts/package-smoke.cs -- --packages <folder of .nupkg files>");
    return 2;
}

string packages = Path.GetFullPath(packagesArg);
string repo = FindRepoRoot();
string model = Path.Combine(repo, "tests", "FrameFlow.Inference.Dml.Tests", "OnnxModel.cs");

// FrameFlow packages come from the folder only, so a published release cannot stand in for the
// one under test. Their own FrameFlow dependencies are in the same folder.
string version = VersionOf(packages, "FrameFlow.Inference.Cpu");
string root = Path.Combine(Path.GetTempPath(), "frameflow-package-smoke");
string globalPackages = Path.Combine(root, "nuget-packages");
Directory.CreateDirectory(globalPackages);

// A local rebuild on the same commit repacks the same version, which a warm package folder would
// serve from cache.
foreach (var stale in Directory.EnumerateDirectories(globalPackages, "frameflow.*"))
    Directory.Delete(stale, recursive: true);

int failures = 0;
failures += Run("cpu", "FrameFlow.Inference.Cpu", CpuProgram);
if (OperatingSystem.IsWindows())
    failures += Run("dml", "FrameFlow.Inference.Dml", DmlProgram);

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

        nint module = GetModuleHandleW("DirectML.dll");
        if (module == 0)
        {
            Console.WriteLine("DirectML.dll is not loaded");
            return 1;
        }

        var path = new StringBuilder(1024);
        GetModuleFileNameW(module, path, path.Capacity);
        string expected = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "DirectML.dll");
        Console.WriteLine($"DirectML.dll loaded from {path}");
        return string.Equals(path.ToString(), expected, StringComparison.OrdinalIgnoreCase) ? 0 : 1;

        [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern nint GetModuleHandleW(string name);

        [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern int GetModuleFileNameW(nint module, StringBuilder path, int size);
        """;
}
