// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// DirectML and its ONNX Runtime package exist only on Windows. Code that calls in without a Windows
// check gets CA1416 when it builds.
[assembly: SupportedOSPlatform("windows")]

// The tests pin which C API version the device-bound session asks a runtime for.
[assembly: InternalsVisibleTo("FrameFlow.Inference.Dml.Tests")]
