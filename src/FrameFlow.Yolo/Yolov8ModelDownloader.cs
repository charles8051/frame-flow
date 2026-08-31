// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Yolo;

/// <summary>
/// Downloads yolov8n.onnx on first run and caches it under
/// <c>%LOCALAPPDATA%\FrameFlow.Yolo\models\</c>.
/// </summary>
/// <remarks>
/// Cache path renamed from <c>FrameFlow.Examples.OnnxInference</c> →
/// <c>FrameFlow.Yolo</c> when the OnnxInference example was deleted;
/// the cache belongs to the library that owns the download, not to
/// whichever consumer triggered it. Existing caches under the old
/// path won't be reused — first run after the rename re-downloads.
/// </remarks>
public static partial class Yolov8ModelDownloader
{
    /// <summary>
    /// Canonical URL for YOLOv8n ONNX (input 640x640, FP32, 80 COCO
    /// classes, ~12.8 MB). Hosted on Hugging Face by the
    /// <c>cabelo/yolov8</c> repository — anonymously downloadable.
    /// Hugging Face redirects to a signed CDN URL behind the scenes;
    /// HttpClient follows the redirect by default.
    /// </summary>
    private const string DefaultModelUrl =
        "https://huggingface.co/cabelo/yolov8/resolve/main/yolov8n.onnx";

    /// <summary>Default file name for the cached model.</summary>
    public const string DefaultModelFileName = "yolov8n.onnx";

    public static async Task<string> EnsureModelAvailableAsync(
        CancellationToken ct = default,
        string modelUrl = DefaultModelUrl,
        string? overrideCacheDir = null,
        ILogger? logger = null
    )
    {
        var log = logger ?? NullLogger.Instance;

        var cacheDir =
            overrideCacheDir
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FrameFlow.Yolo",
                "models"
            );
        Directory.CreateDirectory(cacheDir);

        var modelPath = Path.Combine(cacheDir, DefaultModelFileName);
        if (File.Exists(modelPath) && new FileInfo(modelPath).Length > 0)
        {
            LogModelAlreadyCached(log, modelPath, new FileInfo(modelPath).Length);
            return modelPath;
        }

        LogDownloadStarted(log, DefaultModelFileName, modelUrl);
        var sw = Stopwatch.StartNew();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await http.GetAsync(
                    modelUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct
                )
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var tempPath = modelPath + ".part";
            long byteCount;
            await using (var fileStream = File.Create(tempPath))
            {
                await response.Content.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                byteCount = fileStream.Length;
            }
            File.Move(tempPath, modelPath, overwrite: true);

            sw.Stop();
            LogDownloadCompleted(log, modelPath, byteCount, sw.Elapsed.TotalMilliseconds);
            return modelPath;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogDownloadFailed(log, modelUrl, sw.Elapsed.TotalMilliseconds, ex);
            throw;
        }
    }

    // ── Source-generated log methods ─────────────────────────────────

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Model already cached at {ModelPath} ({ByteCount} bytes); skipping download"
    )]
    private static partial void LogModelAlreadyCached(
        ILogger logger,
        string modelPath,
        long byteCount
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Downloading model {ModelFileName} from {ModelUrl}…"
    )]
    private static partial void LogDownloadStarted(
        ILogger logger,
        string modelFileName,
        string modelUrl
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Model downloaded: {ModelPath} ({ByteCount} bytes in {ElapsedMs:F0} ms)"
    )]
    private static partial void LogDownloadCompleted(
        ILogger logger,
        string modelPath,
        long byteCount,
        double elapsedMs
    );

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Model download failed: url={ModelUrl} after {ElapsedMs:F0} ms"
    )]
    private static partial void LogDownloadFailed(
        ILogger logger,
        string modelUrl,
        double elapsedMs,
        Exception ex
    );
}
