# FrameFlow Agent Responsibility Matrix

This document defines how implementation work should be divided across coding agents.

This document is the ownership and review model itself, and is self-contained: every role, boundary, and review gate below is stated here rather than by reference.

Executable personas for these roles are **local tooling, not part of the repository**. They live under an ignored `.claude/agents/` in a working checkout, one markdown file per agent, and are absent from a fresh clone. Nothing here depends on them — the matrix describes who owns what and which changes need a second pair of eyes, which holds whether the roles are executed by an agent or by a person. If you do provision personas locally, keep them consistent with this document; this document wins.

The guiding rule is:

**assign agents by subsystem boundary, not by file count**

That keeps context stable, reduces overlapping edits, and makes integration failures easier to diagnose.

## Core agent roles

### 1. Native / Bootstrap Agent

Owns:

- `FrameFlow.Native`
- FFmpeg binary resolution
- runtime identifier logic
- binding initialization
- native diagnostics and probe behavior

Primary concerns:

- correctness of native environment setup
- platform-specific loading behavior
- clear diagnostics when FFmpeg is unavailable

### 2. Media / Contracts Agent

Owns:

- `FrameFlow.Media`
- source abstractions
- metadata models
- decoded frame/audio payload contracts
- playback state enums and shared domain contracts

Primary concerns:

- API clarity
- serialization/config friendliness where appropriate
- keeping contracts UI- and backend-neutral

### 3. Decoding Agent

Owns:

- `FrameFlow.Decoding`
- demux session implementation
- stream selection
- audio/video decoder implementation
- conversion and resampling logic

Primary concerns:

- FFmpeg interop correctness
- timestamp normalization
- resource ownership and disposal

### 4. Playback / Orchestration Agent

Owns:

- `FrameFlow.Playback`
- playback session lifecycle
- queues and backpressure
- clocks and synchronization
- seek/pause/resume/stop orchestration
- builders and playback options

Primary concerns:

- explicit state modeling
- low coupling across subsystems
- operational correctness across long-running playback

### 5. Adapter Agent

Owns:

- `FrameFlow.Audio.OpenAL`
- `FrameFlow.Avalonia`
- `FrameFlow.Avalonia.Windows` — the zero-copy Windows presenter (ADR-0061, ADR-0063, ADR-0064)
- `FrameFlow.Sdl`
- future presenters and output adapters

Primary concerns:

- keeping platform/UI dependencies at the edges
- preserving the purity of the headless core
- thread-safe presentation/output integration

### 6. Integration / Review Agent

Owns:

- cross-project coherence
- DI and options registration consistency
- package/build shape
- regression review
- API review across boundaries

Primary concerns:

- making sure the architecture is still being followed
- spotting coupling introduced across boundaries
- verifying multi-project changes build and fit together

### 7. API Steward Agent

Owns:

- top-level consumer-facing API cleanliness
- naming and discoverability
- builder and fluent configuration ergonomics
- DI and options usability from a consumer perspective
- review of public surface area changes

Primary concerns:

- does the API feel clean and obvious to use?
- are defaults sensible?
- do registration and configuration patterns feel natural in .NET?
- are public contracts easy to understand without deep internal knowledge?

This agent should usually act as a review and approval agent rather than the primary implementer.

### 8. Architecture Hawk Agent

Owns:

- architectural integrity across subsystem boundaries
- enforcement of composition-over-inheritance
- lifecycle separation from processing logic
- modularity, reusability, and anti-coupling review
- detection of structural drift from `ARCHITECTURE.md`
- future-proofing review of extension seams, versioning pressure, and irreversible structural choices

Primary concerns:

- are boundaries still clean?
- is a convenience change leaking concerns across layers?
- is resource ownership remaining explicit?
- is the design getting harder to change?
- are likely future use cases blocked by today's shape?
- are we preserving optionality without inventing speculative complexity?

This agent should usually act as a structural review gate rather than a primary implementer.

### 9. Master Coordinator

Owns:

- phase sequencing
- work decomposition
- agent assignment
- conflict resolution between subsystem owners
- pacing and integration timing
- deciding when APIs are stable enough to build on

Primary concerns:

- are we moving in the right order?
- are the right agents involved at the right time?
- is scope expanding too quickly?
- are review gates being respected?

The Master Coordinator should be lightweight and decision-oriented, not a catch-all implementation agent.

**Issue log gate:** Before starting any new phase or approving a phase transition, the Master Coordinator must review `docs/issues/README.md` and ensure all open issues whose phase gate matches the current or upcoming phase are resolved or explicitly deferred with documented rationale. No phase begins with unresolved must-fix issues for that gate.

### 10. FFmpeg Expert Agent

Owns:

- FFmpeg API correctness review across all phases that touch native interop
- version-awareness and deprecation guidance for FFmpeg library APIs
- codec and format coverage review
- test corpus generation review
- edge case awareness for FFmpeg behavioral quirks and platform differences

Primary concerns:

- is the FFmpeg API usage correct for the target version range?
- are deprecated APIs being used when stable replacements exist?
- is the resource lifecycle (alloc/unref/free) correct for every FFmpeg object?
- do timebase conversions use `av_rescale_q` or equivalent?
- are library dependency chains complete for native binary distribution?

This agent should act as a domain-specific review gate, not a primary implementer.

### 11. Testing / Validation Agent

Holds **veto authority** over phase completion — see `phases/SUB_PHASE_GATES.md` Gate 3.

Owns:

- test strategy execution and comprehensive coverage enforcement
- validation harnesses
- fake clocks, sinks, presenters, and controllable doubles
- sample media corpus usage and validation workflows
- regression coverage for stable behavior
- **veto authority over phase completion** — no phase ships without this agent's sign-off

Primary concerns:

- is every documented behavior covered by automated tests?
- is every error path, state transition, and disposal scenario tested?
- do the current seams support headless verification?
- are harnesses revealing failures clearly enough to diagnose them?
- are we accumulating test debt that will slow future phases?

This agent holds a hard gate on phase completion. All implementation agents must cooperate with testability seam requests and coverage demands. Schedule pressure does not override testing requirements.

### 12. Documentation / Samples Agent

Owns:

- `README.md` and consumer-facing documentation
- usage examples under `examples/`
- doc consistency across `ARCHITECTURE.md`, the phase docs, and the ADRs
- XML doc coverage on the public surface

Primary concerns:

- do the examples compile against the API surface that exists today?
- does the documentation describe what is implemented, not what was planned?
- when a refactor improves a call site, has it been propagated to every example?

Every example must ship a `Properties/launchSettings.json` that passes `--log-file` a bare filename, which the example resolves to `<repo>/logs/<short-name>.log` via `FrameFlow.Examples.Common.ExampleLogPaths.Resolve`. A bare filename keeps the file free of any one machine's workspace path. A new example without this wiring is a bug, not a polish item.

## Ownership model

Each work item should have:

- **one primary owner**
- **zero or more supporting agents**
- **one integration reviewer** when the change crosses project boundaries
- **API Steward review** for top-level public API changes
- **Architecture Hawk review** for cross-boundary or lifecycle-affecting changes

The Master Coordinator should decide ownership and review requirements for any task that spans multiple subsystem boundaries or phases.

Avoid split primary ownership of the same subsystem in the same phase unless the work is carefully partitioned.

## Phase-by-phase ownership matrix

All thirteen phases are complete (see `docs/ROADMAP.md`). This table is kept because it records which agent owned which delivery and which gates applied, and because the same ownership pattern applies to comparable new work. It is not a schedule.


| Phase | Primary Owner | Supporting Agents | Integration Reviewer | Additional Gates | Notes |
|---|---|---|---|---|---|
| 00 API and Foundation Design | API Steward Agent | Media / Contracts Agent, Playback / Orchestration Agent | Integration / Review Agent | Architecture Hawk | This phase defines usage samples, public shape, lifecycle contracts, and skeleton signatures before lower-level implementation proceeds. |
| 00b DI Registration and Host Integration | API Steward Agent | Media / Contracts Agent, Playback / Orchestration Agent | Integration / Review Agent | Architecture Hawk | Bridges API design into working DI registration surface. |
| 00c Test Corpus Generation | Testing / Validation Agent | Native / Bootstrap Agent | Integration / Review Agent | FFmpeg Expert, Architecture Hawk | FFmpeg Expert reviews generation commands for correctness. |
| 01 Bootstrap and Probe | Native / Bootstrap Agent | Media / Contracts Agent | Integration / Review Agent | API Steward, Architecture Hawk, FFmpeg Expert | Media agent helps shape bootstrap result contracts and option surfaces. FFmpeg Expert reviews library loading and version detection. |
| 01a Runtime Download Script | Native / Bootstrap Agent | Integration / Review Agent | Integration / Review Agent | FFmpeg Expert, Architecture Hawk | FFmpeg Expert reviews library dependency chain and version compatibility. |
| 02 Demux and Metadata | Decoding Agent | Media / Contracts Agent | Integration / Review Agent | API Steward, FFmpeg Expert | Contracts agent stabilizes `MediaInfo`, source abstractions, and stream models. FFmpeg Expert reviews demux API usage. |
| 03 Video Decode | Decoding Agent | Media / Contracts Agent | Playback / Orchestration Agent | Architecture Hawk, FFmpeg Expert | Playback agent reviews timestamp/frame contracts. FFmpeg Expert reviews decode API and pixel format conversion. |
| 04 Audio Decode | Decoding Agent | Media / Contracts Agent, Adapter Agent | Playback / Orchestration Agent | Architecture Hawk, FFmpeg Expert | Adapter agent helps ensure PCM output shape fits the first sink backend. FFmpeg Expert reviews audio decode and resampler APIs. |
| 05 Playback Session | Playback / Orchestration Agent | Decoding Agent, Media / Contracts Agent | Integration / Review Agent | API Steward, Architecture Hawk, FFmpeg Expert | This is the first major composition phase. FFmpeg Expert reviews flush/EOF semantics at demux/decode boundary. |
| 06 Sync and Seeking | Playback / Orchestration Agent | Decoding Agent, Adapter Agent | Integration / Review Agent | Architecture Hawk, FFmpeg Expert | FFmpeg Expert reviews seek flag selection and flush-after-seek protocol. |
| 07 Avalonia Adapter | Adapter Agent | Playback / Orchestration Agent, Media / Contracts Agent | Integration / Review Agent | API Steward, Architecture Hawk | Playback agent protects the core boundary from UI leakage. |
| 08 Polish and Diagnostics | Testing / Validation Agent or Integration / Review Agent | All agents as needed | Integration / Review Agent | API Steward, Architecture Hawk | If the work centers on harnesses, corpus validation, and regression shape, testing leads. If it centers on repo-wide fit-and-finish, integration leads. |
| 09 Acceleration and Presenters | Adapter Agent or Decoding Agent (depending on spike) | Playback / Orchestration Agent, Native / Bootstrap Agent | Integration / Review Agent | API Steward, Architecture Hawk, FFmpeg Expert | FFmpeg Expert reviews hardware acceleration context setup and format negotiation. |

The Master Coordinator sits above this matrix and is responsible for:

- assigning the primary owner for each concrete task
- deciding which additional gates apply
- sequencing phase and sub-phase work
- resolving ownership ambiguity

## Cross-cutting responsibility map

Some concerns do not belong to just one subsystem.

| Concern | Default Owner | Supporting Agents |
|---|---|---|
| Native resource ownership | Decoding Agent | Native / Bootstrap Agent |
| FFmpeg binary setup and probing | Native / Bootstrap Agent | Integration / Review Agent |
| Shared API contracts | Media / Contracts Agent | Integration / Review Agent |
| Consumer-facing API usability | API Steward Agent | Media / Contracts Agent, Playback / Orchestration Agent |
| Architectural boundary enforcement | Architecture Hawk Agent | Integration / Review Agent |
| Future-proofing and extension seam review | Architecture Hawk Agent | API Steward Agent, Integration / Review Agent |
| Test strategy execution and validation harnesses | Testing / Validation Agent | Architecture Hawk Agent, Integration / Review Agent |
| FFmpeg API correctness and version awareness | FFmpeg Expert Agent | Native / Bootstrap Agent, Decoding Agent |
| Test corpus generation review | FFmpeg Expert Agent | Testing / Validation Agent |
| Phase sequencing and assignment | Master Coordinator | Integration / Review Agent |
| Playback state machine | Playback / Orchestration Agent | Media / Contracts Agent |
| A/V sync policy | Playback / Orchestration Agent | Decoding Agent, Adapter Agent |
| DI and options registration | Playback / Orchestration Agent | Integration / Review Agent |
| Packaging and build layout | Integration / Review Agent | Native / Bootstrap Agent |
| UI-thread marshalling | Adapter Agent | Playback / Orchestration Agent |
| Logging and diagnostics shape | Integration / Review Agent | all subsystem owners |
| Consumer-facing documentation and examples | Documentation / Samples Agent | API Steward Agent, Architecture Hawk Agent |

## Practical handoff rules

Use these rules to keep agent work from colliding.

### Rule 1: contracts first

If a phase changes shared contracts, the Media / Contracts Agent should define or review those contracts before downstream agents implement against them.

### Rule 2: the owner writes, the reviewer protects boundaries

The primary owner should do the implementation work.

The integration reviewer should specifically look for:

- architecture leakage across layers
- hidden ownership problems
- over-broad abstractions
- missing validation or disposal behavior

The API Steward should review:

- naming
- public surface clarity
- fluent setup ergonomics
- DI/options usability

The Architecture Hawk should review:

- boundary leakage
- lifecycle mixing
- unnecessary inheritance
- structural coupling
- whether likely future extension points still have clean seams
- whether v1 scope is staying intentionally narrow instead of becoming a speculative plugin system

This includes watching for consumer scenarios such as overlays, annotations, alternate presenters, diagnostics listeners, source providers, and processing hooks that may matter later even if they are not implemented in v1.

The Testing / Validation Agent owns and has **veto authority** over:

- test harness design
- fake and deterministic test doubles
- sample corpus validation workflows
- comprehensive coverage enforcement — every behavior, error path, state transition, and disposal scenario
- phase sign-off: no phase transitions to "Done" without the testing agent's explicit approval

All implementation agents must cooperate with testability seam requests from the Testing / Validation Agent. These requests are blocking, not advisory.

### Rule 3: adapters do not define the core

Adapter agents can request changes to shared contracts, but they should not quietly reshape the playback core around UI/backend convenience without explicit review.

### Rule 4: native work must stay isolated

Any change that introduces or modifies FFmpeg pointer ownership, allocation, or cleanup should receive review from the Decoding Agent or Native / Bootstrap Agent even if another agent made the change.

### Rule 5: orchestration owns policy

Decode agents should expose data and capabilities.

Playback agents should own:

- timing policy
- buffering policy
- state transitions
- seek/pause/resume behavior

### Rule 6: one subsystem, one active owner at a time

Avoid having multiple agents concurrently editing the same subsystem unless the work is partitioned into clearly separate files with agreed boundaries.

### Rule 7: no top-level API changes without API Steward review

Any change that affects:

- builders
- public options
- public interfaces
- DI extension methods
- common usage patterns

should be reviewed by the API Steward Agent before it is treated as stable.

### Rule 8: no cross-boundary structural changes without Architecture Hawk review

Any change that:

- crosses project boundaries
- alters lifecycle ownership
- moves responsibilities between layers
- introduces new abstraction seams

should be reviewed by the Architecture Hawk Agent.

### Rule 9: the Master Coordinator decides pacing

When there is pressure to move quickly, the Master Coordinator should be the role that decides whether:

- the API is stable enough
- a phase is mature enough to build on
- a spike should become a real implementation
- additional review gates can be deferred

## Suggested execution pattern by task size

### Small tasks

Use one primary owner and no supporting agents.

Examples:

- add a metadata field
- refine a small option type
- improve a single adapter method

### Medium tasks

Use one primary owner and one reviewer.

Examples:

- implement demux session
- add a playback clock
- add Avalonia frame presentation

Add API Steward review if the task alters top-level public usage.

Add Architecture Hawk review if the task alters ownership or boundaries.

### Large tasks

Break the work into contract, implementation, and integration tracks.

Recommended pattern:

1. Media / Contracts Agent defines or stabilizes shared contracts
2. Primary subsystem agent implements
3. Integration / Review Agent validates cross-boundary fit
4. API Steward validates consumer-facing usability where applicable
5. Architecture Hawk validates structural cleanliness
6. Adapter or supporting agent performs edge integration if needed

## Recommended default assignments

If you want a simple default rule set:

- `FrameFlow.Native` -> Native / Bootstrap Agent
- `FrameFlow.Media` -> Media / Contracts Agent
- `FrameFlow.Graph` -> Media / Contracts Agent (frame contracts, ADR-0030) with Playback / Orchestration Agent review on operator and termination semantics
- `FrameFlow.Decoding`, `FrameFlow.Encoding` -> Decoding Agent
- `FrameFlow.Audio` -> Decoding Agent (resampling) with Adapter Agent review
- `FrameFlow.Playback`, `FrameFlow.Player` -> Playback / Orchestration Agent
- `FrameFlow.Camera`, `FrameFlow.Video` -> Decoding Agent for capture/format work, Adapter Agent for device integration
- `FrameFlow.Audio.OpenAL`, `FrameFlow.Avalonia`, `FrameFlow.Avalonia.Windows`, `FrameFlow.Sdl` -> Adapter Agent
- `FrameFlow.Inference.Abstractions` -> Media / Contracts Agent
- `FrameFlow.Inference.Ort`, `FrameFlow.Inference.Cuda`, `FrameFlow.Inference.Dml` -> Adapter Agent (these are backend adapters; native bootstrap concerns go to Native / Bootstrap Agent)
- `FrameFlow.Yolo`, `FrameFlow.Face`, `FrameFlow.Whisper`, `FrameFlow.MotionClip` -> Playback / Orchestration Agent for pipeline shape, Adapter Agent for model and device details
- `examples/`, `README.md`, doc consistency -> Documentation / Samples Agent
- any multi-project change -> Integration / Review Agent reviews
- any top-level API change -> API Steward Agent reviews
- any cross-boundary structural change -> Architecture Hawk Agent reviews
- any FFmpeg API or version concern -> FFmpeg Expert Agent reviews
- any multi-phase or ambiguous task -> Master Coordinator assigns ownership

## When to override the matrix

Override the default ownership when:

- a spike is explicitly exploratory
- one agent has already built the context for a complex thread of work
- a decision spans multiple phases and needs coordinated authorship

When you override the default, document:

- temporary owner
- reason for override
- expected review path

That keeps exceptions from becoming accidental process drift.

## Standard sub-phase gates

Every phase must pass through standard review gates before completion. See `phases/SUB_PHASE_GATES.md` for the full specification.

Summary:

1. **Gate 1 — Architectural Scrutiny** (all phases): Architecture Hawk reviews structural integrity
2. **Gate 2 — FFmpeg Domain Scrutiny** (phases 00c, 01, 01a, 02, 03, 04, 05, 06, 09): FFmpeg Expert Agent reviews API correctness
3. **Gate 3 — Testing Review and Implementation** (all phases): Testing / Validation Agent designs and implements test suite

Gates 1 and 2 may run in parallel. Gate 3 always runs last.
