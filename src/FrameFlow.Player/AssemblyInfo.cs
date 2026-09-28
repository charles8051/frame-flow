// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FrameFlow.Player.Tests")]

// A pass on the GPU route, gated on the hardware the D3D12 and DirectML tests probe for (#277).
[assembly: InternalsVisibleTo("FrameFlow.Inference.Dml.Tests")]
