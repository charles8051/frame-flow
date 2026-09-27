// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>
/// How <see cref="ImageToTensor"/> turns an 8-bit colour sample into a tensor value:
/// <c>value = sample · Scale + Offset</c>, with a scale and offset per colour channel.
/// </summary>
/// <remarks>
/// A bilinear sample is interpolated before it is normalized, so the sample can be fractional.
/// </remarks>
public readonly record struct TensorNormalization
{
    private TensorNormalization((float, float) red, (float, float) green, (float, float) blue)
    {
        Red = red;
        Green = green;
        Blue = blue;
    }

    /// <summary>The red channel's scale and offset.</summary>
    public (float Scale, float Offset) Red { get; }

    /// <summary>The green channel's scale and offset.</summary>
    public (float Scale, float Offset) Green { get; }

    /// <summary>The blue channel's scale and offset.</summary>
    public (float Scale, float Offset) Blue { get; }

    /// <summary>Samples 0 to 255 map to 0 to 1.</summary>
    public static TensorNormalization ZeroToOne => Range(0f, 1f);

    /// <summary>Samples 0 to 255 map to -1 to 1.</summary>
    public static TensorNormalization MinusOneToOne => Range(-1f, 1f);

    /// <summary>Samples 0 to 255 map linearly to <paramref name="min"/> to <paramref name="max"/>.</summary>
    public static TensorNormalization Range(float min, float max)
    {
        if (!float.IsFinite(min) || !float.IsFinite(max))
        {
            throw new ArgumentException($"The range must be finite; got [{min}, {max}].");
        }

        var channel = ((float)(((double)max - min) / 255.0), min);
        return new TensorNormalization(channel, channel, channel);
    }

    /// <summary>
    /// <c>(sample / 255 − mean) / std</c> per channel, the form models trained on ImageNet
    /// statistics publish. The mean and standard deviation are on the 0 to 1 scale.
    /// </summary>
    public static TensorNormalization MeanStd(
        (float R, float G, float B) mean,
        (float R, float G, float B) std)
    {
        return new TensorNormalization(
            Channel(mean.R, std.R, nameof(std)),
            Channel(mean.G, std.G, nameof(std)),
            Channel(mean.B, std.B, nameof(std)));

        static (float, float) Channel(float mean, float std, string paramName)
        {
            if (!float.IsFinite(mean) || !float.IsFinite(std) || std == 0f)
            {
                throw new ArgumentException(
                    $"Each mean must be finite and each std finite and non-zero; got mean {mean}, std {std}.",
                    paramName);
            }

            return ((float)(1.0 / (255.0 * std)), (float)(-(double)mean / std));
        }
    }
}
