// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// What the substrate does when an operator's function throws.
/// </summary>
public enum FailureResponse
{
    /// <summary>Exception propagates upward; the graph faults.</summary>
    Propagate,

    /// <summary>The input that triggered the failure is disposed and the node continues with the next.</summary>
    Discard,
}
