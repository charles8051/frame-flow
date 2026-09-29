// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace FrameFlow.Inference;

/// <summary>
/// A model's output tensors by output name, owned together. It is the outputs dictionary
/// <see cref="IInferenceSession.Run(IReadOnlyDictionary{string, ICpuTensor}, IReadOnlyDictionary{string, ICpuTensor})"/>
/// takes, and <see cref="Dispose"/> disposes every tensor in it once.
/// </summary>
/// <typeparam name="TTensor">The tensor type the indexer returns.</typeparam>
/// <remarks>
/// To keep a tensor after <see cref="Dispose"/>, take a reference with <see cref="ITensor.AddRef"/>
/// first and dispose that reference when done with it. After <see cref="Dispose"/>, every member
/// but <see cref="Dispose"/> throws <see cref="ObjectDisposedException"/>.
/// </remarks>
public sealed class SessionOutputs<TTensor> : IReadOnlyDictionary<string, ICpuTensor>, IDisposable
    where TTensor : class, ICpuTensor
{
    private readonly Dictionary<string, TTensor> _tensors;
    private bool _disposed;

    /// <summary>
    /// Takes ownership of <paramref name="tensors"/>, keyed by output name. When the constructor
    /// throws, the caller still owns them.
    /// </summary>
    /// <exception cref="ArgumentException">Two tensors have the same name, or a tensor is null.</exception>
    public SessionOutputs(IEnumerable<KeyValuePair<string, TTensor>> tensors)
    {
        ArgumentNullException.ThrowIfNull(tensors);
        _tensors = new Dictionary<string, TTensor>(StringComparer.Ordinal);
        foreach (var (name, tensor) in tensors)
        {
            if (tensor is null)
                throw new ArgumentException($"The tensor for output '{name}' is null.", nameof(tensors));
            if (!_tensors.TryAdd(name, tensor))
                throw new ArgumentException($"Output '{name}' appears twice.", nameof(tensors));
        }
    }

    /// <summary>The tensor for output <paramref name="name"/>.</summary>
    /// <exception cref="KeyNotFoundException">No output has that name.</exception>
    public TTensor this[string name]
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _tensors[name];
        }
    }

    /// <inheritdoc />
    public int Count
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _tensors.Count;
        }
    }

    /// <inheritdoc />
    public IEnumerable<string> Keys
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _tensors.Keys;
        }
    }

    /// <summary>The tensors, in the order of <see cref="Keys"/>.</summary>
    public IEnumerable<TTensor> Values
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _tensors.Values;
        }
    }

    ICpuTensor IReadOnlyDictionary<string, ICpuTensor>.this[string key] => this[key];

    IEnumerable<ICpuTensor> IReadOnlyDictionary<string, ICpuTensor>.Values => Values;

    /// <inheritdoc />
    public bool ContainsKey(string key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _tensors.ContainsKey(key);
    }

    /// <summary>The tensor for output <paramref name="name"/>, if there is one.</summary>
    public bool TryGetValue(string name, [MaybeNullWhen(false)] out TTensor tensor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _tensors.TryGetValue(name, out tensor);
    }

    bool IReadOnlyDictionary<string, ICpuTensor>.TryGetValue(string key, [MaybeNullWhen(false)] out ICpuTensor value)
    {
        bool found = TryGetValue(key, out var tensor);
        value = tensor;
        return found;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, ICpuTensor>> GetEnumerator()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Enumerate();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Disposes every tensor once. A second call does nothing. When a tensor's dispose throws, the
    /// rest are still disposed and the failures are thrown together afterwards.
    /// </summary>
    /// <exception cref="AggregateException">One or more tensors threw on dispose.</exception>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        List<Exception>? failures = null;
        foreach (var tensor in _tensors.Values)
        {
            try
            {
                tensor.Dispose();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
            throw new AggregateException("Disposing a session output failed.", failures);
    }

    private IEnumerator<KeyValuePair<string, ICpuTensor>> Enumerate()
    {
        foreach (var (name, tensor) in _tensors)
            yield return new KeyValuePair<string, ICpuTensor>(name, tensor);
    }
}
