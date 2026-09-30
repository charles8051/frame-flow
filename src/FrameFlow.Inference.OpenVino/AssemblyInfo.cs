// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// Intel's OpenVINO build of ONNX Runtime ships for Windows x64 only. Code that calls in without a
// Windows check gets CA1416 when it builds.
[assembly: SupportedOSPlatform("windows")]

// The tests pin the mapping from options to the provider's own, which is pure.
[assembly: InternalsVisibleTo("FrameFlow.Inference.OpenVino.Tests")]
