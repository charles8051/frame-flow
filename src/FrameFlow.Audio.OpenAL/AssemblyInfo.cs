// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

// Exposes SharedOpenAlContext's internal structural diagnostics (device-open
// count, lease refcount, context-live flag) to the test assembly so the
// shared-device/context contract (ADR-0058) can be asserted deterministically.
[assembly: InternalsVisibleTo("FrameFlow.Audio.Tests")]

// The shared test-support library. It owns FakeOpenAlDevice and the factory that
// wires it to a real OpenAlAudioSink, so a suite with no internals access of its
// own can still cover the sink against a device it controls (#146).
[assembly: InternalsVisibleTo("FrameFlow.Audio.TestKit")]
