// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

// The image-to-tensor plan and kernel are pure. The tests reach them directly to pin the plan's
// arithmetic and to compare the kernel's paths with each other.
[assembly: InternalsVisibleTo("FrameFlow.Inference.Abstractions.Tests")]
