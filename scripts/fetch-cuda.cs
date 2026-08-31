#!/usr/bin/env dotnet
#:property TargetFramework=net10.0

// Dev-time helper. Downloads CUDA Toolkit + cuDNN redistributable DLLs
// from NVIDIA's public redist endpoint into `runtimes/{rid}/native/` at
// the repo root. NOT shipped in any NuGet package — this is for local
// development convenience so you don't have to install the system-wide
// CUDA Toolkit + cuDNN installers just to run examples that touch
// ORT's CUDA execution provider (LiveCaptioning's YOLOv8 detector,
// and anything else on FrameFlow.Inference.Cuda).
//
// Pairs with `CudaDllResolver` in
// src/FrameFlow.Inference.Cuda/Bootstrap/, which probes
// `AppContext.BaseDirectory/runtimes/{rid}/native/`
// before falling back to `%CUDA_PATH%` and the canonical install
// root. The repo-root `Directory.Build.targets` already copies all
// `.dll` files from `runtimes/{rid}/native/` to project output dirs
// at build time (originally added for FFmpeg per ADR-0014) — that
// same glob picks up the CUDA DLLs this script drops alongside, so
// no MSBuild changes were needed to wire this in.
//
// SHA-256s come from NVIDIA's live redist manifest — no hardcoded
// checksums to drift. We DO pin the toolkit/cuDNN versions; bump the
// defaults below (or pass --cuda-version / --cudnn-version) when you
// want a newer pin.
//
// This script is win-x64 only today. Linux uses a different bootstrap mechanism
// (`NativeLibrary.Load` with absolute paths) that isn't compatible
// with the "drop files in a directory and let the loader find them"
// pattern this script enables.
//
// Usage:
//   dotnet run scripts/fetch-cuda.cs
//   dotnet run scripts/fetch-cuda.cs -- --no-cudnn
//   dotnet run scripts/fetch-cuda.cs -- --cuda-version 12.9.1
//   dotnet run scripts/fetch-cuda.cs -- --cudnn-version 9.22.0
//   dotnet run scripts/fetch-cuda.cs -- --force

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

// ── Defaults ────────────────────────────────────────────────────────────────
// Versions pinned to match ORT 1.26.0's CUDA-12 build expectations.
// Bump these as ORT-Gpu moves; the manifest URLs are constructed from
// them and NVIDIA publishes a redist JSON per patch release.
const string DefaultCudaVersion = "12.9.1";
const string DefaultCudnnVersion = "9.22.0";
const string CudaRedistRoot = "https://developer.download.nvidia.com/compute/cuda/redist";
const string CudnnRedistRoot = "https://developer.download.nvidia.com/compute/cudnn/redist";

// CUDA Toolkit components ORT 1.26's CUDA EP actually links against.
// Found empirically by running LiveCaptioning + chasing the
// "Failed to load shared library" failure until ORT was happy:
//   cuda_cudart  — CUDA runtime (cudart64_12.dll)
//   libcublas    — dense linear algebra (cublas64_12.dll, cublasLt64_12.dll)
//   libcufft     — FFT, used by some ops
//   libcurand    — RNG, used by dropout / sample ops
//   libcusparse  — sparse linear algebra
//   libcusolver  — dense solvers
//   libnvjitlink — JIT linker; cuDNN 9's runtime-compiled engines need it
// Total payload is ~3 GB once cached. The script is idempotent
// (sha-verified, skip-if-present), so the cost is one-time per
// developer.
string[] cudaComponents =
[
    "cuda_cudart",
    "libcublas",
    "libcufft",
    "libcurand",
    "libcusparse",
    "libcusolver",
    "libnvjitlink",
];

// ── CLI ─────────────────────────────────────────────────────────────────────
var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
var skipCudnn = args.Contains("--no-cudnn", StringComparer.OrdinalIgnoreCase);
var cudaVersion = ExtractFlag(args, "--cuda-version") ?? DefaultCudaVersion;
var cudnnVersion = ExtractFlag(args, "--cudnn-version") ?? DefaultCudnnVersion;
var ridOverride = ExtractFlag(args, "--rid");

// ── Platform gate ───────────────────────────────────────────────────────────
var rid = ridOverride ?? GetCurrentRid();
if (rid != "win-x64")
{
    Console.Error.WriteLine($"Unsupported RID for this script: {rid}");
    Console.Error.WriteLine("Today this script handles win-x64 only. See ADR-0011.");
    return 2;
}

// ── Paths ───────────────────────────────────────────────────────────────────
var repoRoot = FindRepoRoot();
var nativeDir = Path.Combine(repoRoot, "runtimes", rid, "native");
Directory.CreateDirectory(nativeDir);

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine($"FrameFlow dev-time CUDA fetch");
Console.ResetColor();
Console.WriteLine($"  repo root : {repoRoot}");
Console.WriteLine($"  output    : {nativeDir}");
Console.WriteLine($"  RID       : {rid}");
Console.WriteLine($"  CUDA      : {cudaVersion}  ({string.Join(", ", cudaComponents)})");
Console.WriteLine($"  cuDNN     : {(skipCudnn ? "(skipped)" : cudnnVersion)}");
Console.WriteLine($"  force     : {force}");
Console.WriteLine();

using var http = new HttpClient();
http.Timeout = TimeSpan.FromMinutes(30); // cuDNN archive is ~1.8 GB

var totalCopied = 0;
var totalFailed = 0;

// ── CUDA components ─────────────────────────────────────────────────────────
try
{
    var cudaManifest = await LoadManifest(http, $"{CudaRedistRoot}/redistrib_{cudaVersion}.json");

    foreach (var component in cudaComponents)
    {
        if (!cudaManifest.TryGetValue(component, out var entry))
        {
            Console.Error.WriteLine($"  [{component}] not in CUDA manifest {cudaVersion}");
            totalFailed++;
            continue;
        }
        var (copied, failed) = await FetchComponent(
            http,
            label: component,
            rootUrl: CudaRedistRoot,
            archive: entry,
            archKey: "windows-x86_64",
            destDir: nativeDir,
            force: force
        );
        totalCopied += copied;
        totalFailed += failed;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"CUDA manifest fetch failed: {ex.Message}");
    return 1;
}

// ── cuDNN ───────────────────────────────────────────────────────────────────
if (!skipCudnn)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("cuDNN archive is large (~1.8 GB). First download takes a while.");
    Console.ResetColor();

    try
    {
        var cudnnManifest = await LoadManifest(
            http,
            $"{CudnnRedistRoot}/redistrib_{cudnnVersion}.json"
        );
        if (!cudnnManifest.TryGetValue("cudnn", out var entry))
        {
            Console.Error.WriteLine($"  [cudnn] not in cuDNN manifest {cudnnVersion}");
            totalFailed++;
        }
        else
        {
            // cuDNN's manifest is shaped as cudnn.<arch>.<cuda-variant>
            // rather than cudnn.<arch> directly. Reach in one extra level.
            var (copied, failed) = await FetchComponent(
                http,
                label: "cudnn",
                rootUrl: CudnnRedistRoot,
                archive: entry,
                archKey: "windows-x86_64",
                destDir: nativeDir,
                force: force,
                cudaVariant: "cuda12"
            );
            totalCopied += copied;
            totalFailed += failed;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"cuDNN manifest fetch failed: {ex.Message}");
        totalFailed++;
    }
}

// ── Summary ─────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine(new string('═', 60));
if (totalFailed > 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Completed with errors: {totalCopied} files placed, {totalFailed} failed.");
    Console.ResetColor();
    return 1;
}
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"Done! {totalCopied} files placed in {nativeDir}");
Console.ResetColor();
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine("These files are gitignored. CudaDllResolver picks them up automatically");
Console.WriteLine("via AppContext.BaseDirectory/runtimes/{rid}/native/ (copied at build time).");
Console.ResetColor();
return 0;

// ── Helpers ──────────────────────────────────────────────────────────────────

static string GetCurrentRid()
{
    var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    if (OperatingSystem.IsWindows())
        return $"win-{arch}";
    if (OperatingSystem.IsMacOS())
        return $"osx-{arch}";
    if (OperatingSystem.IsLinux())
        return $"linux-{arch}";
    throw new PlatformNotSupportedException();
}

static string? ExtractFlag(string[] args, string flag)
{
    var idx = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static string FindRepoRoot()
{
    var dir = Environment.CurrentDirectory;
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir, "FrameFlow.slnx")))
            return dir;
        dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Could not locate FrameFlow.slnx walking up from CWD.");
}

static async Task<Dictionary<string, JsonElement>> LoadManifest(HttpClient http, string url)
{
    Console.WriteLine($"  manifest: {url}");
    var json = await http.GetStringAsync(url);
    using var doc = JsonDocument.Parse(json);
    var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    foreach (var prop in doc.RootElement.EnumerateObject())
    {
        // Top-level metadata (release_date, release_label, …) is string-
        // typed; component entries are objects. Stash the cloned object
        // entries so the JsonDocument can be disposed.
        if (prop.Value.ValueKind == JsonValueKind.Object)
            result[prop.Name] = prop.Value.Clone();
    }
    return result;
}

static async Task<(int copied, int failed)> FetchComponent(
    HttpClient http,
    string label,
    string rootUrl,
    JsonElement archive,
    string archKey,
    string destDir,
    bool force,
    string? cudaVariant = null)
{
    if (!archive.TryGetProperty(archKey, out var archEntry))
    {
        Console.Error.WriteLine($"  [{label}] manifest has no {archKey} entry");
        return (0, 1);
    }

    // cuDNN nests one extra level (cuda12 / cuda13 subkey).
    if (cudaVariant is not null)
    {
        if (!archEntry.TryGetProperty(cudaVariant, out var nested))
        {
            Console.Error.WriteLine($"  [{label}] manifest has no {archKey}.{cudaVariant} entry");
            return (0, 1);
        }
        archEntry = nested;
    }

    var relPath = archEntry.GetProperty("relative_path").GetString()!;
    var sha256 = archEntry.GetProperty("sha256").GetString()!;
    var sizeStr = archEntry.TryGetProperty("size", out var s) ? s.GetString() : null;
    var url = $"{rootUrl}/{relPath}";
    var archiveName = Path.GetFileName(relPath);

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"  [{label}] {archiveName}");
    Console.ResetColor();
    if (long.TryParse(sizeStr, out var size))
        Console.WriteLine($"    size : {size / 1024.0 / 1024.0:F1} MB");
    Console.WriteLine($"    sha  : {sha256[..16]}…");

    var tempDir = Path.Combine(Path.GetTempPath(), $"frame-flow-cuda-{label}");
    Directory.CreateDirectory(tempDir);
    var tempFile = Path.Combine(tempDir, archiveName);

    // Skip download if the archive is already on disk with matching SHA.
    if (!force && File.Exists(tempFile))
    {
        var existing = ComputeSha256(tempFile);
        if (string.Equals(existing, sha256, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"    cache: present + verified — skipping download");
        else
            File.Delete(tempFile);
    }

    if (!File.Exists(tempFile))
    {
        Console.WriteLine($"    fetch: {url}");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(tempFile);
            await resp.Content.CopyToAsync(fs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"    download failed: {ex.Message}");
            return (0, 1);
        }

        var actual = ComputeSha256(tempFile);
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"    SHA-256 mismatch — expected {sha256}, got {actual}");
            File.Delete(tempFile);
            return (0, 1);
        }
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"    verified");
        Console.ResetColor();
    }

    // Extract → copy bin/*.dll → dest.
    var extractDir = Path.Combine(tempDir, "extracted");
    if (Directory.Exists(extractDir))
        Directory.Delete(extractDir, recursive: true);
    Directory.CreateDirectory(extractDir);

    try
    {
        ZipFile.ExtractToDirectory(tempFile, extractDir);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"    extract failed: {ex.Message}");
        return (0, 1);
    }

    var binDir = Directory
        .EnumerateDirectories(extractDir, "bin", SearchOption.AllDirectories)
        .FirstOrDefault();
    if (binDir is null)
    {
        Console.Error.WriteLine($"    archive has no bin/ subdir");
        return (0, 1);
    }

    var copied = 0;
    // AllDirectories: cuDNN nests its DLLs in bin/x64/ rather than
    // bin/ directly. CUDA Toolkit components (cudart, cublas) put
    // them in bin/ flat, which works fine either way — so we just
    // recurse from bin/ unconditionally and copy every DLL we find.
    foreach (var dll in Directory.EnumerateFiles(binDir, "*.dll", SearchOption.AllDirectories))
    {
        var dest = Path.Combine(destDir, Path.GetFileName(dll));
        File.Copy(dll, dest, overwrite: true);
        Console.WriteLine($"      → {Path.GetFileName(dll)}");
        copied++;
    }

    return (copied, 0);
}

static string ComputeSha256(string path)
{
    using var s = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(s));
}
