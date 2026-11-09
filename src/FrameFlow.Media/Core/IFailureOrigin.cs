// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media.Core;

/// <summary>
/// An exception thrown where the failing part is known, which says what kind of failure it is
/// (#836, #837). <see cref="MediaErrorClassification.Categorize(Exception?, ErrorCategory)"/>
/// takes its <see cref="Category"/> and does not look at the exceptions inside it.
/// </summary>
/// <remarks>
/// A caller's video sink that throws a device-loss <c>COMException</c> is wrapped in one of these
/// with <see cref="ErrorCategory.Sink"/>. The loss is the sink's own device, not the decode
/// device, so the HRESULT inside it does not make the failure <see cref="ErrorCategory.DeviceLost"/>.
/// </remarks>
internal interface IFailureOrigin
{
    /// <summary>The category of the failure.</summary>
    ErrorCategory Category { get; }
}
