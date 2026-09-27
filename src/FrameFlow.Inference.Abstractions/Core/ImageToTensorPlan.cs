// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Numerics;

namespace FrameFlow.Inference.Core;

/// <summary>
/// Where each pixel of one <see cref="ImageToTensor"/> call reads the frame: a crop, a tensor size
/// and a fit in, an affine map and a covered region out. Pure.
/// </summary>
/// <remarks>
/// A tensor pixel <c>(dx, dy)</c> is sampled at its centre <c>(px, py) = (dx + ½, dy + ½)</c>, which
/// lands in the frame at <c>x = A·px + B·py + C</c>, <c>y = D·px + E·py + F</c>. A pixel whose centre
/// falls outside <c>[FitLeft, FitRight) × [FitTop, FitBottom)</c> is a letterbox bar. With
/// <see cref="ImageFit.Stretch"/> that region is the whole tensor.
/// </remarks>
internal readonly record struct ImageToTensorPlan(
    double A,
    double B,
    double C,
    double D,
    double E,
    double F,
    double FitLeft,
    double FitTop,
    double FitRight,
    double FitBottom,
    int TensorWidth,
    int TensorHeight)
{
    /// <summary>
    /// True when x depends only on the column and y only on the row. The kernel then samples from
    /// per-column and per-row tables instead of per pixel.
    /// </summary>
    public bool IsAxisAligned => B == 0 && D == 0;

    /// <summary>The mapping in tensor coordinates normalized to 0 to 1.</summary>
    public TensorTransform Transform => new(new Matrix3x2(
        (float)(A * TensorWidth), (float)(D * TensorWidth),
        (float)(B * TensorHeight), (float)(E * TensorHeight),
        (float)C, (float)F));

    public static ImageToTensorPlan Create(RotatedRect crop, int tensorWidth, int tensorHeight, ImageFit fit)
    {
        double width = crop.Width;
        double height = crop.Height;

        // Frame pixels per tensor pixel, along the crop's own axes.
        double scaleX = width / tensorWidth;
        double scaleY = height / tensorHeight;
        double fitLeft = 0, fitTop = 0, fitRight = tensorWidth, fitBottom = tensorHeight;
        if (fit == ImageFit.Letterbox)
        {
            scaleX = scaleY = Math.Max(scaleX, scaleY);
            double fittedWidth = width / scaleX;
            double fittedHeight = height / scaleY;
            fitLeft = (tensorWidth - fittedWidth) / 2;
            fitTop = (tensorHeight - fittedHeight) / 2;
            fitRight = fitLeft + fittedWidth;
            fitBottom = fitTop + fittedHeight;
        }

        // sin(0) is exactly 0, so an unrotated crop leaves B and D exactly 0 and takes the table path.
        var (sin, cos) = Math.SinCos(crop.Rotation);
        double a = scaleX * cos;
        double b = -scaleY * sin;
        double d = scaleX * sin;
        double e = scaleY * cos;

        // The tensor's centre lands on the crop's centre.
        double halfWidth = tensorWidth / 2.0;
        double halfHeight = tensorHeight / 2.0;
        double c = crop.CenterX - halfWidth * a - halfHeight * b;
        double f = crop.CenterY - halfWidth * d - halfHeight * e;

        return new ImageToTensorPlan(
            a, b, c, d, e, f, fitLeft, fitTop, fitRight, fitBottom, tensorWidth, tensorHeight);
    }

    /// <summary>The frame x the centre of tensor pixel <c>(dx, dy)</c> lands on.</summary>
    public double SourceX(int dx, int dy) => A * (dx + 0.5) + B * (dy + 0.5) + C;

    /// <summary>The frame y the centre of tensor pixel <c>(dx, dy)</c> lands on.</summary>
    public double SourceY(int dx, int dy) => D * (dx + 0.5) + E * (dy + 0.5) + F;

    /// <summary>Whether column <paramref name="dx"/> is inside the fitted crop.</summary>
    public bool CoversColumn(int dx) => FitLeft <= dx + 0.5 && dx + 0.5 < FitRight;

    /// <summary>Whether row <paramref name="dy"/> is inside the fitted crop.</summary>
    public bool CoversRow(int dy) => FitTop <= dy + 0.5 && dy + 0.5 < FitBottom;
}
