// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using FrameFlow.Native;
using Microsoft.Extensions.Logging;

namespace FrameFlow.MotionClip;

/// <summary>
/// FFmpeg native bootstrap, shared by both run modes. The pipeline uses
/// libswscale (via <c>ResizeAndConvert</c>) and the FrameFlow.Encoding H.264
/// encoder, neither of which goes through <c>MediaPlayer.CreateAsync</c>, so
/// the native DllImport resolver must be registered explicitly here or the
/// first frame throws <c>DllNotFoundException</c>.
/// </summary>
internal static class NativeBootstrap
{
    /// <summary>
    /// Initializes FFmpeg, resolving the native directory from the repo's
    /// <c>runtimes/&lt;rid&gt;/native</c>. Logs and returns <see langword="false"/>
    /// on failure (the caller should abort — no encoder, no clips).
    /// </summary>
    public static bool InitializeFfmpeg(ILoggerFactory loggerFactory, ILogger logger)
    {
        var options = new FrameFlowNativeOptions
        {
            SkipHardwareProbe = true,
            CustomFfmpegPath = ResolveFfmpegDirectory(),
        };
        var result = new FrameFlowBootstrapper(options, loggerFactory).Initialize();
        if (!result.IsSuccess)
        {
            logger.LogError(
                "FFmpeg bootstrap failed ({Message}) — cannot encode clips. Run "
                    + "scripts/fetch-ffmpeg.cs to populate runtimes/<rid>/native, or pass a "
                    + "valid FFmpeg directory.",
                result.Message
            );
        }
        return result.IsSuccess;
    }

    // Walk up from the app base directory to the repo root (FrameFlow.slnx) and
    // use runtimes/<rid>/native. Returns null to fall back to default resolution.
    private static string? ResolveFfmpegDirectory()
    {
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string rid =
            OperatingSystem.IsWindows() ? $"win-{arch}"
            : OperatingSystem.IsMacOS() ? $"osx-{arch}"
            : $"linux-{arch}";
        string libName =
            OperatingSystem.IsWindows() ? "avutil-59.dll"
            : OperatingSystem.IsMacOS() ? "libavutil.59.dylib"
            : "libavutil.so.59";

        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "FrameFlow.slnx")))
            {
                string native = Path.Combine(dir, "runtimes", rid, "native");
                return File.Exists(Path.Combine(native, libName)) ? native : null;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
