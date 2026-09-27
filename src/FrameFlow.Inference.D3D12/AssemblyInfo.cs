// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

// The tests pin the pure constant block and read the tensor back to compare it.
[assembly: InternalsVisibleTo("FrameFlow.Inference.D3D12.Tests")]
