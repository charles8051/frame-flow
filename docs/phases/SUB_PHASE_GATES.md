# Standard Sub-Phase Gates

Every phase in FrameFlow must pass through these standard review gates before it can be considered complete. These gates are sub-phases that run after the primary implementation work of each phase.

## Gate 1 — Architectural Scrutiny

**Applies to:** all phases

**Owner:** Architecture Hawk Agent

**Supporting:** Integration / Review Agent, API Steward Agent (for phases with public API changes)

### Purpose

Verify that the phase's implementation preserves architectural integrity, respects subsystem boundaries, and does not introduce structural drift from `ARCHITECTURE.md`.

### Review checklist

- [ ] Subsystem boundaries remain clean — no leakage of concerns across layers
- [ ] Composition over inheritance is maintained
- [ ] Lifecycle ownership is explicit and correct
- [ ] Resource ownership follows ADR-0005 rules
- [ ] No hidden coupling introduced between subsystems
- [ ] Extension seams from ADR-0006 are preserved — future customization paths are not blocked
- [ ] Threading and concurrency model follows ADR-0009
- [ ] Cancellation token propagation follows ADR-0013
- [ ] The phase's deliverables do not make the design harder to change
- [ ] Naming and project placement are consistent with `ARCHITECTURE.md`

### Exit criteria

The Architecture Hawk Agent signs off that no structural regressions were introduced.

---

## Gate 2 — FFmpeg Domain Scrutiny

**Applies to:** phases that interact with FFmpeg libraries or generate FFmpeg-dependent artifacts

**Owner:** FFmpeg Expert Agent

**Applicable phases:** 00c, 01, 01a, 02, 03, 04, 05, 06, 09

**Not applicable to:** 00, 00b, 07, 08

### Purpose

Verify that all FFmpeg API usage, library references, and generated test media are correct, version-aware, and free of common interop pitfalls.

### Review checklist

- [ ] FFmpeg API calls target the documented minimum version range
- [ ] No use of deprecated APIs when stable replacements exist in the target range
- [ ] Resource lifecycle is correct: every `av_*_alloc` has a matching `av_*_free`, every `AVPacket`/`AVFrame` is unref'd
- [ ] Timebase conversions use `av_rescale_q` or equivalent — no manual arithmetic
- [ ] Error handling distinguishes `EAGAIN`, `EOF`, and real errors
- [ ] Platform-specific library naming and loading is accounted for
- [ ] Test corpus generation commands produce files that exercise real decode paths
- [ ] Library dependency chains are complete (no missing transitive native dependencies)

### Exit criteria

The FFmpeg Expert Agent signs off that FFmpeg usage is correct and version-appropriate.

---

## Gate 3 — Testing Review and Implementation

**Applies to:** all phases

**Owner:** Testing / Validation Agent

**Supporting:** Architecture Hawk Agent (for testability seam review)

**Authority level:** The Testing / Validation Agent holds **veto authority** over phase completion. This gate is a hard block — no phase ships without the testing agent's explicit sign-off. All implementation agents are required to cooperate with testability seam requests and coverage demands from this gate.

### Purpose

Review the phase's deliverables from a testing perspective, identify gaps in validation coverage, and implement a comprehensive, air-tight test suite that proves the phase's behavior exhaustively. Coverage must be comprehensive enough that any agent could safely rewrite the internals and the tests would catch regressions.

### Review checklist

- [ ] **Every** documented behavior has automated test coverage — not just key behaviors, all behaviors
- [ ] **Every** error and failure path is tested, not just happy paths
- [ ] **Every** state transition is tested, including illegal transitions that must be rejected
- [ ] Deterministic seams exist where timing and state behavior matter
- [ ] Fake/mock doubles are available for all external dependencies (native binaries, audio sinks, presenters)
- [ ] **All** edge cases identified in the phase doc are covered by tests
- [ ] Resource disposal is validated under normal exit, exceptional exit, and cancellation
- [ ] Concurrency and thread safety guarantees are tested where applicable per ADR-0009
- [ ] Tests are fast, deterministic, and do not depend on external network or UI
- [ ] Test corpus files (from Phase 00c) are used where applicable for integration-level validation
- [ ] Test naming is clear and follows the project's conventions
- [ ] Regression coverage is sufficient to catch regressions in any future phase that builds on this one
- [ ] No behavior is left with only manual verification — if it matters, it has an automated test

### Deliverables

- test project(s) or test classes providing **comprehensive** coverage of the phase's deliverables
- any new fake/double infrastructure needed for the phase
- updated test harness if the phase introduces new testable seams
- explicit sign-off from the Testing / Validation Agent

### Exit criteria

The Testing / Validation Agent signs off that:
- the phase has **comprehensive** test coverage — not adequate, comprehensive
- every documented behavior, error path, state transition, and disposal scenario is tested
- the test infrastructure supports future regression detection
- no behavior is left untested
- all testability seam requests have been fulfilled by the owning agents

---

## Sequencing

Within each phase, the standard gate sequence is:

1. **Primary implementation** — the phase's main deliverables are built
2. **Gate 1: Architectural Scrutiny** — structural review before tests are written against potentially wrong boundaries
3. **Gate 2: FFmpeg Domain Scrutiny** — (where applicable) domain review before tests encode incorrect FFmpeg assumptions
4. **Gate 3: Testing Review and Implementation** — test suite is designed and implemented against reviewed, stable deliverables

Gates 1 and 2 may run in parallel when the phase has both architectural and FFmpeg concerns.

Gate 3 always runs last because tests should validate the reviewed implementation, not lock in pre-review mistakes.

## Tracking

Each phase doc should reference this document and note which gates apply. When a gate is completed, the phase doc's status section should record:

```
## Gate status

- [x] Gate 1 — Architectural Scrutiny (completed YYYY-MM-DD)
- [x] Gate 2 — FFmpeg Domain Scrutiny (completed YYYY-MM-DD)  <!-- if applicable -->
- [x] Gate 3 — Testing Review and Implementation (completed YYYY-MM-DD)
```
