// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding;

/// <summary>
/// A session-lifetime component whose state must not survive a seek (the position
/// discontinuity). Implementers fold their <em>entire</em> seek-invalidation into a single
/// <see cref="ResetForSeek"/>, so the seek orchestrator resets every participant in one
/// uniform pass — registered once where the component is constructed — instead of
/// hand-listing each component's individual reset steps on the seek path.
/// </summary>
/// <remarks>
/// This mechanises the seek discipline audited in ADR-0048 (which deferred the
/// "ISessionResettable" enforcement to a follow-up) and fulfils the SeekTransition
/// follow-up of ADR-0055. The four documented seek-state-leak bugs all came from a partial
/// checklist on the seek path; a single per-component reset closes that gap by construction.
/// </remarks>
public interface ISeekResettable
{
    /// <summary>
    /// Discards all state tied to the pre-seek timeline. Called on the seek path after the
    /// demuxer has repositioned and the graph/pump is stopped, before fresh tasks start.
    /// Must be idempotent and must not throw for a live (non-disposed) component.
    /// </summary>
    void ResetForSeek();
}
