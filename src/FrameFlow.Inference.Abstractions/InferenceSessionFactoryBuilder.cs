// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Inference.Core;
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
///         preferred: ExecutionProvider.DirectML,
///         providers: new Dictionary&lt;ExecutionProvider, Func&lt;string, IInferenceSession&gt;&gt;
///         {
///             [ExecutionProvider.DirectML] = path => new DmlInferenceSession(path),
///             [ExecutionProvider.Cpu] = path => new CpuInferenceSession(path),
///         },
///         loggerFactory: sp.GetRequiredService&lt;ILoggerFactory&gt;()));
/// </code>
/// <para>
/// An app references one of <c>FrameFlow.Inference.Dml</c>, <c>.Cuda</c>, <c>.WinML</c> and
/// <c>.Cpu</c>: each carries its own ONNX Runtime native library, and two in one process
/// conflict. Each of them runs <c>CpuInferenceSession</c> too, so the usual registration is the
/// package's GPU provider and <see cref="ExecutionProvider.Cpu"/> behind it.
/// </para>
/// </remarks>
public static class InferenceSessionFactoryBuilder
{
    /// <summary>
    /// Builds a factory that tries <paramref name="preferred"/> first,
    /// then walks <paramref name="fallbackOrder"/>, or by default the other
    /// registered EPs from narrowest to broadest: CUDA, Windows ML,
    /// DirectML, and CPU last, since it is the one that nearly always opens.
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
    /// Defaults to every other EP in <paramref name="providers"/>, narrowest
    /// first and <see cref="ExecutionProvider.Cpu"/> last.
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
    /// Where a provider falls in the default fallback, narrowest first (#431). The enum's
    /// numeric order says nothing: CPU is its first value, and a provider appended later need
    /// not be broader than the ones before it. One this does not name goes before CPU.
    /// </summary>
    private static int FallbackRank(ExecutionProvider provider) =>
        provider switch
        {
            ExecutionProvider.Cuda => 0,
            ExecutionProvider.WindowsML => 1,
            ExecutionProvider.DirectML => 2,
            ExecutionProvider.Cpu => int.MaxValue,
            _ => int.MaxValue - 1,
        };

    /// <summary>
    /// Computes the actual EP probe order: preferred first, then the
    /// supplied fallback (filtered to registered providers and
    /// deduplicated), or the default fallback: the other registered EPs
    /// by <see cref="FallbackRank"/>.
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

        foreach (var provider in registeredSet.OrderBy(FallbackRank).ThenBy(p => (int)p))
        {
            if (seen.Add(provider))
                chain.Add(provider);
        }
        return chain;
    }

    /// <summary>
    /// Caches the first successful provider; subsequent <c>Open</c>
    /// calls use the cached provider directly, and one that fails there
    /// walks the rest of the chain. What a failure does depends on its
    /// <see cref="ConstructFailure"/> kind: out of memory throws, device loss and
    /// an unavailable provider let the provider that opens be cached, and an
    /// unrecognised failure opens the model without changing the cache.
    /// Thread-safe under the "single-reader, possibly racing first Open" pattern.
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
                var failures = new List<Failure>();
                if (_active is ExecutionProvider cached)
                {
                    // Cached path: no re-probe, but a session is still
                    // constructed with the known-good provider. Report the
                    // same Probing → Opening phases so consumers see a
                    // consistent shape on every Open(), warm or cold.
                    progress?.Report(new InferenceSessionProgress(
                        InferenceSessionPhase.ProbingProvider, cached));
                    if (TryConstruct(cached, modelPath, failures) is { } cachedSession)
                        return Opened(cachedSession, cached, progress);
                    ThrowIfOutOfMemory(failures[^1], modelPath);

                    // A provider that opened before can stop opening: after a GPU reset,
                    // DirectML fails for the rest of the process (#431). Walk the chain
                    // without it; the provider that opens replaces it in the cache only when
                    // every failure on the way rules its provider out (#497).
                    _logger.LogWarning(
                        failures[^1].Exception,
                        "Inference factory: execution provider {Provider}, which opened before, "
                            + "failed to open model '{ModelPath}' ({Kind}). Probing the rest of the chain.",
                        cached,
                        modelPath,
                        failures[^1].Kind);
                }

                foreach (var provider in _chain)
                {
                    if (failures.Exists(f => f.Provider == provider))
                        continue;
                    progress?.Report(new InferenceSessionProgress(
                        InferenceSessionPhase.ProbingProvider, provider));
                    if (TryConstruct(provider, modelPath, failures) is not { } session)
                    {
                        ThrowIfOutOfMemory(failures[^1], modelPath);
                        _logger.LogWarning(
                            failures[^1].Exception,
                            "Inference factory: execution provider {Provider} failed to open "
                                + "model '{ModelPath}' ({Kind}): {Message}",
                            provider,
                            modelPath,
                            failures[^1].Kind,
                            failures[^1].Exception.Message);
                        continue;
                    }

                    if (failures.Count == 0)
                    {
                        _active = provider;
                        _logger.LogInformation(
                            "Inference factory using execution provider {Provider}.",
                            provider);
                    }
                    else if (failures.TrueForAll(f => ConstructFailure.RulesOut(f.Kind)))
                    {
                        _active = provider;
                        _logger.LogWarning(
                            "Inference factory fell back to execution provider {Provider} "
                                + "after {FailureCount} earlier provider(s) failed.",
                            provider,
                            failures.Count);
                    }
                    else
                    {
                        // A failure the factory does not recognise may be this model's alone, so
                        // the model opens here and the next one starts where this one did (#497).
                        _logger.LogWarning(
                            "Inference factory opened model '{ModelPath}' on execution provider {Provider} "
                                + "after {FailureCount} earlier provider(s) failed, and keeps {Active} for the "
                                + "next model: a failure it does not recognise does not rule a provider out.",
                            modelPath,
                            provider,
                            failures.Count,
                            _active?.ToString() ?? "no provider");
                    }

                    return Opened(session, provider, progress);
                }

                // Every provider failed. One cached before stays cached: a model that no
                // provider opens says nothing against the provider that last opened one.
                var summary = string.Join("; ",
                    failures.Select(f => $"{f.Provider}: {f.Exception.GetType().Name}: {f.Exception.Message}"));
                throw new InvalidOperationException(
                    $"All execution providers failed to open model '{modelPath}'. Tried: {summary}",
                    new AggregateException(failures.Select(f => f.Exception)));
            }
        }

        /// <summary>
        /// Constructs a session with <paramref name="provider"/>, or records why it failed and
        /// returns null. Only the construct is caught: a failure is the provider's.
        /// </summary>
        private IInferenceSession? TryConstruct(
            ExecutionProvider provider,
            string modelPath,
            List<Failure> failures)
        {
            try
            {
                return _providers[provider](modelPath);
            }
            catch (Exception ex)
            {
                failures.Add(new Failure(provider, ex, ConstructFailure.Classify(ex)));
                return null;
            }
        }

        /// <summary>
        /// Throws for a provider that ran out of memory: the model did not fit, which says nothing
        /// about the next model, so no other provider is tried and the cache stays (#497).
        /// </summary>
        private void ThrowIfOutOfMemory(Failure failure, string modelPath)
        {
            if (failure.Kind != ConstructFailureKind.OutOfMemory)
                return;

            _logger.LogWarning(
                failure.Exception,
                "Inference factory: execution provider {Provider} ran out of memory opening model "
                    + "'{ModelPath}'. No other provider was tried.",
                failure.Provider,
                modelPath);
            throw new ProviderOutOfMemoryException(failure.Provider, modelPath, failure.Exception);
        }

        private readonly record struct Failure(ExecutionProvider Provider, Exception Exception, ConstructFailureKind Kind);

        /// <summary>
        /// Reports <paramref name="session"/> opened and hands it over. A reporter that throws
        /// gets the session disposed, rather than leaked or counted against the provider.
        /// </summary>
        private static IInferenceSession Opened(
            IInferenceSession session,
            ExecutionProvider provider,
            IProgress<InferenceSessionProgress>? progress)
        {
            try
            {
                progress?.Report(new InferenceSessionProgress(InferenceSessionPhase.OpeningSession, provider));
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
    }
}
