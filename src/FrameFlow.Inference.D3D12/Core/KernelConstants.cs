// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using FrameFlow.Inference.Core;

namespace FrameFlow.Inference.D3D12.Core;

/// <summary>
/// The shader's root constants for one write: the plan's map and covered region, the tensor's
/// shape, and how samples become tensor values. Pure: a plan and options in, 40 values out.
/// </summary>
/// <remarks>
/// The field order is the shader's <c>cbuffer</c> order, and HLSL packs a <c>cbuffer</c> into
/// 16-byte registers without splitting a vector across two. So each run of four fields below is
/// one register, and the three colour vectors start on register boundaries (bytes 80, 96, 112).
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct KernelConstants
{
    /// <summary>The number of 32-bit values, as the root signature declares them.</summary>
    public const int Count = 40;

    // Frame x = A·px + B·py + C and frame y = D·px + E·py + F for the tensor pixel centre (px, py).
    public float A, B, C, D;
    public float E, F, FitLeft, FitTop;
    public float FitRight, FitBottom;
    public uint TensorWidth, TensorHeight;
    public uint FrameWidth, FrameHeight, Bilinear, Nhwc;

    // Where each colour goes: the plane in NCHW, the position within a pixel in NHWC.
    public uint RedIndex, GreenIndex, BlueIndex, Unused0;

    // value = sample · Scale + Offset, with the sample on 0 to 255, per colour.
    public float ScaleRed, ScaleGreen, ScaleBlue, Unused1;
    public float OffsetRed, OffsetGreen, OffsetBlue, Unused2;

    // A letterbox bar's tensor value, per colour.
    public float PadRed, PadGreen, PadBlue, Unused3;

    // Y' = (Y - YOffset) · YScale and C' = (C - COffset) · CScale, on 0 to 1 samples.
    public float YOffset, YScale, COffset, CScale;

    // R = Y' + RedFromCr·Cr', G = Y' - GreenFromCb·Cb' - GreenFromCr·Cr', B = Y' + BlueFromCb·Cb'.
    public float RedFromCr, GreenFromCb, GreenFromCr, BlueFromCb;

    public static KernelConstants Create(
        in ImageToTensorPlan plan,
        ImageToTensorOptions options,
        YuvMatrix matrix,
        YuvRange range,
        int frameWidth,
        int frameHeight)
    {
        var (kr, kb) = matrix switch
        {
            YuvMatrix.Bt601 => (0.299, 0.114),
            YuvMatrix.Bt709 => (0.2126, 0.0722),
            _ => throw new ArgumentOutOfRangeException(nameof(matrix), matrix, "Undefined YUV matrix."),
        };
        double kg = 1 - kr - kb;
        bool limited = range switch
        {
            YuvRange.Limited => true,
            YuvRange.Full => false,
            _ => throw new ArgumentOutOfRangeException(nameof(range), range, "Undefined YUV range."),
        };

        var normalization = options.Normalization;
        float pad = options.PadValue;
        bool rgb = options.ChannelOrder == TensorChannelOrder.Rgb;

        return new KernelConstants
        {
            A = (float)plan.A,
            B = (float)plan.B,
            C = (float)plan.C,
            D = (float)plan.D,
            E = (float)plan.E,
            F = (float)plan.F,
            FitLeft = (float)plan.FitLeft,
            FitTop = (float)plan.FitTop,
            FitRight = (float)plan.FitRight,
            FitBottom = (float)plan.FitBottom,
            TensorWidth = (uint)plan.TensorWidth,
            TensorHeight = (uint)plan.TensorHeight,
            FrameWidth = (uint)frameWidth,
            FrameHeight = (uint)frameHeight,
            Bilinear = options.Sampling == ImageSampling.Bilinear ? 1u : 0u,
            Nhwc = options.Layout == TensorLayout.Nhwc ? 1u : 0u,
            RedIndex = rgb ? 0u : 2u,
            GreenIndex = 1,
            BlueIndex = rgb ? 2u : 0u,
            ScaleRed = normalization.Red.Scale,
            ScaleGreen = normalization.Green.Scale,
            ScaleBlue = normalization.Blue.Scale,
            OffsetRed = normalization.Red.Offset,
            OffsetGreen = normalization.Green.Offset,
            OffsetBlue = normalization.Blue.Offset,
            PadRed = pad * normalization.Red.Scale + normalization.Red.Offset,
            PadGreen = pad * normalization.Green.Scale + normalization.Green.Offset,
            PadBlue = pad * normalization.Blue.Scale + normalization.Blue.Offset,
            YOffset = limited ? 16f / 255 : 0f,
            YScale = limited ? 255f / 219 : 1f,
            COffset = 128f / 255,
            CScale = limited ? 255f / 224 : 1f,
            RedFromCr = (float)(2 * (1 - kr)),
            GreenFromCb = (float)(2 * kb * (1 - kb) / kg),
            GreenFromCr = (float)(2 * kr * (1 - kr) / kg),
            BlueFromCb = (float)(2 * (1 - kb)),
        };
    }
}
