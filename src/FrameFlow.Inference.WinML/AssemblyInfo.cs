// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

// The tests pin the policy mapping and the LUID parsing, which are pure.
[assembly: InternalsVisibleTo("FrameFlow.Inference.WinML.Tests")]
