// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

// ADR-0037 / §3.3: expose the pure swscale reconfigure predicate (SwsPlan,
// SwsConfigKey) to the Video test project so the reuse-vs-rebuild decision can
// be unit-tested with no FFmpeg loaded — the predicate carries zero native
// references, so it does not need a real SwsContext to exercise.
[assembly: InternalsVisibleTo("FrameFlow.Video.Tests")]
