// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Inference.WinML.Core;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.WinML;

/// <summary>
/// ONNX Runtime inference session through Windows ML, which reaches the vendor execution providers
/// Windows installs, accepting <see cref="Graph.ICpuTensor"/> inputs and outputs. Sibling to
/// <c>DmlInferenceSession</c> and <c>CudaInferenceSession</c> per ADR-0049 §3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Choosing the provider.</b> The constructors let Windows choose by a
/// <see cref="WinMLDevicePolicy"/>, which follows the providers Windows installs.
/// <see cref="OnProvider(string, string, ulong?, ILogger{WinMLInferenceSession}?)"/> names one, and
/// its adapter, when the choice has to be predictable. Both reach the same speed once running.
/// </para>
/// <para>
/// <b>Options.</b> A <see cref="WinMLInferenceSessionOptions"/> sets the graph optimization level,
/// and, for a session on a named provider, that provider's own options (#481).
/// </para>
/// <para>
/// <b>Registration.</b> A session sees the vendor providers only once they are registered:
/// <see cref="WinMLProviders.RegisterInstalledAsync"/> does it once per process, and
/// <see cref="CreateAsync(string, WinMLDevicePolicy)"/> calls it first.
/// </para>
/// <para>
/// <b>First open.</b> A TensorRT-RTX session builds its engine when it opens, which took 3.5 to
/// 5.3 seconds for yolov8n on an RTX 3080 Ti with a warm cache. Open once per process, off the UI
/// thread: <see cref="CreateAsync(string, WinMLDevicePolicy)"/> opens on the thread pool.
/// </para>
/// <para>
/// <b>Threading.</b> A single session is safe for sequential Run calls. Concurrent calls against
/// one session are not supported.
/// </para>
/// </remarks>
public sealed class WinMLInferenceSession : OrtInferenceSessionBase
{
    /// <summary>Loads a model from <paramref name="modelPath"/>, preferring a GPU provider.</summary>
    public WinMLInferenceSession(string modelPath)
        : this(modelPath, WinMLDevicePolicy.PreferGpu) { }

    /// <summary>Loads a model from <paramref name="modelPath"/> on the provider <paramref name="policy"/> chooses.</summary>
    public WinMLInferenceSession(string modelPath, WinMLDevicePolicy policy, ILogger<WinMLInferenceSession>? logger = null)
        : this(modelPath, DefaultOptions, policy, logger) { }

    /// <summary>
    /// Loads a model from <paramref name="modelPath"/> as <paramref name="options"/> says, on the
    /// provider <paramref name="policy"/> chooses.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="options"/> has provider options, which need a named provider.</exception>
    // CA2000: the base owns and disposes the SessionOptions built here.
#pragma warning disable CA2000
    public WinMLInferenceSession(
        string modelPath,
        WinMLInferenceSessionOptions options,
        WinMLDevicePolicy policy = WinMLDevicePolicy.PreferGpu,
        ILogger<WinMLInferenceSession>? logger = null)
        : base(modelPath, PolicyOptions(policy, options))
    {
        _ = logger;
    }
#pragma warning restore CA2000

    /// <summary>Loads a model from <paramref name="modelBytes"/>, preferring a GPU provider.</summary>
    public WinMLInferenceSession(byte[] modelBytes)
        : this(modelBytes, WinMLDevicePolicy.PreferGpu) { }

    /// <summary>Loads a model from <paramref name="modelBytes"/> on the provider <paramref name="policy"/> chooses.</summary>
    public WinMLInferenceSession(byte[] modelBytes, WinMLDevicePolicy policy, ILogger<WinMLInferenceSession>? logger = null)
        : this(modelBytes, DefaultOptions, policy, logger) { }

    /// <summary>
    /// Loads a model from <paramref name="modelBytes"/> as <paramref name="options"/> says, on the
    /// provider <paramref name="policy"/> chooses.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="options"/> has provider options, which need a named provider.</exception>
#pragma warning disable CA2000
    public WinMLInferenceSession(
        byte[] modelBytes,
        WinMLInferenceSessionOptions options,
        WinMLDevicePolicy policy = WinMLDevicePolicy.PreferGpu,
        ILogger<WinMLInferenceSession>? logger = null)
        : base(modelBytes, PolicyOptions(policy, options))
    {
        _ = logger;
    }
#pragma warning restore CA2000

    private WinMLInferenceSession(string modelPath, SessionOptions options)
        : base(modelPath, options) { }

    private WinMLInferenceSession(byte[] modelBytes, SessionOptions options)
        : base(modelBytes, options) { }

    /// <summary>
    /// Registers the installed providers, then loads a model from <paramref name="modelPath"/> on the
    /// provider <paramref name="policy"/> chooses, opening it on the thread pool.
    /// </summary>
    public static Task<WinMLInferenceSession> CreateAsync(string modelPath, WinMLDevicePolicy policy = WinMLDevicePolicy.PreferGpu) =>
        CreateAsync(modelPath, DefaultOptions, policy);

    /// <inheritdoc cref="CreateAsync(string, WinMLDevicePolicy)" />
    /// <param name="modelPath">The model file.</param>
    /// <param name="options">How the model loads. Provider options need a named provider and are refused here.</param>
    /// <param name="policy">How Windows chooses the provider.</param>
    public static async Task<WinMLInferenceSession> CreateAsync(
        string modelPath, WinMLInferenceSessionOptions options, WinMLDevicePolicy policy = WinMLDevicePolicy.PreferGpu)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        ArgumentNullException.ThrowIfNull(options);
        await WinMLProviders.RegisterInstalledAsync().ConfigureAwait(false);
        return await Task.Run(() => new WinMLInferenceSession(modelPath, options, policy)).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads a model from <paramref name="modelPath"/> on the provider named
    /// <paramref name="providerName"/>, on the adapter <paramref name="adapterLuid"/> when given.
    /// </summary>
    /// <param name="modelPath">The model file.</param>
    /// <param name="providerName">A provider's name, as <see cref="WinMLProviders.Available"/> lists it.</param>
    /// <param name="adapterLuid">The DXGI adapter LUID, to choose among a provider's GPUs.</param>
    /// <param name="logger">Reserved.</param>
    /// <exception cref="ArgumentException">No registered provider has that name, or none is on that adapter.</exception>
    public static WinMLInferenceSession OnProvider(
        string modelPath, string providerName, ulong? adapterLuid = null, ILogger<WinMLInferenceSession>? logger = null) =>
        OnProvider(modelPath, DefaultOptions, providerName, adapterLuid, logger);

    /// <inheritdoc cref="OnProvider(string, string, ulong?, ILogger{WinMLInferenceSession}?)" />
    /// <param name="modelPath">The model file.</param>
    /// <param name="options">How the model loads, and the provider's own options.</param>
    /// <param name="providerName">A provider's name, as <see cref="WinMLProviders.Available"/> lists it.</param>
    /// <param name="adapterLuid">The DXGI adapter LUID, to choose among a provider's GPUs.</param>
    /// <param name="logger">Reserved.</param>
    public static WinMLInferenceSession OnProvider(
        string modelPath,
        WinMLInferenceSessionOptions options,
        string providerName,
        ulong? adapterLuid = null,
        ILogger<WinMLInferenceSession>? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        ArgumentNullException.ThrowIfNull(options);
        _ = logger;
        // CA2000: the session owns the options once built; the catch disposes them if it is not.
#pragma warning disable CA2000
        var sessionOptions = ProviderOptions(providerName, adapterLuid, options);
        try
        {
            return new WinMLInferenceSession(modelPath, sessionOptions);
        }
        catch
        {
            sessionOptions.Dispose();
            throw;
        }
#pragma warning restore CA2000
    }

    /// <inheritdoc cref="OnProvider(string, string, ulong?, ILogger{WinMLInferenceSession}?)" />
    /// <param name="modelBytes">The model.</param>
    /// <param name="providerName">A provider's name, as <see cref="WinMLProviders.Available"/> lists it.</param>
    /// <param name="adapterLuid">The DXGI adapter LUID, to choose among a provider's GPUs.</param>
    /// <param name="logger">Reserved.</param>
    public static WinMLInferenceSession OnProvider(
        byte[] modelBytes, string providerName, ulong? adapterLuid = null, ILogger<WinMLInferenceSession>? logger = null) =>
        OnProvider(modelBytes, DefaultOptions, providerName, adapterLuid, logger);

    /// <inheritdoc cref="OnProvider(string, WinMLInferenceSessionOptions, string, ulong?, ILogger{WinMLInferenceSession}?)" />
    /// <param name="modelBytes">The model.</param>
    /// <param name="options">How the model loads, and the provider's own options.</param>
    /// <param name="providerName">A provider's name, as <see cref="WinMLProviders.Available"/> lists it.</param>
    /// <param name="adapterLuid">The DXGI adapter LUID, to choose among a provider's GPUs.</param>
    /// <param name="logger">Reserved.</param>
    public static WinMLInferenceSession OnProvider(
        byte[] modelBytes,
        WinMLInferenceSessionOptions options,
        string providerName,
        ulong? adapterLuid = null,
        ILogger<WinMLInferenceSession>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(modelBytes);
        ArgumentNullException.ThrowIfNull(options);
        _ = logger;
        // CA2000: the session owns the options once built; the catch disposes them if it is not.
#pragma warning disable CA2000
        var sessionOptions = ProviderOptions(providerName, adapterLuid, options);
        try
        {
            return new WinMLInferenceSession(modelBytes, sessionOptions);
        }
        catch
        {
            sessionOptions.Dispose();
            throw;
        }
#pragma warning restore CA2000
    }

    private static readonly WinMLInferenceSessionOptions DefaultOptions = new();

    internal static SessionOptions PolicyOptions(WinMLDevicePolicy policy, WinMLInferenceSessionOptions sessionOptions)
    {
        ArgumentNullException.ThrowIfNull(sessionOptions);
        if (sessionOptions.ProviderOptions.Count > 0)
        {
            throw new ArgumentException(
                "Provider options need a named provider, and a device policy lets Windows choose it. "
                    + "Open the session with OnProvider to pass them.",
                nameof(sessionOptions));
        }

        var ortPolicy = DevicePolicies.ToOrt(policy);
        WinMLRuntime.EnsureLoaded();
        var options = new SessionOptions();
        try
        {
            options.GraphOptimizationLevel = sessionOptions.OptimizationLevel;
            options.SetEpSelectionPolicy(ortPolicy);
            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }

    internal static SessionOptions ProviderOptions(
        string providerName, ulong? adapterLuid, WinMLInferenceSessionOptions sessionOptions)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerName);
        ArgumentNullException.ThrowIfNull(sessionOptions);
        WinMLRuntime.EnsureLoaded();
        var devices = WinMLProviders.Devices(providerName, adapterLuid);
        if (devices.Count == 0)
        {
            var available = string.Join(", ", WinMLProviders.Available().Select(p =>
                p.AdapterLuid is { } luid ? $"{p.Name} (adapter {luid})" : p.Name));
            throw new ArgumentException(
                $"No registered provider '{providerName}'{(adapterLuid is null ? "" : $" on adapter {adapterLuid}")}. "
                    + $"Available: {available}. Vendor providers appear once WinMLProviders.RegisterInstalledAsync has run.",
                nameof(providerName));
        }

        var options = new SessionOptions();
        try
        {
            options.GraphOptimizationLevel = sessionOptions.OptimizationLevel;
            options.AppendExecutionProvider(
                OrtEnv.Instance(), [devices[0]], new Dictionary<string, string>(sessionOptions.ProviderOptions));
            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }
}
