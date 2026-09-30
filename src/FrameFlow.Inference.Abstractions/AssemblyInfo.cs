// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

// The image-to-tensor plan and kernel, and the tensor-to-image kernel, are pure. The tests reach
// them directly to pin the plan's arithmetic and to compare each kernel's paths with each other.
[assembly: InternalsVisibleTo("FrameFlow.Inference.Abstractions.Tests")]

// The device-side stage (FrameFlow.Inference.D3D12) computes the same plan and validates the same
// crop and options as the CPU stage, so the two map a tensor back to the frame identically.
[assembly: InternalsVisibleTo("FrameFlow.Inference.D3D12")]
[assembly: InternalsVisibleTo("FrameFlow.Inference.D3D12.Tests")]

// The DirectML handoff tests drive one operator run at a time to compare each result with the
// tensor the stage wrote.
[assembly: InternalsVisibleTo("FrameFlow.Inference.Dml.Tests")]

// The detectors bind a model's fp16 or fp32 tensors as its session declares them and read either
// as floats (#10), with the same element-type rule, rental and half-to-float kernel as the
// inference operator.
[assembly: InternalsVisibleTo("FrameFlow.Yolo")]
[assembly: InternalsVisibleTo("FrameFlow.Face")]
