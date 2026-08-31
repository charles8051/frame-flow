// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Inference;

/// <summary>
/// Builder for <see cref="IInferenceSessionFactory"/> instances. The
/// caller registers per-EP construction delegates so this abstraction
/// package doesn't need to reference the concrete EP packages
/// (<c>FrameFlow.Inference.Cuda</c>, <c>FrameFlow.Inference.Dml</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Typical usage</b> (host DI registration):
/// </para>
/// <code>
/// services.AddSingleton&lt;IInferenceSessionFactory&gt;(sp =>
///     InferenceSessionFactoryBuilder.Create(
///         preferred: ExecutionProvider.Cuda,
///         providers: new Dictionary&lt;ExecutionProvider, Func&lt;string, IInferenceSession&gt;&gt;
///         {
///             [ExecutionProvider.Cuda] = path => new CudaInferenceSession(path),
///             [ExecutionProvider.DirectML] = path => new DmlInferenceSession(path),
///         },
///         loggerFactory: sp.GetRequiredService&lt;ILoggerFactory&gt;()));
/// </code>
/// <para>
/// The host application pins which EP packages are referenced (NVIDIA-equipped
/// SKUs add <c>FrameFlow.Inference.Cuda</c>; the rest stick with
/// DirectML); the registered <c>providers</c> dictionary reflects that
/// per-SKU choice. The builder picks among them at runtime.
/// </para>
/// </remarks>
public static class InferenceSessionFactoryBuilder
{
    /// <summary>
    /// Builds a factory that tries <paramref name="preferred"/> first,
    /// then walks <paramref name="fallbackOrder"/> (or the default
    /// chain, which is the other registered EPs in
    /// <see cref="ExecutionProvider"/> declaration order: Cpu first,
    /// DirectML, Cuda last — broadest compatibility first).
    /// </summary>
    /// <param name="preferred">EP attempted first.</param>
    /// <param name="providers">
    /// Map of EP → constructor delegate
    /// (<c>path =&gt; new XInferenceSession(path)</c>). Must contain
    /// <paramref name="preferred"/>.
    /// </param>
    /// <param name="fallbackOrder">
    /// EPs to try after <paramref name="preferred"/> fails, in order.
    /// EPs not present in <paramref name="providers"/> are silently
    /// skipped; the preferred EP is auto-prepended if not already first.
    /// Defaults to every other EP in <paramref name="providers"/> in
    /// <see cref="ExecutionProvider"/> declaration order.
    /// </param>
    /// <param name="loggerFactory">
    /// Optional logger factory. The factory logs the selected EP and
    /// any fallback transitions at <c>Information</c> / <c>Warning</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="providers"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="providers"/> is empty, or <paramref name="preferred"/>
    /// is not a key in <paramref name="providers"/>.
    /// </exception>
    public static IInferenceSessionFactory Create(
        ExecutionProvider preferred,
        IReadOnlyDictionary<ExecutionProvider, Func<string, IInferenceSession>> providers,
        IReadOnlyList<ExecutionProvider>? fallbackOrder = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Count == 0)
        {
            throw new ArgumentException(
                "At least one provider must be registered.",
                nameof(providers));
        }
        if (!providers.ContainsKey(preferred))
        {
            throw new ArgumentException(
                $"Preferred provider '{preferred}' is not registered in providers "
                    + $"(registered: [{string.Join(", ", providers.Keys)}]).",
                nameof(preferred));
        }

        var chain = BuildChain(preferred, providers.Keys, fallbackOrder);
        var logger = (loggerFactory ?? NullLoggerFactory.Instance)
            .CreateLogger("FrameFlow.Inference.InferenceSessionFactory");
        return new LazyResolvingFactory(chain, providers, logger);
    }

    /// <summary>
    /// Computes the actual EP probe order: preferred first, then the
    /// supplied fallback (filtered to registered providers and
    /// deduplicated), or a default fallback (other registered EPs in
    /// enum-declaration order).
    /// </summary>
    private static IReadOnlyList<ExecutionProvider> BuildChain(
        ExecutionProvider preferred,
        IEnumerable<ExecutionProvider> registered,
        IReadOnlyList<ExecutionProvider>? customFallback)
    {
        var registeredSet = new HashSet<ExecutionProvider>(registered);
        var chain = new List<ExecutionProvider> { preferred };
        var seen = new HashSet<ExecutionProvider> { preferred };

        if (customFallback is not null)
        {
            foreach (var provider in customFallback)
            {
                if (!seen.Add(provider)) continue;
                if (!registeredSet.Contains(provider)) continue;
                chain.Add(provider);
            }
            return chain;
        }

        foreach (var provider in registeredSet.OrderBy(p => (int)p))
        {
            if (seen.Add(provider))
                chain.Add(provider);
        }
        return chain;
    }

    /// <summary>
    /// Caches the first successful provider; subsequent <c>Open</c>
    /// calls use the cached provider directly. Thread-safe under the
    /// "single-reader, possibly racing first Open" pattern.
    /// </summary>
    private sealed class LazyResolvingFactory : IInferenceSessionFactory
    {
        private readonly IReadOnlyList<ExecutionProvider> _chain;
        private readonly IReadOnlyDictionary<ExecutionProvider, Func<string, IInferenceSession>> _providers;
        private readonly ILogger _logger;
        private readonly object _gate = new();
        private ExecutionProvider? _active;

        public LazyResolvingFactory(
            IReadOnlyList<ExecutionProvider> chain,
            IReadOnlyDictionary<ExecutionProvider, Func<string, IInferenceSession>> providers,
            ILogger logger)
        {
            _chain = chain;
            _providers = providers;
            _logger = logger;
        }

        public ExecutionProvider? ActiveProvider
        {
            get { lock (_gate) return _active; }
        }

        public IInferenceSession Open(string modelPath) => Open(modelPath, progress: null);

        public IInferenceSession Open(string modelPath, IProgress<InferenceSessionProgress>? progress)
        {
            ArgumentException.ThrowIfNullOrEmpty(modelPath);

            lock (_gate)
            {
                if (_active is ExecutionProvider cached)
                {
                    // Cached path: no re-probe, but a session is still
                    // constructed with the known-good provider. Report the
                    // same Probing → Opening phases so consumers see a
                    // consistent shape on every Open(), warm or cold.
                    progress?.Report(new InferenceSessionProgress(
                        InferenceSessionPhase.ProbingProvider, cached));
                    var cachedSession = _providers[cached](modelPath);
                    progress?.Report(new InferenceSessionProgress(
                        InferenceSessionPhase.OpeningSession, cached));
                    return cachedSession;
                }

                var failures = new List<(ExecutionProvider Provider, Exception Exception)>();
                foreach (var provider in _chain)
                {
                    progress?.Report(new InferenceSessionProgress(
                        InferenceSessionPhase.ProbingProvider, provider));
                    try
                    {
                        var session = _providers[provider](modelPath);
                        _active = provider;
                        progress?.Report(new InferenceSessionProgress(
                            InferenceSessionPhase.OpeningSession, provider));
                        if (failures.Count == 0)
                        {
                            _logger.LogInformation(
                                "Inference factory using execution provider {Provider}.",
                                provider);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Inference factory fell back to execution provider {Provider} "
                                    + "after {FailureCount} earlier provider(s) failed.",
                                provider,
                                failures.Count);
                        }
                        return session;
                    }
                    catch (Exception ex)
                    {
                        failures.Add((provider, ex));
                        _logger.LogWarning(
                            ex,
                            "Inference factory: execution provider {Provider} failed to open "
                                + "model '{ModelPath}': {Message}",
                            provider,
                            modelPath,
                            ex.Message);
                    }
                }

                var summary = string.Join("; ",
                    failures.Select(f => $"{f.Provider}: {f.Exception.GetType().Name}: {f.Exception.Message}"));
                throw new InvalidOperationException(
                    $"All execution providers failed to open model '{modelPath}'. Tried: {summary}",
                    new AggregateException(failures.Select(f => f.Exception)));
            }
        }
    }
}
