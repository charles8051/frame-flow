// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text.RegularExpressions;

namespace FrameFlow.Inference.Core;

/// <summary>Why a provider failed to construct a session, as far as the factory acts on it.</summary>
internal enum ConstructFailureKind
{
    /// <summary>Nothing below matched: a bad model, a bad argument, or a failure not seen before.</summary>
    Unknown,

    /// <summary>An allocation failed. It depends on the model and on what else holds memory.</summary>
    OutOfMemory,

    /// <summary>The GPU was reset or removed. The provider stays broken for the rest of the process.</summary>
    DeviceLost,

    /// <summary>The provider cannot run here: its library, its entry point or an adapter is missing.</summary>
    ProviderUnavailable,
}

/// <summary>
/// Classifies the exception a provider's session constructor threw, from its type, its HRESULT, and
/// the status ONNX Runtime writes into its message, looking through the exceptions it wraps. Pure.
/// </summary>
internal static partial class ConstructFailure
{
    private const int EOutOfMemory = unchecked((int)0x8007000E);

    // DXGI_ERROR_DEVICE_REMOVED, DXGI_ERROR_DEVICE_HUNG, DXGI_ERROR_DEVICE_RESET and
    // D3DDDIERR_DEVICEREMOVED, the codes FrameFlow.Avalonia.Windows treats as device loss.
    private static readonly int[] DeviceLostCodes =
        [unchecked((int)0x887A0005), unchecked((int)0x887A0006), unchecked((int)0x887A0007), unchecked((int)0x88760870)];

    // DXGI_ERROR_NOT_FOUND (no adapter at the index asked for) and DXGI_ERROR_UNSUPPORTED.
    private static readonly int[] UnavailableCodes = [unchecked((int)0x887A0002), unchecked((int)0x887A0004)];

    // CUDA ("CUDA failure 2: out of memory"), cuBLAS and cuDNN (..._STATUS_ALLOC_FAILED), ONNX
    // Runtime's arena ("Failed to allocate memory for requested buffer"), and the Windows text for
    // E_OUTOFMEMORY that follows the HRESULT in a DirectML failure.
    private static readonly string[] OutOfMemoryTexts =
    [
        "out of memory",
        "_STATUS_ALLOC_FAILED",
        "Failed to allocate memory",
        "Not enough memory resources are available",
    ];

    // ONNX Runtime failing to load a provider's library, and Windows ML naming a provider it does
    // not have.
    private static readonly string[] UnavailableTexts = ["LoadLibrary failed", "No registered provider"];

    /// <summary>
    /// The kind of <paramref name="exception"/>. When it and the exceptions it wraps say different
    /// things, device loss wins over out of memory, and both win over an unavailable provider.
    /// </summary>
    public static ConstructFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var kinds = new HashSet<ConstructFailureKind>();
        Collect(exception, kinds);
        return kinds.Contains(ConstructFailureKind.DeviceLost) ? ConstructFailureKind.DeviceLost
            : kinds.Contains(ConstructFailureKind.OutOfMemory) ? ConstructFailureKind.OutOfMemory
            : kinds.Contains(ConstructFailureKind.ProviderUnavailable) ? ConstructFailureKind.ProviderUnavailable
            : ConstructFailureKind.Unknown;
    }

    /// <summary>
    /// Whether a failure of this kind rules the provider out for later models, so the factory may
    /// cache the provider that opened after it. Device loss and a missing provider last for the
    /// process; running out of memory and a failure not recognised may be one model's alone.
    /// </summary>
    public static bool RulesOut(ConstructFailureKind kind) =>
        kind is ConstructFailureKind.DeviceLost or ConstructFailureKind.ProviderUnavailable;

    private static void Collect(Exception exception, HashSet<ConstructFailureKind> kinds)
    {
        kinds.Add(Of(exception));
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                Collect(inner, kinds);
        }
        else if (exception.InnerException is { } wrapped)
        {
            Collect(wrapped, kinds);
        }
    }

    private static ConstructFailureKind Of(Exception exception)
    {
        // The type and the HRESULT say what the exception is, so they are read before its text.
        if (DeviceLostCodes.Contains(exception.HResult))
            return ConstructFailureKind.DeviceLost;
        if (exception is OutOfMemoryException || exception.HResult == EOutOfMemory)
            return ConstructFailureKind.OutOfMemory;
        if (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            || UnavailableCodes.Contains(exception.HResult))
            return ConstructFailureKind.ProviderUnavailable;

        // A quoted span is a path or a name, such as the library ONNX Runtime could not load, and
        // says nothing about why it failed.
        string text = Quoted().Replace(exception.Message, " ");
        var codes = HexCodes(text);
        if (codes.Overlaps(DeviceLostCodes))
            return ConstructFailureKind.DeviceLost;
        if (codes.Contains(EOutOfMemory)
            || OutOfMemoryTexts.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
            return ConstructFailureKind.OutOfMemory;
        if (codes.Overlaps(UnavailableCodes)
            || UnavailableTexts.Any(t => text.Contains(t, StringComparison.Ordinal)))
            return ConstructFailureKind.ProviderUnavailable;

        return ConstructFailureKind.Unknown;
    }

    /// <summary>
    /// Every eight-digit hex number in <paramref name="text"/> that stands alone, as an HRESULT
    /// does in <c>887A0005</c> or <c>(0x887A0005)</c>. A longer run of hex digits, such as an
    /// address, is not one.
    /// </summary>
    private static HashSet<int> HexCodes(string text)
    {
        var codes = new HashSet<int>();
        foreach (Match match in StandaloneHex().Matches(text))
            codes.Add(unchecked((int)Convert.ToUInt32(match.Value, 16)));
        return codes;
    }

    [GeneratedRegex("(?<![0-9A-Fa-f])[0-9A-Fa-f]{8}(?![0-9A-Fa-f])")]
    private static partial Regex StandaloneHex();

    // A double-quoted span, or a single-quoted one whose quotes are not inside a word, so the
    // apostrophe in "doesn't" opens nothing.
    [GeneratedRegex("\"[^\"]*\"|(?<!\\w)'[^']*'(?!\\w)")]
    private static partial Regex Quoted();
}
