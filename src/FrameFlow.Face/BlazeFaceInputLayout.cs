// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Face;

/// <summary>
/// Memory layout of a BlazeFace model's input tensor. Community ONNX
/// exports differ: a direct tf2onnx of the MediaPipe TFLite keeps its
/// native <see cref="Nhwc"/> (<c>[1,H,W,3]</c>), while re-exported /
/// transposed models present <see cref="Nchw"/> (<c>[1,3,H,W]</c>). The
/// preprocessor writes whichever the loaded model declares, so both load
/// without a graph-surgery transpose.
/// </summary>
public enum BlazeFaceInputLayout
{
    /// <summary>Channel-first <c>[1, 3, H, W]</c>.</summary>
    Nchw,

    /// <summary>Channel-last <c>[1, H, W, 3]</c> — the MediaPipe/TFLite-native layout.</summary>
    Nhwc,
}
