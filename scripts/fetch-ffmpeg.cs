#!/usr/bin/env dotnet
#:property TargetFramework=net10.0

// Downloads pre-built FFmpeg 7.1 shared libraries for local development and CI.
//
// Reads scripts/runtime-manifest.json to determine which libraries are needed,
// their expected SHA-256 checksums, and where to place them. Supports manifest-
// driven download with checksum verification and skip-if-present logic.
//
// Detects the current platform RID (or accepts --rid / --all), downloads the
// matching FFmpeg shared build from BtbN/FFmpeg-Builds (GitHub), and extracts
// the shared libraries into runtimes/{rid}/native/ at the repository root.
//
// Idempotent -- skips files that already exist with matching checksums.
// Pass --force to re-download regardless.
// Per ADR-0014, these binaries are gitignored and not tracked in source control.
//
// Usage:
//   dotnet run scripts/fetch-ffmpeg.cs                              — current platform
//   dotnet run scripts/fetch-ffmpeg.cs -- --rid linux-x64           — specific platform
//   dotnet run scripts/fetch-ffmpeg.cs -- --all                     — all downloadable platforms
//   dotnet run scripts/fetch-ffmpeg.cs -- --rid linux-x64 --force   — force re-download
//   dotnet run scripts/fetch-ffmpeg.cs -- --license gpl             — GPL build instead of LGPL

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
var all = args.Contains("--all", StringComparer.OrdinalIgnoreCase);

var license = "lgpl";
var licenseIdx = Array.FindIndex(
    args,
    a => a.Equals("--license", StringComparison.OrdinalIgnoreCase)
);
if (licenseIdx >= 0 && licenseIdx + 1 < args.Length)
    license = args[licenseIdx + 1].ToLowerInvariant();

string? ridOverride = null;
var ridIdx = Array.FindIndex(args, a => a.Equals("--rid", StringComparison.OrdinalIgnoreCase));
if (ridIdx >= 0 && ridIdx + 1 < args.Length)
    ridOverride = args[ridIdx + 1];

// ── Configuration ────────────────────────────────────────────────────────────

const string FfmpegVersion = "7.1";

// Pinned to a dated autobuild rather than the rolling `latest` tag — and
// specifically to an END-OF-MONTH one. That distinction is the whole point.
//
// BtbN re-cuts `latest` as FFmpeg moves on, and its n7.1 assets were dropped
// when they went to n8.1/n9.0 — which 404'd this script on every fresh clone
// and made `dotnet run scripts/fetch-ffmpeg.cs` (README's first step) fail.
// ADR-0014 already called this out: "pin to specific build artifacts, not
// rolling latest".
//
// Pinning to a dated build is necessary but not sufficient. The previous pin,
// autobuild-2026-08-16-13-00, was a mid-month daily, and BtbN deletes those:
// the whole release tag 404s roughly two weeks after it is cut, not just the
// asset. It broke every fresh clone and publish.yml's fetch step within a
// fortnight of being chosen.
//
// Their retention, measured 2026-08-30 across 38 releases: `latest`, about
// fourteen daily autobuilds, then END-OF-MONTH snapshots going back to
// 2024-09. Every monthly checked back to 2025-10 still carries n7.1 assets;
// `latest` no longer does. So pin to a monthly and the horizon is years
// rather than weeks.
//
// Dated assets carry the git-describe suffix in the version segment
// (ffmpeg-n7.1.5-12-g1fdbca85aa-…) rather than the tag, so ArchiveVersion is
// separate from FfmpegVersion.
//
// To move to a newer build:
//   1. Pick an END-OF-MONTH tag (autobuild-YYYY-MM-<last day>-*) that still
//      carries `*-lgpl-shared-7.1.*` assets. A mid-month tag will rot.
//   2. Update both constants together.
//   3. Regenerate the sha256 values in runtime-manifest.json — they are per
//      library file and change with every build. A stale hash fails the
//      download rather than silently passing.
//   4. Update the build identity in THIRD-PARTY-NOTICES.md, which is the
//      LGPL-2.1 corresponding-source pointer.
const string BuildTag = "autobuild-2026-07-31-14-10";
const string ArchiveVersion = "n7.1.5-12-g1fdbca85aa";
var baseUrl = $"https://github.com/BtbN/FFmpeg-Builds/releases/download/{BuildTag}";

var platforms = new Dictionary<string, PlatformInfo>
{
    // Each entry lists the 7 FFmpeg DLLs/.so/.dylib shipped by BtbN's
    // shared builds. The library DLLs (avcodec, avformat, etc.) are what
    // FrameFlow P/Invokes; avdevice and avfilter are dynamic dependencies
    // of the bundled `ffmpeg.exe` / `ffprobe.exe` tools and MUST also be
    // copied — without them the tools fail to load with
    // STATUS_DLL_NOT_FOUND on a fresh machine.
    ["win-x64"] = new(
        "win64",
        "zip",
        [
            "avformat-61.dll",
            "avcodec-61.dll",
            "avutil-59.dll",
            "avdevice-61.dll",
            "avfilter-10.dll",
            "swscale-8.dll",
            "swresample-5.dll",
        ]
    ),
    ["win-arm64"] = new(
        "winarm64",
        "zip",
        [
            "avformat-61.dll",
            "avcodec-61.dll",
            "avutil-59.dll",
            "avdevice-61.dll",
            "avfilter-10.dll",
            "swscale-8.dll",
            "swresample-5.dll",
        ]
    ),
    ["linux-x64"] = new(
        "linux64",
        "tar.xz",
        [
            "libavformat.so.61",
            "libavcodec.so.61",
            "libavutil.so.59",
            "libavdevice.so.61",
            "libavfilter.so.10",
            "libswscale.so.8",
            "libswresample.so.5",
        ]
    ),
    ["linux-arm64"] = new(
        "linuxarm64",
        "tar.xz",
        [
            "libavformat.so.61",
            "libavcodec.so.61",
            "libavutil.so.59",
            "libavdevice.so.61",
            "libavfilter.so.10",
            "libswscale.so.8",
            "libswresample.so.5",
        ]
    ),
    ["osx-x64"] = new(
        "sonoma",
        "tar.gz",
        [
            "libavformat.61.dylib",
            "libavcodec.61.dylib",
            "libavutil.59.dylib",
            "libavdevice.61.dylib",
            "libavfilter.10.dylib",
            "libswscale.8.dylib",
            "libswresample.5.dylib",
        ]
    ),
    ["osx-arm64"] = new(
        "arm64_sequoia",
        "tar.gz",
        [
            "libavformat.61.dylib",
            "libavcodec.61.dylib",
            "libavutil.59.dylib",
            "libavdevice.61.dylib",
            "libavfilter.10.dylib",
            "libswscale.8.dylib",
            "libswresample.5.dylib",
        ]
    ),
};

// ── Determine which RIDs to process ─────────────────────────────────────────

var ridsToProcess = new List<string>();

if (all)
{
    // --all: download all platforms that have a remote build (excludes macOS)
    ridsToProcess.AddRange(platforms.Where(p => p.Value.Label is not null).Select(p => p.Key));
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(
        $"Downloading FFmpeg for all {ridsToProcess.Count} platforms: {string.Join(", ", ridsToProcess)}"
    );
    Console.ResetColor();
}
else
{
    ridsToProcess.Add(ridOverride ?? GetCurrentRid());
}

// ── Paths ────────────────────────────────────────────────────────────────────

var repoRoot = FindRepoRoot();

// ── Load manifest ────────────────────────────────────────────────────────────

var manifestPath = Path.Combine(repoRoot, "scripts", "runtime-manifest.json");
Dictionary<string, Dictionary<string, string>> allManifestChecksums = new();

// The manifest's hashes are of the pinned LGPL build's files, and nothing in
// it is keyed by licence. Applying them to a `--license gpl` run would be
// wrong twice over: every extracted GPL library would fail the comparison, and
// worse, the skip-if-present check above would accept already-installed LGPL
// files as "matching" and never fetch the GPL build at all — silently leaving
// the wrong binaries in place. So the hashes are LGPL-only, and a GPL run says
// out loud that it is unverified.
if (license != "lgpl")
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine(
        $"--license {license}: the manifest pins the LGPL build, so these downloads are "
            + "NOT checksum-verified."
    );
    Console.ResetColor();
}
else if (File.Exists(manifestPath))
{
    Console.WriteLine($"Loading manifest: {manifestPath}");
    foreach (var rid in ridsToProcess)
    {
        var checksums = LoadManifestChecksums(manifestPath, rid);
        if (checksums is not null)
            allManifestChecksums[rid] = checksums;
    }
}
else
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine(
        $"Manifest not found at {manifestPath} -- will download without checksum verification."
    );
    Console.ResetColor();
}

// ── Process each RID ────────────────────────────────────────────────────────

using var http = new HttpClient();
http.Timeout = TimeSpan.FromMinutes(10);

var totalCopied = 0;
var totalFailed = 0;

foreach (var rid in ridsToProcess)
{
    Console.WriteLine();
    Console.WriteLine(new string('─', 60));
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Platform: {rid}");
    Console.ResetColor();

    if (!platforms.TryGetValue(rid, out var platform))
    {
        Console.Error.WriteLine($"  Unsupported RID: {rid}");
        totalFailed++;
        continue;
    }

    var nativeDir = Path.Combine(repoRoot, "runtimes", rid, "native");
    allManifestChecksums.TryGetValue(rid, out var manifestChecksums);

    // Say so per RID, not just per run. A licence other than the pinned one
    // already warned above, but a RID with no manifest entry reaches here
    // silently — macOS does, because it copies from a local Homebrew keg and
    // there is no pinned artifact to hash. Without this an osx run prints
    // nothing but PLACED, which is indistinguishable from a verified one at a
    // glance.
    //
    // A warning rather than a refusal. Refusing would delete two working paths
    // to close a gap neither of them can close: the GPL build has no pinned
    // hashes to check against, and the macOS trust root is the developer's own
    // Homebrew install rather than an artifact this script fetched.
    if (manifestChecksums is null)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  No checksums for {rid} — files below are NOT verified.");
        Console.ResetColor();
    }

    // ── Check if already present ────────────────────────────────────────

    // Presence alone is not evidence when the requested licence is not the one
    // the manifest pins. The files on disk carry no record of which build they
    // came from, so an LGPL install looks exactly like a satisfied GPL request
    // and the skip below would leave the wrong binaries in place while
    // reporting success. Only the pinned LGPL build can be recognised on disk,
    // so any other licence always re-downloads.
    if (!force && license == "lgpl")
    {
        var allPresent = true;
        var allChecksumMatch = true;

        foreach (var lib in platform.Libs)
        {
            var filePath = Path.Combine(nativeDir, lib);
            if (!File.Exists(filePath))
            {
                allPresent = false;
                break;
            }

            if (
                manifestChecksums is not null
                && manifestChecksums.TryGetValue(lib, out var expectedHash)
            )
            {
                var actualHash = ComputeSha256(filePath);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    allChecksumMatch = false;
                    break;
                }
            }
        }

        if (allPresent && allChecksumMatch)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(
                manifestChecksums is not null
                    ? "  Already present with matching checksums — skipping."
                    : "  Already present (no checksums in the manifest to compare) — skipping."
            );
            Console.ResetColor();
            continue;
        }
    }

    // ── macOS: copy from installed Homebrew keg ──────────────────────────

    if (rid.StartsWith("osx-", StringComparison.Ordinal))
    {
        // Raw Homebrew bottles contain unresolved placeholder strings
        // (@@HOMEBREW_PREFIX@@, @@HOMEBREW_CELLAR@@) and depend on optional
        // Homebrew packages (libsoxr, libvpx, etc.) that may not be installed.
        // Downloading and extracting the bottle directly produces dylibs that
        // fail to load at runtime. The only reliable macOS approach is to copy
        // from the already-installed Homebrew keg, where all placeholders have
        // been resolved and transitive dependencies are satisfied.
        var homebrewPrefix = rid == "osx-arm64" ? "/opt/homebrew" : "/usr/local";
        var kegLibDir = Path.Combine(homebrewPrefix, "opt", "ffmpeg@7", "lib");
        var kegBinDir = Path.Combine(homebrewPrefix, "opt", "ffmpeg@7", "bin");

        if (!Directory.Exists(kegLibDir))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ffmpeg@7 not found at {kegLibDir}.");
            Console.WriteLine("  Install it first: brew install ffmpeg@7");
            Console.ResetColor();
            totalFailed++;
            continue;
        }

        Directory.CreateDirectory(nativeDir);
        var kegCopied = 0;

        foreach (var lib in platform.Libs)
        {
            var src = Path.Combine(kegLibDir, lib);
            if (!File.Exists(src))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    {lib, -25} NOT FOUND in {kegLibDir}");
                Console.ResetColor();
                continue;
            }
            File.Copy(src, Path.Combine(nativeDir, lib), overwrite: true);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"    {lib, -25} ");
            Console.ResetColor();
            Console.WriteLine("PLACED");
            kegCopied++;
        }

        // Copy ffmpeg and ffprobe executables from the keg bin/ directory.
        foreach (var tool in new[] { "ffmpeg", "ffprobe" })
        {
            var src = Path.Combine(kegBinDir, tool);
            if (File.Exists(src))
            {
                var dest = Path.Combine(nativeDir, tool);
                File.Copy(src, dest, overwrite: true);
                SetExecutable(dest);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"    {tool, -25} ");
                Console.ResetColor();
                Console.WriteLine("PLACED");
                kegCopied++;
            }
        }

        // Patch inter-FFmpeg install names to use @loader_path so the bundled
        // dylibs find each other without requiring the original Homebrew keg paths.
        // Transitive Homebrew dependencies (libsoxr, libvpx, etc.) keep their
        // absolute Homebrew paths; those remain valid as long as ffmpeg@7 is installed.
        if (kegCopied > 0)
        {
            Console.WriteLine("  Patching dylib install names...");
            FixMacOsDylibInstallNames(nativeDir, platform.Libs);
        }

        totalCopied += kegCopied;
        continue; // Skip the download/extract path below.
    }

    // ── Resolve download URL (non-macOS platforms) ──────────────────────

    string downloadUrl;
    string archiveName;

    archiveName =
        $"ffmpeg-{ArchiveVersion}-{platform.Label}-{license}-shared-{FfmpegVersion}.{platform.Extension}";
    downloadUrl = $"{baseUrl}/{archiveName}";

    var tempDir = Path.Combine(Path.GetTempPath(), $"frameflow-ffmpeg-{rid}");
    var tempFile = Path.Combine(tempDir, archiveName);

    Console.WriteLine($"  Downloading: {archiveName}");

    Directory.CreateDirectory(tempDir);

    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead
        );
        response.EnsureSuccessStatusCode();

        await using var fs = File.Create(tempFile);
        await response.Content.CopyToAsync(fs);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  Download failed: {ex.Message}");
        totalFailed++;
        continue;
    }

    var downloadSize = new FileInfo(tempFile).Length / (1024.0 * 1024.0);
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  Downloaded {downloadSize:F1} MB");
    Console.ResetColor();

    // ── Extract ─────────────────────────────────────────────────────────

    var extractDir = Path.Combine(tempDir, "extracted");
    if (Directory.Exists(extractDir))
        Directory.Delete(extractDir, recursive: true);

    Console.WriteLine("  Extracting...");

    if (platform.Extension == "zip")
    {
        ZipFile.ExtractToDirectory(tempFile, extractDir);
    }
    else
    {
        // tar.xz — use tar command (available on Windows 10+, Linux, macOS).
        // On Windows, tar cannot create symlinks without admin privileges. Linux
        // .so archives contain symlinks (e.g., libavutil.so.59 → libavutil.so.59.39.100).
        // tar exits with code 2 for symlink failures but still extracts the real files.
        // We treat this as non-fatal and rely on FindLibrary to locate the real files.
        Directory.CreateDirectory(extractDir);
        var tar = Process.Start(
            new ProcessStartInfo("tar", $"-xf \"{tempFile}\" -C \"{extractDir}\"")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            }
        )!;
        await tar.WaitForExitAsync();
        var tarStderr = await tar.StandardError.ReadToEndAsync();
        if (tar.ExitCode != 0 && !tarStderr.Contains("Cannot create symlink"))
        {
            Console.Error.WriteLine($"  tar extraction failed: {tarStderr}");
            totalFailed++;
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch { }
            continue;
        }
    }

    // ── Copy shared libraries ───────────────────────────────────────────

    // Find the archive root — the directory that contains bin/ and/or lib/.
    // On Windows, DLLs are in bin/; on Linux, .so files are in lib/.
    // Search from the parent of bin/ or lib/ so FindLibrary can see both.
    var binOrLib = Directory
        .EnumerateDirectories(extractDir, "*", SearchOption.AllDirectories)
        .FirstOrDefault(d => Path.GetFileName(d) is "bin" or "lib");

    if (binOrLib is null)
    {
        Console.Error.WriteLine("  Could not find bin/ or lib/ directory in extracted archive.");
        totalFailed++;
        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch { }
        continue;
    }

    var sourceDir = Path.GetDirectoryName(binOrLib)!;

    Directory.CreateDirectory(nativeDir);
    var copied = 0;
    var failed = 0;

    foreach (var lib in platform.Libs)
    {
        var src = FindLibrary(sourceDir, lib);
        if (src is null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"    {lib, -25} NOT FOUND");
            Console.ResetColor();
            continue;
        }

        if (
            manifestChecksums is not null
            && manifestChecksums.TryGetValue(lib, out var expectedHash)
        )
        {
            var actualHash = ComputeSha256(src);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"    {lib, -25} CHECKSUM MISMATCH");
                Console.ResetColor();
                failed++;
                continue;
            }
        }

        File.Copy(src, Path.Combine(nativeDir, lib), overwrite: true);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"    {lib, -25} ");
        Console.ResetColor();
        Console.WriteLine(manifestChecksums?.ContainsKey(lib) == true ? "VERIFIED" : "PLACED");
        copied++;
    }

    // Also copy ffmpeg and ffprobe executables
    var isWindows = rid.StartsWith("win-", StringComparison.Ordinal);
    var exeExt = isWindows ? ".exe" : "";
    foreach (var tool in new[] { "ffmpeg", "ffprobe" })
    {
        var toolName = $"{tool}{exeExt}";
        var src = Directory
            .EnumerateFiles(extractDir, toolName, SearchOption.AllDirectories)
            .FirstOrDefault();
        if (src is not null)
        {
            // Verified on the same terms as the libraries. These two are not
            // inert payload — generate-test-corpus.cs executes ffmpeg — so an
            // archive that kept the expected library bytes and swapped a tool
            // would otherwise pass every check this script makes and still put
            // an unverified executable on disk.
            if (
                manifestChecksums is not null
                && manifestChecksums.TryGetValue(toolName, out var expectedToolHash)
            )
            {
                var actualToolHash = ComputeSha256(src);
                if (
                    !string.Equals(actualToolHash, expectedToolHash, StringComparison.OrdinalIgnoreCase)
                )
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"    {toolName, -25} CHECKSUM MISMATCH");
                    Console.ResetColor();
                    failed++;
                    continue;
                }
            }

            var dest = Path.Combine(nativeDir, toolName);
            File.Copy(src, dest, overwrite: true);

            // Make executable on Unix
            if (!isWindows)
                SetExecutable(dest);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"    {toolName, -25} ");
            Console.ResetColor();
            Console.WriteLine(
                manifestChecksums?.ContainsKey(toolName) == true ? "VERIFIED" : "PLACED"
            );
            copied++;
        }
    }

    totalCopied += copied;
    totalFailed += failed;

    // Cleanup temp
    try
    {
        Directory.Delete(tempDir, recursive: true);
    }
    catch { }
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
else
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine(
        $"Done! {totalCopied} files placed across {ridsToProcess.Count} platform(s)."
    );
    Console.ResetColor();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("These files are gitignored per ADR-0014.");
    Console.ResetColor();
    return 0;
}

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
    throw new PlatformNotSupportedException("Unable to detect current platform.");
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
    return Environment.CurrentDirectory;
}

static string ComputeSha256(string filePath)
{
    using var stream = File.OpenRead(filePath);
    var hash = SHA256.HashData(stream);
    return Convert.ToHexStringLower(hash);
}

static void SetExecutable(string path)
{
    try
    {
        Process
            .Start(
                new ProcessStartInfo("chmod", $"+x \"{path}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                }
            )
            ?.WaitForExit(5000);
    }
    catch
    { /* best effort — chmod not available on Windows */
    }
}

/// <summary>
/// Finds a library file in the extracted archive. On Linux, tar extraction on Windows
/// creates MSYS symlinks that .NET cannot follow. The real file has the fully-versioned
/// name (e.g., libavutil.so.59.39.100). We search for it by pattern and return it
/// so the caller can copy it with the SONAME filename.
/// </summary>
static string? FindLibrary(string searchDir, string libName)
{
    foreach (var file in Directory.EnumerateFiles(searchDir, "*", SearchOption.AllDirectories))
    {
        var name = Path.GetFileName(file);

        // Exact match (works for .dll files and non-symlinked .so/.dylib files)
        if (name.Equals(libName, StringComparison.Ordinal))
        {
            try
            {
                if (new FileInfo(file).Length > 1024)
                    return file;
            }
            catch { }
        }

        // Linux .so: libName = "libavutil.so.59", real file = "libavutil.so.59.39.100"
        if (
            name.StartsWith(libName + ".", StringComparison.Ordinal)
            && name.Length > libName.Length + 1
        )
        {
            try
            {
                if (new FileInfo(file).Length > 1024)
                    return file;
            }
            catch { }
        }

        // macOS .dylib: libName = "libavutil.59.dylib", real file = "libavutil.59.39.100.dylib"
        // Strip ".dylib" from both, compare the prefix
        if (
            libName.EndsWith(".dylib", StringComparison.Ordinal)
            && name.EndsWith(".dylib", StringComparison.Ordinal)
        )
        {
            var libBase = libName[..^".dylib".Length]; // "libavutil.59"
            var nameBase = name[..^".dylib".Length]; // "libavutil.59.39.100"
            if (
                nameBase.StartsWith(libBase + ".", StringComparison.Ordinal)
                && nameBase.Length > libBase.Length + 1
            )
            {
                try
                {
                    if (new FileInfo(file).Length > 1024)
                        return file;
                }
                catch { }
            }
        }
    }

    return null;
}

/// <summary>
/// Patches inter-FFmpeg install names in the copied dylibs so they reference each
/// other via <c>@loader_path</c> rather than absolute Homebrew keg paths.
/// This makes the bundled dylibs self-contained for FFmpeg-to-FFmpeg dependencies.
/// Transitive Homebrew dependencies (libsoxr, libvpx, etc.) are left as-is;
/// they resolve from the Homebrew prefix as long as ffmpeg@7 remains installed.
/// Each dylib is re-signed with an ad-hoc signature after modification.
/// </summary>
static void FixMacOsDylibInstallNames(string nativeDir, string[] libs)
{
    var dylibNames = new HashSet<string>(
        libs.Where(l => l.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)),
        StringComparer.OrdinalIgnoreCase
    );

    foreach (var lib in dylibNames)
    {
        var dylibPath = Path.Combine(nativeDir, lib);
        if (!File.Exists(dylibPath))
            continue;

        // Fix the dylib's own install name.
        RunTool("install_name_tool", ["-id", $"@loader_path/{lib}", dylibPath]);

        // Parse otool -L output to find references that point to other bundled dylibs.
        var otoolOutput = RunToolOutput("otool", ["-L", dylibPath]);
        var lines = otoolOutput.Split('\n').Skip(1); // first line is "filename:"

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            // Format: "<dep-path> (compatibility version X, current version Y)"
            var parenIdx = trimmed.IndexOf(" (", StringComparison.Ordinal);
            var depPath = parenIdx >= 0 ? trimmed[..parenIdx] : trimmed;

            // Only fix references to other bundled FFmpeg dylibs.
            var depFileName = Path.GetFileName(depPath);
            if (!dylibNames.Contains(depFileName))
                continue;

            var newRef = $"@loader_path/{depFileName}";
            if (!string.Equals(depPath, newRef, StringComparison.Ordinal))
            {
                RunTool("install_name_tool", ["-change", depPath, newRef, dylibPath]);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    {lib}: {depFileName} → @loader_path");
                Console.ResetColor();
            }
        }

        // Re-apply an ad-hoc signature after modifying the binary.
        RunTool("codesign", ["--force", "--sign", "-", dylibPath]);
    }
}

static void RunTool(string tool, string[] args)
{
    try
    {
        var psi = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        Process.Start(psi)?.WaitForExit(15_000);
    }
    catch
    { /* best effort */
    }
}

static string RunToolOutput(string tool, string[] args)
{
    try
    {
        var psi = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        return output;
    }
    catch
    {
        return string.Empty;
    }
}

static Dictionary<string, string>? LoadManifestChecksums(string manifestPath, string rid)
{
    var json = File.ReadAllText(manifestPath);
    using var doc = JsonDocument.Parse(json);

    if (!doc.RootElement.TryGetProperty("runtimes", out var runtimes))
        return null;

    if (!runtimes.TryGetProperty(rid, out var ridElement))
        return null;

    if (!ridElement.TryGetProperty("libraries", out var libraries))
        return null;

    var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var lib in libraries.EnumerateArray())
    {
        var name = lib.GetProperty("name").GetString();
        if (name is not null && lib.TryGetProperty("sha256", out var sha256Prop))
        {
            var sha256 = sha256Prop.GetString();
            if (sha256 is not null)
                checksums[name] = sha256;
        }
    }

    return checksums.Count > 0 ? checksums : null;
}

record PlatformInfo(string? Label, string? Extension, string[] Libs);
