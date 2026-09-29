using System.Runtime.InteropServices;
using FrameFlow.Graph;
using FrameFlow.Inference;
using Xunit;

namespace FrameFlow.Face.Tests;

/// <summary>A model output that is not a finite number is never a face (#498).</summary>
public sealed class NonFiniteOutputTests
{
    private static readonly BlazeFaceModelDescriptor D = BlazeFaceModelDescriptor.Front128;
    private static readonly FaceRoi Roi = new(0, 0, 128, 128);

    [Fact]
    public void FiniteBoxes_WithNaNScores_DecodeToNothing()
    {
        var boxes = Boxes();
        var scores = new float[D.ScoreElementCount];
        Array.Fill(scores, float.NaN);

        Assert.Empty(new BlazeFacePostprocessor(D).Decode(boxes, scores, Roi));
    }

    [Theory]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void AnInfiniteScore_IsNotAFace_WhateverTheMinimum(float infinity)
    {
        // Every other anchor is NaN, which is dropped, so only anchor 0 can pass a minimum of 0.
        var scores = new float[D.ScoreElementCount];
        Array.Fill(scores, float.NaN);
        scores[0] = infinity;

        Assert.Empty(new BlazeFacePostprocessor(D) { MinScore = 0f }.Decode(Boxes(), scores, Roi));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(15)]
    public void AConfidentFace_WithANaNInItsBoxOrKeypoints_IsDropped(int column)
    {
        var boxes = Boxes();
        var scores = Scores();
        scores[0] = 100f;
        boxes[column] = float.NaN;

        Assert.Empty(new BlazeFacePostprocessor(D).Decode(boxes, scores, Roi));
    }

    [Fact]
    public void TheSameFace_WithNumbers_IsKept()
    {
        var scores = Scores();
        scores[0] = 100f;

        Assert.Single(new BlazeFacePostprocessor(D).Decode(Boxes(), scores, Roi));
    }

    [Fact]
    public void AModelThatReturnsNaN_FailsToLoad_NamingTheSession()
    {
        var session = new ScriptedSession(float.NaN);

        var error = Assert.Throws<InvalidOperationException>(() => BlazeFaceDetector.Create(session));

        Assert.Contains(nameof(ScriptedSession), error.Message);
        Assert.True(session.Disposed);
    }

    [Fact]
    public void AModelThatReturnsNumbers_Loads()
    {
        using var detector = BlazeFaceDetector.Create(new ScriptedSession(-100f));
    }

    /// <summary>A 16-unit box on every anchor, keypoints at the anchor centre.</summary>
    private static float[] Boxes()
    {
        var boxes = new float[D.BoxElementCount];
        for (int i = 0; i < D.NumBoxes; i++)
        {
            boxes[i * D.NumCoords + 2] = 16f;
            boxes[i * D.NumCoords + 3] = 16f;
        }

        return boxes;
    }

    private static float[] Scores()
    {
        var scores = new float[D.ScoreElementCount];
        Array.Fill(scores, -100f);
        return scores;
    }

    /// <summary>A front-128 model that fills both outputs with one value on every run.</summary>
    private sealed class ScriptedSession(float value) : IInferenceSession
    {
        public bool Disposed { get; private set; }

        public IReadOnlyList<string> InputNames { get; } = ["input"];

        public IReadOnlyList<string> OutputNames { get; } = ["boxes", "scores"];

        public IReadOnlyList<IReadOnlyList<long>> InputShapes { get; } = [[1, 3, 128, 128]];

        public IReadOnlyList<IReadOnlyList<long>> OutputShapes { get; } = [[1, 896, 16], [1, 896, 1]];

        public void Run(IReadOnlyDictionary<string, ICpuTensor> inputs, IReadOnlyDictionary<string, ICpuTensor> outputs)
        {
            foreach (var output in outputs.Values)
                MemoryMarshal.Cast<byte, float>(MemoryMarshal.AsMemory(output.Bytes).Span).Fill(value);
        }

        public void Dispose() => Disposed = true;
    }
}
