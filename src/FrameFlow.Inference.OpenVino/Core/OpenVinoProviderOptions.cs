// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text.Json;

namespace FrameFlow.Inference.OpenVino.Core;

/// <summary>
/// What an <see cref="OpenVinoInferenceSessionOptions"/> asks ONNX Runtime for: the provider's
/// options, with OpenVINO's own properties as the <c>load_config</c> JSON, and the session's config
/// entries. Pure.
/// </summary>
internal static class OpenVinoProviderOptions
{
    /// <summary>The name ONNX Runtime appends the provider by.</summary>
    public const string ProviderName = "OpenVINO";

    /// <summary>The session entry that makes ONNX Runtime refuse a model OpenVINO takes only part of.</summary>
    public const string DisableCpuFallback = "session.disable_cpu_ep_fallback";

    private static readonly string[] Devices = ["CPU", "GPU", "NPU"];

    /// <summary><c>device_type</c> and <c>load_config</c>, as the provider takes them.</summary>
    public static IReadOnlyDictionary<string, string> For(OpenVinoInferenceSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["device_type"] = options.Device,
            ["load_config"] = LoadConfig(options),
        };
    }

    /// <summary>The session config entries: the CPU fallback refusal, unless it is allowed.</summary>
    public static IReadOnlyDictionary<string, string> SessionEntries(OpenVinoInferenceSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!options.AllowCpuFallback)
            entries[DisableCpuFallback] = "1";
        return entries;
    }

    /// <summary>
    /// OpenVINO's properties for the device, as <c>{"GPU": {"PERFORMANCE_HINT": "LATENCY", ...}}</c>.
    /// </summary>
    /// <remarks>
    /// The provider looks a device's properties up by its name without the index, so <c>GPU.1</c>
    /// reads the <c>GPU</c> entry; an entry keyed <c>GPU.1</c> it skips, with a warning in its own
    /// log. The session runs one inference at a time, which is what the latency hint tunes for.
    /// </remarks>
    public static string LoadConfig(OpenVinoInferenceSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteStartObject(BaseDevice(options.Device));
            json.WriteString("PERFORMANCE_HINT", "LATENCY");
            if (options.PrecisionHint is { } precision)
                json.WriteString("INFERENCE_PRECISION_HINT", precision);
            if (options.QueueThrottle is { } throttle && BaseDevice(options.Device) == "GPU")
                json.WriteString("GPU_QUEUE_THROTTLE", Throttle(throttle));
            if (options.CacheDirectory is { } cache)
                json.WriteString("CACHE_DIR", cache);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Whether <paramref name="device"/> is <c>CPU</c>, <c>GPU</c> or <c>NPU</c>, alone or with an index.</summary>
    public static bool IsDevice(string device)
    {
        ArgumentNullException.ThrowIfNull(device);
        int dot = device.IndexOf('.', StringComparison.Ordinal);
        string name = dot < 0 ? device : device[..dot];
        if (!Devices.Contains(name, StringComparer.Ordinal))
            return false;
        if (dot < 0)
            return true;

        string index = device[(dot + 1)..];
        return index.Length > 0 && index.All(char.IsAsciiDigit);
    }

    /// <summary>The device's name without its index: <c>GPU</c> for <c>GPU.1</c>.</summary>
    public static string BaseDevice(string device)
    {
        ArgumentNullException.ThrowIfNull(device);
        int dot = device.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? device : device[..dot];
    }

    /// <summary>
    /// The default cache under <paramref name="localApplicationData"/>, or null when there is no such
    /// folder, which <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> reports as an
    /// empty path.
    /// </summary>
    public static string? CacheDirectoryUnder(string localApplicationData)
    {
        ArgumentNullException.ThrowIfNull(localApplicationData);
        return localApplicationData.Length == 0
            ? null
            : Path.Combine(localApplicationData, "FrameFlow.Inference.OpenVino", "cache");
    }

    private static string Throttle(OpenVinoQueueThrottle throttle) => throttle switch
    {
        OpenVinoQueueThrottle.Low => "LOW",
        OpenVinoQueueThrottle.Medium => "MEDIUM",
        OpenVinoQueueThrottle.High => "HIGH",
        _ => throw new ArgumentOutOfRangeException(nameof(throttle), throttle, "Undefined throttle level."),
    };
}
