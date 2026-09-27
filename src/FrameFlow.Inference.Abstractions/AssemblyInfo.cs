// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

// The image-to-tensor plan and kernel are pure. The tests reach them directly to pin the plan's
// arithmetic and to compare the kernel's paths with each other.
[assembly: InternalsVisibleTo("FrameFlow.Inference.Abstractions.Tests")]

// The device-side stage (FrameFlow.Inference.D3D12) computes the same plan and validates the same
// crop and options as the CPU stage, so the two map a tensor back to the frame identically.
[assembly: InternalsVisibleTo("FrameFlow.Inference.D3D12")]
[assembly: InternalsVisibleTo("FrameFlow.Inference.D3D12.Tests")]
