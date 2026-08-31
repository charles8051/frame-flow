#!/usr/bin/env dotnet
#:property TargetFramework=net10.0

// Generates synthetic test media files using FFmpeg for FrameFlow integration tests.
//
// Uses FFmpeg's lavfi filters (testsrc2, sine) to produce a minimal set of media
// files covering key codecs, containers, pixel formats, and edge cases.
// Files are placed in tests/corpus/files/.
// A precise test-expectations manifest is written to tests/corpus/test-expectations.json.
//
// Requires FFmpeg on PATH or in runtimes/{rid}/native/.
//
// Usage:
//   dotnet run scripts/generate-test-corpus.cs
//   dotnet run scripts/generate-test-corpus.cs -- --force
//   dotnet run scripts/generate-test-corpus.cs -- --include-benchmarks
//
// Benchmark fixtures are opt-in: they are large and slow to produce, and no
// conformance test needs them. See Category 6.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
var includeBenchmarks = args.Contains("--include-benchmarks", StringComparer.OrdinalIgnoreCase);
string? ffmpegOverride = null;
var ffmpegIdx = Array.FindIndex(
    args,
    a => a.Equals("--ffmpeg", StringComparison.OrdinalIgnoreCase)
);
if (ffmpegIdx >= 0 && ffmpegIdx + 1 < args.Length)
    ffmpegOverride = args[ffmpegIdx + 1];

var repoRoot = FindRepoRoot();
var ffmpeg = FindFfmpeg(ffmpegOverride, repoRoot);

if (ffmpeg is null)
{
    Console.Error.WriteLine(
        """
        FFmpeg not found. Either:
          1. Install FFmpeg and ensure it is on PATH
          2. Run: dotnet run scripts/fetch-ffmpeg.cs
          3. Pass --ffmpeg /path/to/ffmpeg
        """
    );
    return 1;
}

var version = RunProcess(ffmpeg, "-version")?.Split('\n').FirstOrDefault() ?? "unknown";
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine($"Using: {ffmpeg}");
Console.ResetColor();
Console.WriteLine($"       {version}");
Console.WriteLine();

// Which encoders does THIS build have? The pinned runtime is an LGPL build
// (scripts/runtime-manifest.json), configured --disable-libx264
// --disable-libx265 because x264 and x265 are GPL. Fixtures that name a
// missing encoder are reported as unavailable rather than as a generic
// FFmpeg failure, so the reason is legible without reading stderr.
const char NL = (char)10;
var availableEncoders = new HashSet<string>(StringComparer.Ordinal);
foreach (var line in (RunProcess(ffmpeg, "-hide_banner -encoders") ?? "").Split(NL))
{
    var t = line.TrimStart();
    if (t.Length > 7 && (t[0] is 'V' or 'A' or 'S'))
    {
        var name = t.Substring(6).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (name is not null)
            availableEncoders.Add(name);
    }
}

var unavailable = 0;
var missingEncoders = new SortedSet<string>(StringComparer.Ordinal);

var outputDir = Path.Combine(repoRoot, "tests", "corpus", "files");
var subsDir = Path.Combine(repoRoot, "tests", "corpus", "subs");
Directory.CreateDirectory(outputDir);

var generated = 0;
var skipped = 0;
var failed = 0;
var expectations = new List<TestExpectation>();

// ═════════════════════════════════════════════════════════════════════════════
// Category 1: Basic video formats
// ═════════════════════════════════════════════════════════════════════════════

Section("Category 1: Basic video formats");

Gen(
    "test-video-h264-yuv420p.mp4",
    "-f lavfi -i testsrc2=size=320x240:rate=24:duration=3",
    "-c:v libopenh264 -preset fast -pix_fmt yuv420p -an",
    new(Width: 320, Height: 240, Fps: 24, DurationSec: 3.0)
);

Gen(
    "test-video-h265-yuv420p.mp4",
    "-f lavfi -i testsrc2=size=320x240:rate=24:duration=3",
    "-c:v libkvazaar -preset fast -pix_fmt yuv420p -an",
    new(Width: 320, Height: 240, Fps: 24, DurationSec: 3.0)
);

Gen(
    "test-video-vp9-yuv420p.webm",
    "-f lavfi -i testsrc2=size=320x240:rate=24:duration=3",
    "-c:v libvpx-vp9 -b:v 200k -pix_fmt yuv420p -an",
    new(Width: 320, Height: 240, Fps: 24, DurationSec: 3.0)
);

Gen(
    "test-video-av1-yuv420p.mkv",
    "-f lavfi -i testsrc2=size=320x240:rate=24:duration=3",
    "-c:v libaom-av1 -cpu-used 8 -b:v 200k -pix_fmt yuv420p -an",
    new(Width: 320, Height: 240, Fps: 24, DurationSec: 3.0)
);

// ═════════════════════════════════════════════════════════════════════════════
// Category 2: Basic audio formats
// ═════════════════════════════════════════════════════════════════════════════

Section("Category 2: Basic audio formats");

Gen(
    "test-audio-aac.m4a",
    "-f lavfi -i sine=frequency=440:sample_rate=44100:duration=3",
    "-c:a aac -b:a 128k -ac 2",
    new(AudioSampleRate: 44100, AudioChannels: 2, DurationSec: 3.0)
);

Gen(
    "test-audio-mono-aac.m4a",
    "-f lavfi -i sine=frequency=440:sample_rate=44100:duration=3",
    "-c:a aac -b:a 96k -ac 1",
    new(AudioSampleRate: 44100, AudioChannels: 1, DurationSec: 3.0)
);

Gen(
    "test-audio-mp3.mp3",
    "-f lavfi -i sine=frequency=440:sample_rate=44100:duration=3",
    "-c:a libmp3lame -b:a 128k -ac 2",
    new(AudioSampleRate: 44100, AudioChannels: 2, DurationSec: 3.0)
);

Gen(
    "test-audio-opus.ogg",
    "-f lavfi -i sine=frequency=440:sample_rate=48000:duration=3",
    "-c:a libopus -b:a 96k -ac 2",
    new(AudioSampleRate: 48000, AudioChannels: 2, DurationSec: 3.0)
);

Gen(
    "test-audio-flac.flac",
    "-f lavfi -i sine=frequency=440:sample_rate=44100:duration=3",
    "-c:a flac -ac 2",
    new(AudioSampleRate: 44100, AudioChannels: 2, DurationSec: 3.0)
);

// ═════════════════════════════════════════════════════════════════════════════
// Category 3: Combined A/V
// ═════════════════════════════════════════════════════════════════════════════

Section("Category 3: Combined A/V");

Gen(
    "test-av-h264-aac.mp4",
    "-f lavfi -i testsrc2=size=320x240:rate=30:duration=3 -f lavfi -i sine=frequency=440:sample_rate=44100:duration=3",
    "-c:v libopenh264 -preset fast -pix_fmt yuv420p -c:a aac -b:a 128k -ac 2 -shortest",
    new(
        Width: 320,
        Height: 240,
        Fps: 30,
        DurationSec: 3.0,
        AudioSampleRate: 44100,
        AudioChannels: 2
    )
);

Gen(
    "test-av-h264-aac.mkv",
    "-f lavfi -i testsrc2=size=320x240:rate=30:duration=3 -f lavfi -i sine=frequency=440:sample_rate=44100:duration=3",
    "-c:v libopenh264 -preset fast -pix_fmt yuv420p -c:a aac -b:a 128k -ac 2 -shortest",
    new(
        Width: 320,
        Height: 240,
        Fps: 30,
        DurationSec: 3.0,
        AudioSampleRate: 44100,
        AudioChannels: 2
    )
);

// ═════════════════════════════════════════════════════════════════════════════
// Category 4: Pixel format variety
// ═════════════════════════════════════════════════════════════════════════════

Section("Category 4: Pixel format variety");

Gen(
    "test-video-h264-yuv444p.mp4",
    "-f lavfi -i testsrc2=size=320x240:rate=24:duration=3",
    "-c:v libx264 -preset fast -pix_fmt yuv444p -profile:v high444 -an",
    new(Width: 320, Height: 240, Fps: 24, DurationSec: 3.0)
);

Gen(
    "test-video-av1-yuv444p-hard.mkv",
    "-f lavfi -i testsrc2=size=1280x720:rate=60:duration=3",
    "-c:v libaom-av1 -cpu-used 8 -b:v 2M -pix_fmt yuv444p -an",
    new(Width: 1280, Height: 720, Fps: 60, DurationSec: 3.0)
);

// ═════════════════════════════════════════════════════════════════════════════
// Category 5: Edge cases
// ═════════════════════════════════════════════════════════════════════════════

Section("Category 5: Edge cases");

Gen(
    "test-subsecond.mp4",
    "-f lavfi -i testsrc2=size=320x240:rate=30:duration=0.5 -f lavfi -i sine=frequency=440:sample_rate=44100:duration=0.5",
    "-c:v libopenh264 -preset fast -pix_fmt yuv420p -c:a aac -b:a 128k -shortest",
    new(
        Width: 320,
        Height: 240,
        Fps: 30,
        DurationSec: 0.5,
        AudioSampleRate: 44100,
        AudioChannels: 2
    )
);

Gen(
    "test-audio-only.mp4",
    "-f lavfi -i sine=frequency=440:sample_rate=44100:duration=3",
    "-c:a aac -b:a 128k -ac 2",
    new(AudioSampleRate: 44100, AudioChannels: 2, DurationSec: 3.0)
);

Gen(
    "test-video-only.mp4",
    "-f lavfi -i testsrc2=size=320x240:rate=24:duration=3",
    "-c:v libopenh264 -preset fast -pix_fmt yuv420p -an",
    new(Width: 320, Height: 240, Fps: 24, DurationSec: 3.0)
);

Gen(
    "test-fps-24.mp4",
    "-f lavfi -i testsrc2=size=320x240:rate=24:duration=3",
    "-c:v libopenh264 -preset fast -pix_fmt yuv420p -an",
    new(Width: 320, Height: 240, Fps: 24, DurationSec: 3.0)
);

Gen(
    "test-fps-30.mp4",
    "-f lavfi -i testsrc2=size=320x240:rate=30:duration=3",
    "-c:v libopenh264 -preset fast -pix_fmt yuv420p -an",
    new(Width: 320, Height: 240, Fps: 30, DurationSec: 3.0)
);

Gen(
    "test-fps-60.mp4",
    "-f lavfi -i testsrc2=size=320x240:rate=60:duration=3",
    "-c:v libopenh264 -preset fast -pix_fmt yuv420p -an",
    new(Width: 320, Height: 240, Fps: 60, DurationSec: 3.0)
);

Gen(
    "test-1080p-h264-aac.mp4",
    "-f lavfi -i testsrc2=size=1920x1080:rate=30:duration=3 -f lavfi -i sine=frequency=440:sample_rate=44100:duration=3",
    "-c:v libopenh264 -preset fast -pix_fmt yuv420p -c:a aac -b:a 128k -ac 2 -shortest",
    new(
        Width: 1920,
        Height: 1080,
        Fps: 30,
        DurationSec: 3.0,
        AudioSampleRate: 44100,
        AudioChannels: 2
    )
);

Gen(
    // 1080p60 WITH an audio track. The 1080p entry above is 30fps and the
    // fps-60 entry is 320x240 video-only, so nothing in the corpus previously
    // combined a full-rate HD video load with a live audio clock -- the exact
    // shape that reproduces frame-flow#125. 10s rather than the usual 3s
    // because the symptom is sustained backpressure, which needs time to build.
    "test-1080p60-h264-aac.mp4",
    "-f lavfi -i testsrc2=size=1920x1080:rate=60:duration=10 -f lavfi -i sine=frequency=440:sample_rate=48000:duration=10",
    "-c:v libopenh264 -preset fast -pix_fmt yuv420p -c:a aac -b:a 128k -ac 2 -shortest",
    new(
        Width: 1920,
        Height: 1080,
        Fps: 60,
        DurationSec: 10.0,
        AudioSampleRate: 48000,
        AudioChannels: 2
    )
);

Gen(
    "test-multi-audio.mkv",
    "-f lavfi -i sine=frequency=440:sample_rate=44100:duration=3 -f lavfi -i sine=frequency=880:sample_rate=44100:duration=3 -f lavfi -i testsrc2=size=320x240:rate=24:duration=3",
    "-c:v libopenh264 -preset fast -pix_fmt yuv420p -c:a aac -b:a 128k -ac 2 -map 2:v -map 0:a -map 1:a -shortest",
    new(
        Width: 320,
        Height: 240,
        Fps: 24,
        DurationSec: 3.0,
        AudioSampleRate: 44100,
        AudioChannels: 2
    )
);

Gen(
    "test-pts-b-frames.mp4",
    "-f lavfi -i testsrc2=size=320x240:rate=24:duration=3",
    "-c:v libx264 -preset fast -pix_fmt yuv420p -bf 2 -b_strategy 0 -an",
    new(Width: 320, Height: 240, Fps: 24, DurationSec: 3.0)
);

var srtPath = Path.Combine(subsDir, "test-subtitles.srt");
if (File.Exists(srtPath))
{
    // -shortest truncates to the SRT end time (~2.5s), yielding ~58 video frames.
    Gen(
        "test-with-subtitles.mkv",
        $"-f lavfi -i testsrc2=size=320x240:rate=24:duration=3 -f lavfi -i sine=frequency=440:sample_rate=44100:duration=3 -f srt -i \"{srtPath}\"",
        "-c:v libopenh264 -preset fast -pix_fmt yuv420p -c:a aac -b:a 128k -ac 2 -c:s srt -map 0:v -map 1:a -map 2:s -shortest",
        new(
            Width: 320,
            Height: 240,
            Fps: 24,
            DurationSec: 2.5,
            AudioSampleRate: 44100,
            AudioChannels: 2
        )
    );
}

// ═════════════════════════════════════════════════════════════════════════════
// ═════════════════════════════════════════════════════════════════════════════
// Category 6: Benchmarks (opt-in, --include-benchmarks)
// ═════════════════════════════════════════════════════════════════════════════
//
// These exist to measure throughput under a realistic decode load, not to check
// conformance. They are skipped by default because they are ~80 MB and take about
// a minute each to encode, against ~1s and <1 MB for every other corpus file.
//
// When skipped they contribute NO test-expectations entry either, so a default
// corpus stays exactly as complete as it was before this category existed.

if (includeBenchmarks)
{
    Section("Category 6: Benchmarks (opt-in)");

    // Why this is not just test-1080p60-h264-aac.mp4 with a longer duration:
    //
    // testsrc2 is a flat synthetic pattern that encodes to almost nothing, so it
    // decodes almost for free and understates any throughput problem. Measured
    // against a real 1080p59.94 H.264 encode on the same box, the plain testsrc2
    // file reported 57.6 fps decoded / 48.2 fps presented where the real file
    // managed 41.8 / 37.3 — it reproduced the shape of frame-flow#145 at roughly
    // 30% of its severity, which is why that issue went unnoticed twice.
    //
    // The `noise` filter is what closes the gap: it forces the encoder to spend
    // real bits, so the decoder does real work. With it, this file measures within
    // a couple of fps of real content on every counter.
    //
    // all_seed fixes the *input*: the noise filter yields the same pixels every
    // run, so the fixture does not drift between regenerations on one machine
    // (verified: two runs, identical hashes). It does NOT make the encoded output
    // byte-identical across machines — the bitstream still depends on the
    // libopenh264 build, and FindFfmpeg deliberately prefers a PATH/Homebrew
    // FFmpeg on macOS over the pinned binary. Treat measurements as comparable
    // within one environment, not across environments.
    //
    // 15 Mbps is deliberate — a 59 Mbps variant measured identically, so this is
    // the realistic end of the range that still reproduces.
    //
    // 45s, not the corpus-standard 3s, because the measurement window is 30s and
    // looping a short file introduces restart bursts that confound the counters.
    Gen(
        "bench-1080p60-h264-aac.mp4",
        "-f lavfi -i testsrc2=size=1920x1080:rate=60:duration=45,noise=alls=14:allf=t:all_seed=12345 -f lavfi -i sine=frequency=440:sample_rate=48000:duration=45",
        "-c:v libopenh264 -rc_mode bitrate -b:v 15M -maxrate 18M -bufsize 30M -pix_fmt yuv420p -c:a aac -b:a 128k -ac 2 -shortest",
        new(
            Width: 1920,
            Height: 1080,
            Fps: 60,
            DurationSec: 45.0,
            AudioSampleRate: 48000,
            AudioChannels: 2
        ),
        // ~50s to encode here; the 60s default would be a coin flip on a slower box.
        timeoutMs: 600_000
    );
}

// Write test-expectations.json
// ═════════════════════════════════════════════════════════════════════════════

var expectationsPath = Path.Combine(repoRoot, "tests", "corpus", "test-expectations.json");
var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
};
File.WriteAllText(expectationsPath, JsonSerializer.Serialize(expectations, jsonOpts));
Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine($"Wrote test expectations: {expectationsPath}");
Console.ResetColor();

// ═════════════════════════════════════════════════════════════════════════════
// Summary
// ═════════════════════════════════════════════════════════════════════════════

Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("=== Summary ===");
Console.ResetColor();
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"  Generated: {generated}");
Console.ResetColor();
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine($"  Skipped:   {skipped}");
Console.ResetColor();
Console.ForegroundColor = failed > 0 ? ConsoleColor.Red : ConsoleColor.DarkGray;
Console.WriteLine($"  Failed:    {failed}");
Console.ResetColor();
Console.ForegroundColor = unavailable > 0 ? ConsoleColor.Yellow : ConsoleColor.DarkGray;
Console.WriteLine($"  Unavail:   {unavailable}");
Console.ResetColor();
Console.WriteLine($"  Output:    {outputDir}");
Console.WriteLine();

if (unavailable > 0)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine(
        $"{unavailable} fixture(s) need encoders this FFmpeg build does not have: "
            + string.Join(", ", missingEncoders)
    );
    Console.WriteLine(
        """
        The pinned runtime (scripts/runtime-manifest.json) is an LGPL build, so
        it is configured --disable-libx264 --disable-libx265: both are GPL. The
        fixtures above are the ones that cannot be produced any other way —
        4:4:4 chroma and B-frames are beyond what libopenh264 encodes.

        Tests needing them will skip. To generate them, point the script at a
        GPL FFmpeg build:

            dotnet run scripts/generate-test-corpus.cs -- --ffmpeg /path/to/gpl/ffmpeg

        Do not switch the pinned runtime to a GPL build without deciding what
        that means for FrameFlow's own licensing.
        """
    );
    Console.ResetColor();
}

if (failed > 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Some files failed to generate. This is not an encoder-availability");
    Console.WriteLine("problem — see the FAIL lines above.");
    Console.ResetColor();
}

return failed > 0 || unavailable > 0 ? 1 : 0;

// ═════════════════════════════════════════════════════════════════════════════
// Helpers
// ═════════════════════════════════════════════════════════════════════════════

void Section(string title)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"=== {title} ===");
    Console.ResetColor();
}

void Gen(string fileName, string inputs, string options, MediaSpec spec, int timeoutMs = 60_000)
{
    var outPath = Path.Combine(outputDir, fileName);

    // Always record expectations, even if file already exists.
    expectations.Add(
        new TestExpectation(
            Filename: fileName,
            DurationSeconds: spec.DurationSec,
            DurationToleranceMs: spec.DurationToleranceMs,
            Width: spec.Width,
            Height: spec.Height,
            Fps: spec.Fps,
            ExpectedVideoFrames: spec.ExpectedVideoFrames,
            AudioSampleRate: spec.AudioSampleRate,
            AudioChannels: spec.AudioChannels,
            HasVideo: spec.HasVideo,
            HasAudio: spec.HasAudio
        )
    );

    if (!force && File.Exists(outPath))
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  SKIP  {fileName} -- exists");
        Console.ResetColor();
        skipped++;
        return;
    }

    // Named encoders this fixture needs. A build without one cannot produce
    // the file, and substituting a different encoder silently would be worse
    // than not producing it — see the pix_fmt verification below.
    var needed = new List<string>();
    var optTokens = options.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    for (var i = 0; i + 1 < optTokens.Length; i++)
    {
        if (optTokens[i] is "-c:v" or "-c:a" or "-c:s" && !optTokens[i + 1].StartsWith('-'))
            needed.Add(optTokens[i + 1]);
    }

    var absent = needed.Where(e =>
        availableEncoders.Count > 0
        && !availableEncoders.Contains(e)
        && e is not ("copy" or "srt")
    ).ToList();

    if (absent.Count > 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  UNAVL {fileName} -- this build has no {string.Join(", ", absent)}");
        Console.ResetColor();
        foreach (var e in absent)
            missingEncoders.Add(e);
        unavailable++;
        return;
    }

    Console.WriteLine($"  GEN   {fileName}");

    var arguments = $"-y -loglevel warning {inputs} {options} \"{outPath}\"";

    try
    {
        var psi = new ProcessStartInfo(ffmpeg!, arguments)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)!;

        // Drain stderr on a separate task. Reading it to completion first would
        // block until FFmpeg exits, which makes the WaitForExit timeout below
        // unreachable — a stalled encoder would hang the generator forever.
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }

            // A half-written file passes the File.Exists check on the next run and
            // would then be treated as a valid fixture.
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { /* best effort */ }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  FAIL  {fileName} (timed out after {timeoutMs / 1000}s; partial output removed)");
            Console.ResetColor();
            failed++;
            return;
        }

        var stderr = stderrTask.GetAwaiter().GetResult();

        if (proc.ExitCode != 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  FAIL  {fileName} (exit code {proc.ExitCode})");
            if (!string.IsNullOrWhiteSpace(stderr))
                Console.WriteLine($"        {stderr.Trim()}");
            Console.ResetColor();
            failed++;
            return;
        }

        // Verify the encoder honoured the request. FFmpeg exits 0 when an
        // encoder silently downconverts — libopenh264 accepts "-pix_fmt
        // yuv444p" and writes yuv420p, and accepts "-bf 2" and writes no
        // B-frames. A fixture that exists but encodes something other than
        // what it is named for is worse than a missing one: it passes the
        // presence check and tests the wrong thing.
        var wantPixFmt = Array.IndexOf(optTokens, "-pix_fmt") is var pfi && pfi >= 0 && pfi + 1 < optTokens.Length
            ? optTokens[pfi + 1]
            : null;
        if (wantPixFmt is not null)
        {
            var probe = FindFfprobe(ffmpeg!);
            var actual = probe is null
                ? null
                : RunProcess(
                    probe,
                    $"-v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 \"{outPath}\""
                )?.Trim();

            // An unverifiable output is treated as a failed one. Accepting it
            // would defeat the whole point of the check: the fixture would ship
            // with whatever the encoder decided to write, which is the silent
            // substitution this guard exists to catch.
            if (string.IsNullOrEmpty(actual))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(
                    $"  FAIL  {fileName} -- asked for {wantPixFmt}, could not verify "
                        + (probe is null ? "(ffprobe not found)" : "(ffprobe returned nothing)")
                );
                Console.ResetColor();
                File.Delete(outPath);
                failed++;
                return;
            }

            if (actual != wantPixFmt)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(
                    $"  FAIL  {fileName} -- asked for {wantPixFmt}, encoder wrote {actual}"
                );
                Console.ResetColor();
                File.Delete(outPath);
                failed++;
                return;
            }
        }

        // Same check for B-frames, and for the same reason: libopenh264 accepts
        // "-bf 2" and writes none, exiting 0. A fixture named for frame
        // reordering that contains no B pictures tests the opposite of what it
        // claims.
        var bfIdx = Array.IndexOf(optTokens, "-bf");
        if (bfIdx >= 0 && bfIdx + 1 < optTokens.Length && int.TryParse(optTokens[bfIdx + 1], out var wantBf) && wantBf > 0)
        {
            var probe = FindFfprobe(ffmpeg!);
            var hasB = probe is null
                ? null
                : RunProcess(
                    probe,
                    $"-v error -select_streams v:0 -show_entries stream=has_b_frames -of csv=p=0 \"{outPath}\""
                )?.Trim();

            if (string.IsNullOrEmpty(hasB) || hasB == "0")
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(
                    $"  FAIL  {fileName} -- asked for {wantBf} B-frames, file reports has_b_frames="
                        + (string.IsNullOrEmpty(hasB) ? "(unverifiable)" : hasB)
                );
                Console.ResetColor();
                File.Delete(outPath);
                failed++;
                return;
            }
        }

        var sizeKb = new FileInfo(outPath).Length / 1024.0;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(
            $"        OK {sizeKb:F1} KB  (expect {spec.ExpectedVideoFrames?.ToString() ?? "-"} video frames)"
        );
        Console.ResetColor();
        generated++;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  FAIL  {fileName} -- {ex.Message}");
        Console.ResetColor();
        failed++;
    }
}

/// <summary>
/// ffprobe sits beside ffmpeg in every layout this script supports (PATH,
/// runtimes/{rid}/native/, or an explicit --ffmpeg path). Returns null when it
/// is absent, in which case output verification is skipped rather than fatal.
/// </summary>
static string? FindFfprobe(string ffmpegPath)
{
    var dir = Path.GetDirectoryName(ffmpegPath);
    if (string.IsNullOrEmpty(dir))
        return null;
    var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe";
    var candidate = Path.Combine(dir, name);
    return File.Exists(candidate) ? candidate : null;
}

static string? FindFfmpeg(string? overridePath, string repoRoot)
{
    if (overridePath is not null && File.Exists(overridePath))
        return overridePath;

    var exeExt = OperatingSystem.IsWindows() ? ".exe" : "";
    var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    // On macOS the bundled ffmpeg binary is extracted from a Homebrew bottle and
    // keeps @@HOMEBREW_CELLAR@@ placeholder paths that are never resolved outside
    // of a real Homebrew install. It also requires libavdevice/libavfilter/libpostproc
    // which are not bundled. Prefer the system ffmpeg (Homebrew keg or PATH) so the
    // binary actually resolves its own dylib dependencies at runtime.
    if (OperatingSystem.IsMacOS())
    {
        var homebrewPrefix = arch == "arm64" ? "/opt/homebrew" : "/usr/local";

        // Prefer the keg-only ffmpeg@7 binary for version consistency.
        var kegBin = Path.Combine(homebrewPrefix, "opt", "ffmpeg@7", "bin", "ffmpeg");
        if (File.Exists(kegBin))
            return kegBin;

        // Fall back to whatever is on PATH (e.g. a brew-linked or system ffmpeg).
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, "ffmpeg");
            if (File.Exists(candidate))
                return candidate;
        }

        // Last resort: bundled binary (may fail if it was extracted from a raw bottle).
        var bundled = Path.Combine(repoRoot, "runtimes", $"osx-{arch}", "native", "ffmpeg");
        if (File.Exists(bundled))
            return bundled;

        return null;
    }

    // Windows / Linux: bundled binary works reliably; fall back to PATH.
    var rid = OperatingSystem.IsWindows() ? $"win-{arch}" : $"linux-{arch}";
    var bundledPath = Path.Combine(repoRoot, "runtimes", rid, "native", $"ffmpeg{exeExt}");
    if (File.Exists(bundledPath))
        return bundledPath;

    var systemPathDirs =
        Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
    foreach (var dir in systemPathDirs)
    {
        var candidate = Path.Combine(dir, $"ffmpeg{exeExt}");
        if (File.Exists(candidate))
            return candidate;
    }

    return null;
}

static string? RunProcess(string exe, string arguments)
{
    try
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5_000);
        return output.Trim();
    }
    catch
    {
        return null;
    }
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

// ═════════════════════════════════════════════════════════════════════════════
// Types (must be after all top-level statements and local functions)
// ═════════════════════════════════════════════════════════════════════════════

record MediaSpec(
    int? Width = null,
    int? Height = null,
    int? Fps = null,
    double DurationSec = 0,
    int? AudioSampleRate = null,
    int? AudioChannels = null
)
{
    public bool HasVideo => Width is not null;
    public bool HasAudio => AudioSampleRate is not null;
    public int? ExpectedVideoFrames => HasVideo ? (int)(Fps!.Value * DurationSec) : null;

    /// <summary>
    /// Lossy audio codecs (AAC, MP3, Opus) add encoder delay/padding of up to ~300ms.
    /// FLAC is lossless but the resampler may add a few ms. Allow generous tolerance.
    /// </summary>
    public int DurationToleranceMs => DurationSec < 1 ? 500 : 350;
}

record TestExpectation(
    string Filename,
    double DurationSeconds,
    int DurationToleranceMs,
    int? Width,
    int? Height,
    int? Fps,
    int? ExpectedVideoFrames,
    int? AudioSampleRate,
    int? AudioChannels,
    bool HasVideo,
    bool HasAudio
);
