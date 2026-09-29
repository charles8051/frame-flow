// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.D3D12.Core;

/// <summary>
/// The compute shader <see cref="D3D12ImageToTensor"/> dispatches: one thread per tensor pixel.
/// It samples a planar YUV texture (NV12 or P010) where the plan maps the pixel, converts to RGB,
/// normalizes, and writes the tensor. The geometry is the CPU stage's: sample positions at pixel
/// centres, the edge pixel repeated outside the frame or the pad colour there, bars outside the
/// fitted region.
/// </summary>
/// <remarks>
/// Chroma is sampled the way luma is. Nearest takes the chroma sample of the 2x2 block the luma
/// pixel is in. Bilinear interpolates the chroma plane with its samples centred on their blocks.
/// The <c>cbuffer</c> matches <see cref="KernelConstants"/> field for field.
/// </remarks>
internal static class ImageToTensorShader
{
    public const string EntryPoint = "main";

    /// <summary>Shader model 5.1, which carries the root signature in the bytecode.</summary>
    public const string Profile = "cs_5_1";

    /// <summary>Threads per group on each axis; a dispatch covers the tensor in 16x16 tiles.</summary>
    public const int GroupSize = 16;

    public const string Source = """
        #define RS "RootConstants(num32BitConstants=40, b0), " \
                   "DescriptorTable(SRV(t0, numDescriptors=2)), " \
                   "UAV(u0)"

        cbuffer Constants : register(b0)
        {
            float A, B, C, D;
            float E, F, FitLeft, FitTop;
            float FitRight, FitBottom;
            uint TensorWidth, TensorHeight;
            uint FrameWidth, FrameHeight, Bilinear, Nhwc;
            uint RedIndex, GreenIndex, BlueIndex, PadOutside;
            float4 Scale;
            float4 Offset;
            float4 PadColour;
            float YOffset, YScale, COffset, CScale;
            float RedFromCr, GreenFromCb, GreenFromCr, BlueFromCb;
        };

        Texture2D<float> Luma : register(t0);
        Texture2D<float2> Chroma : register(t1);
        RWStructuredBuffer<float> Output : register(u0);

        // Pixel i covers [i, i + 1): the pixel a position falls in, the edge pixel outside.
        int2 Nearest(float2 position, int2 last)
        {
            return clamp(int2(floor(position)), int2(0, 0), last);
        }

        float LoadLuma(int2 p) { return Luma.Load(int3(p, 0)); }
        float2 LoadChroma(int2 p) { return Chroma.Load(int3(p, 0)); }

        // Pixel i's centre is at i + 0.5; the two pixels whose centres straddle the position.
        float SampleLuma(float2 position, int2 last)
        {
            if (Bilinear == 0)
                return LoadLuma(Nearest(position, last));
            float2 g = position - 0.5;
            float2 f = floor(g);
            float2 w = g - f;
            int2 p0 = clamp(int2(f), int2(0, 0), last);
            int2 p1 = clamp(int2(f) + 1, int2(0, 0), last);
            float top = LoadLuma(p0) + (LoadLuma(int2(p1.x, p0.y)) - LoadLuma(p0)) * w.x;
            float bottom = LoadLuma(int2(p0.x, p1.y)) + (LoadLuma(p1) - LoadLuma(int2(p0.x, p1.y))) * w.x;
            return top + (bottom - top) * w.y;
        }

        float2 SampleChroma(float2 position, int2 last)
        {
            if (Bilinear == 0)
                return LoadChroma(clamp(Nearest(position, last * 2 + 1) / 2, int2(0, 0), last));
            float2 g = position / 2 - 0.5;
            float2 f = floor(g);
            float2 w = g - f;
            int2 p0 = clamp(int2(f), int2(0, 0), last);
            int2 p1 = clamp(int2(f) + 1, int2(0, 0), last);
            float2 top = LoadChroma(p0) + (LoadChroma(int2(p1.x, p0.y)) - LoadChroma(p0)) * w.x;
            float2 bottom = LoadChroma(int2(p0.x, p1.y)) + (LoadChroma(p1) - LoadChroma(int2(p0.x, p1.y))) * w.x;
            return top + (bottom - top) * w.y;
        }

        [RootSignature(RS)]
        [numthreads(16, 16, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= TensorWidth || id.y >= TensorHeight)
                return;

            float px = id.x + 0.5;
            float py = id.y + 0.5;
            float3 value;
            float2 position = float2(A * px + B * py + C, D * px + E * py + F);
            bool outsideFrame = position.x < 0 || position.x >= float(FrameWidth)
                || position.y < 0 || position.y >= float(FrameHeight);
            if (px < FitLeft || px >= FitRight || py < FitTop || py >= FitBottom
                || (PadOutside != 0 && outsideFrame))
            {
                value = PadColour.rgb;
            }
            else
            {
                int2 lumaLast = int2(FrameWidth - 1, FrameHeight - 1);
                int2 chromaLast = int2((FrameWidth + 1) / 2 - 1, (FrameHeight + 1) / 2 - 1);
                float y = (SampleLuma(position, lumaLast) - YOffset) * YScale;
                float2 c = (SampleChroma(position, chromaLast) - COffset) * CScale;
                float3 rgb = saturate(float3(
                    y + RedFromCr * c.y,
                    y - GreenFromCb * c.x - GreenFromCr * c.y,
                    y + BlueFromCb * c.x));
                value = rgb * 255.0 * Scale.rgb + Offset.rgb;
            }

            uint pixel = id.y * TensorWidth + id.x;
            uint plane = TensorWidth * TensorHeight;
            if (Nhwc != 0)
            {
                Output[pixel * 3 + RedIndex] = value.r;
                Output[pixel * 3 + GreenIndex] = value.g;
                Output[pixel * 3 + BlueIndex] = value.b;
            }
            else
            {
                Output[RedIndex * plane + pixel] = value.r;
                Output[GreenIndex * plane + pixel] = value.g;
                Output[BlueIndex * plane + pixel] = value.b;
            }
        }
        """;
}
