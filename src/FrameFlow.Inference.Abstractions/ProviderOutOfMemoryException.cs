// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>
/// Thrown by <see cref="IInferenceSessionFactory.Open(string, IProgress{InferenceSessionProgress}?)"/>
/// when a provider ran out of memory opening a model: the model did not fit alongside what else held
/// the memory then.
/// </summary>
/// <remarks>
/// <para>
/// The factory tries no other provider for that model and keeps its
/// <see cref="IInferenceSessionFactory.ActiveProvider"/>, so the next model opens on the provider as
/// before (#497). Freeing memory, such as disposing other sessions, and opening again may succeed;
/// so may a smaller model or a factory that prefers another provider.
/// </para>
/// <para>
/// It derives from <see cref="InvalidOperationException"/>, which <c>Open</c> throws when every
/// provider fails, so a caller that catches that catches this too.
/// </para>
/// </remarks>
public sealed class ProviderOutOfMemoryException : InvalidOperationException
{
    /// <summary>Creates the exception for <paramref name="provider"/> failing to open <paramref name="modelPath"/>.</summary>
    /// <param name="provider">The provider that ran out of memory.</param>
    /// <param name="modelPath">The model it was opening.</param>
    /// <param name="innerException">The provider's own exception.</param>
    public ProviderOutOfMemoryException(ExecutionProvider provider, string modelPath, Exception innerException)
        : base(
            $"Execution provider {provider} ran out of memory opening model '{modelPath}': the model did not fit. "
                + "No other provider was tried.",
            innerException)
    {
        Provider = provider;
        ModelPath = modelPath;
    }

    /// <summary>The provider that ran out of memory.</summary>
    public ExecutionProvider Provider { get; }

    /// <summary>The model it was opening.</summary>
    public string ModelPath { get; }
}
