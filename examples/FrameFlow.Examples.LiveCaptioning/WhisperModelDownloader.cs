using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Examples.LiveCaptioning;

/// <summary>
/// Downloads a Whisper.cpp <c>.bin</c> model on first run and caches it
/// under <c>%LOCALAPPDATA%\FrameFlow.Examples.LiveCaptioning\models\</c>.
/// Mirrors the <c>Yolov8ModelDownloader</c> pattern from the OnnxInference
/// example.
/// </summary>
/// <remarks>
/// Hugging Face mirror hosts the ggml-formatted Whisper models — the same
/// files Whisper.cpp ships with. We default to <c>ggml-base.en.bin</c>
/// (~142 MB) as the demo-friendly compromise between size and quality;
/// <c>tiny.en</c> (~75 MB) is the fast / low-quality option,
/// <c>small.en</c> (~466 MB) is the quality / slow option.
/// </remarks>
public static partial class WhisperModelDownloader
{
    /// <summary>
    /// Canonical URL for ggml-base.en (English-only, 142 MB). Hosted on
    /// Hugging Face by the <c>ggerganov/whisper.cpp</c> repository.
    /// </summary>
    private const string DefaultModelUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin";

    public const string DefaultModelFileName = "ggml-base.en.bin";

    public static async Task<string> EnsureModelAvailableAsync(
        CancellationToken ct = default,
        string modelUrl = DefaultModelUrl,
        string? overrideCacheDir = null,
        string? overrideFileName = null,
        ILogger? logger = null
    )
    {
        var log = logger ?? NullLogger.Instance;

        var cacheDir =
            overrideCacheDir
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FrameFlow.Examples.LiveCaptioning",
                "models"
            );
        Directory.CreateDirectory(cacheDir);

        var modelFileName = overrideFileName ?? DefaultModelFileName;
        var modelPath = Path.Combine(cacheDir, modelFileName);
        if (File.Exists(modelPath) && new FileInfo(modelPath).Length > 0)
        {
            LogModelAlreadyCached(log, modelPath, new FileInfo(modelPath).Length);
            return modelPath;
        }

        LogDownloadStarted(log, modelFileName, modelUrl);
        var sw = Stopwatch.StartNew();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
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

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Whisper model already cached at {ModelPath} ({ByteCount} bytes); skipping download"
    )]
    private static partial void LogModelAlreadyCached(
        ILogger logger,
        string modelPath,
        long byteCount
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Downloading Whisper model {ModelFileName} from {ModelUrl}… (this may take a few minutes on first run)"
    )]
    private static partial void LogDownloadStarted(
        ILogger logger,
        string modelFileName,
        string modelUrl
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Whisper model downloaded: {ModelPath} ({ByteCount} bytes in {ElapsedMs:F0} ms)"
    )]
    private static partial void LogDownloadCompleted(
        ILogger logger,
        string modelPath,
        long byteCount,
        double elapsedMs
    );

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Whisper model download failed: url={ModelUrl} after {ElapsedMs:F0} ms"
    )]
    private static partial void LogDownloadFailed(
        ILogger logger,
        string modelUrl,
        double elapsedMs,
        Exception ex
    );
}
