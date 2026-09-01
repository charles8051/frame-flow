// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

// The native bootstrappers reach BundleExtractionHelper, RuntimeIdentifierHelper, and
// HomebrewLayout. ADR-0019 forbids FrameFlow.Sdl referencing FrameFlow.Native, so the
// helpers both need live here.
[assembly: InternalsVisibleTo("FrameFlow.Sdl")]
[assembly: InternalsVisibleTo("FrameFlow.Native")]

// AsyncManualResetEvent and AsyncAutoResetEvent. Generic concurrency plumbing rather than
// media API, so they stay internal and are shared this way instead of being published.
[assembly: InternalsVisibleTo("FrameFlow.Playback")]
[assembly: InternalsVisibleTo("FrameFlow.Audio.OpenAL")]

[assembly: InternalsVisibleTo("FrameFlow.Media.Tests")]
