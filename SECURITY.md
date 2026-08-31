# Security policy

## Reporting a vulnerability

Report privately through GitHub, not in a public issue:
[**Report a vulnerability**](https://github.com/charles8051/frame-flow/security/advisories/new).

That opens a draft advisory visible only to you and the maintainer. Please include:

- what the flaw is, and which project or file it is in
- how to reproduce it — a failing test or a short repro beats prose
- what an attacker gets out of it

FrameFlow is maintained by one person, so response times are best-effort rather
than contractual. Expect an acknowledgement within a week. If a report is
confirmed, the fix and the advisory go out together, and you get credit in the
advisory unless you ask otherwise.

## Supported versions

FrameFlow is pre-1.0 and its public surface is still free to change. Fixes land on
`main` and ship in the next release. Older tags are not patched.

## Scope

FrameFlow decodes untrusted media. A malformed file that causes a crash, a hang, an
out-of-bounds read, or a leak of process memory through a decoded frame is in
scope, and so is anything reachable from the P/Invoke layer in
`src/FrameFlow.Native/`.

Two things sit outside it:

- **FFmpeg itself.** FrameFlow dynamically links the upstream LGPL shared
  libraries and does not patch them. Report those to the FFmpeg project. If a
  FrameFlow-side call sequence is what makes an upstream flaw reachable, that part
  is ours — send it here.
- **Models fetched at runtime.** The YOLO and Whisper weights are downloaded from
  third-party hosts into a local cache and are not redistributed by this project.

Development scripts under `scripts/` fetch binaries over the network by design.
That is the documented behaviour, not a finding on its own; a flaw in how a fetched
artifact is verified before use is.
