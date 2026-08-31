# ADR-0003: Audio-Master Synchronization Policy

## Status

Accepted

## Context

Playback synchronization is one of the hardest parts of a media player.

When audio exists, the audio device is usually the most stable continuous time source. Users also perceive audio glitches and drift more harshly than minor video timing adjustments.

FrameFlow needs an initial synchronization policy that is:

- simple
- practical
- consistent with common player behavior
- decoupled from UI/backend specifics

## Decision

FrameFlow will use **audio as the default master clock** when audio is present.

When there is no audio stream, or no usable audio sink, FrameFlow will fall back to a wall-clock-driven video timing model.

Synchronization policy will be centralized in a dedicated sync service or strategy layer rather than spread across decoders, presenters, or UI code.

The playback orchestration layer will own timing policy, including:

- when to delay a frame
- when to drop or skip a late frame
- how to reset timing after seek/pause/resume

## Consequences

### Positive

- synchronization policy is clear and conventional
- timing logic has a single authoritative owner
- later tuning remains localized

### Negative

- the audio sink must expose enough timing behavior to support sync
- fallback behavior must still be carefully designed for video-only playback

## Alternatives Considered

### Video as the master clock

Rejected because audio devices are generally the better continuous reference when audio exists.

### Hybrid or dynamically switching clock in v1

Rejected because it adds complexity too early.

### Audio-master with video-only fallback

Accepted as the simplest strong initial policy.
