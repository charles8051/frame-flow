using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.LiveCaptioning;

/// <summary>
/// Downloads the whisper.cpp <c>ggml-base.en</c> model on first run and caches it under
/// <c>%LOCALAPPDATA%\FrameFlow.Examples.LiveCaptioning\models\</c>. FrameFlow.Whisper takes a
/// model path and does not fetch one; FrameFlow.Yolo's <c>Yolov8ModelDownloader</c> does the
/// same for YOLO.
/// </summary>
/// <remarks>
/// <c>ggml-tiny.en.bin</c> is smaller and less accurate; <c>ggml-small.en.bin</c> is more
/// accurate and slower.
/// </remarks>
internal static class WhisperModelDownloader
{
    private const string ModelFileName = "ggml-base.en.bin";
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + ModelFileName;

    public static async Task<string> EnsureModelAvailableAsync(ILogger logger, CancellationToken ct)
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FrameFlow.Examples.LiveCaptioning",
            "models"
        );
        var modelPath = Path.Combine(cacheDir, ModelFileName);
        if (File.Exists(modelPath) && new FileInfo(modelPath).Length > 0)
            return modelPath;

        Directory.CreateDirectory(cacheDir);
        logger.LogInformation("Downloading the Whisper model from {Url}", ModelUrl);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Written beside the model and moved into place, so a download cut short is never taken for it.
        var partPath = modelPath + ".part";
        await using (var file = File.Create(partPath))
            await response.Content.CopyToAsync(file, ct);
        File.Move(partPath, modelPath, overwrite: true);

        logger.LogInformation("Whisper model saved to {Path}", modelPath);
        return modelPath;
    }
}
