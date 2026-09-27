// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// The tests pin the pure constant block and read the tensor back to compare it.
[assembly: InternalsVisibleTo("FrameFlow.Inference.D3D12.Tests")]

// Direct3D 12 exists only on Windows. Code that calls in without a Windows check gets CA1416 when
// it builds.
[assembly: SupportedOSPlatform("windows")]
