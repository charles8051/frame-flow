// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Media.Core;

namespace FrameFlow.Playback;

/// <summary>
/// The fault a pacer raises when its video sink has thrown on too many calls in a row (#703).
/// A playback error built from it is <see cref="ErrorCategory.Sink"/>, whatever the sink threw
/// (#836). That exception is the inner one.
/// </summary>
internal sealed class SinkFailedException(string message, Exception innerException)
    : InvalidOperationException(message, innerException),
        IFailureOrigin
{
    /// <inheritdoc/>
    public ErrorCategory Category => ErrorCategory.Sink;
}
