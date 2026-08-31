// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

// Expose the pure CUDA path-resolution decision (CudaPathResolution and
// its CudaPathVerdict / CudaPathOutcome) to the Cuda test project so the
// bootstrap verdict — which root, which RID shape, found-or-missing — can
// be unit-tested over a synthetic file table with no real CUDA / cuDNN
// install. The decision is internal because it is a shell-internal seam,
// not part of the public bootstrap surface (CudaBootstrapper /
// CudaDllResolver).
[assembly: InternalsVisibleTo("FrameFlow.Inference.Cuda.Tests")]
