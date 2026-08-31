// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

// Expose the pure host->ORT staging transforms on OrtInferenceSessionBase
// (ToLongShape / MapDType / ValidateNames / ConvertDims) to the inference
// test project so the staging contract both EP wrappers share through this
// base can be unit-tested directly — shape conversion, dtype mapping, and
// name-mismatch validation — without standing up a real ORT session
// (which needs GPU/DML natives + a model). The transforms are pure; this
// only widens their visibility for the test, not the public surface.
[assembly: InternalsVisibleTo("FrameFlow.Inference.Abstractions.Tests")]
