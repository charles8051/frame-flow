// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Inference;

/// <summary>Where a model's input was prepared.</summary>
public enum InferencePath
{
    /// <summary>On the CPU, by <see cref="ImageToTensor"/>, and uploaded by the session.</summary>
    Host,

    /// <summary>On the GPU, by an <see cref="IDeviceImageToTensor"/>, and bound in place.</summary>
    Device,
}

/// <summary>
/// One model run's result, stamped with the frame it came from. It holds no frame and no native
/// memory, so it keeps no decode surface alive; <see cref="AddRef"/> returns itself and
/// <see cref="Dispose"/> does nothing.
/// </summary>
/// <typeparam name="TResult">What the model produces.</typeparam>
public sealed class InferenceResult<TResult> : IRefCounted, IFrame
{
    /// <summary>A result for the frame at <paramref name="timestamp"/>.</summary>
    public InferenceResult(TResult result, TimeSpan timestamp, int width, int height, InferencePath path)
    {
        Result = result;
        Timestamp = timestamp;
        Width = width;
        Height = height;
        Path = path;
    }

    /// <summary>What the model produced.</summary>
    public TResult Result { get; }

    /// <summary>The presentation timestamp of the frame the result is for.</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>The frame's width in pixels.</summary>
    public int Width { get; }

    /// <summary>The frame's height in pixels.</summary>
    public int Height { get; }

    /// <summary>Where the input was prepared.</summary>
    public InferencePath Path { get; }

    /// <summary>
    /// The timeline of the frame the result is for (ADR-0084): set from the frame by the node that
    /// ran the model, <see cref="TimelineId.None"/> when the result was built without one.
    /// </summary>
    /// <remarks>
    /// A result can arrive before its frame is presented, so a consumer matches it against what is
    /// shown through a <see cref="Graph.Core.PresentedTimeline"/>.
    /// </remarks>
    public TimelineId Timeline { get; init; }

    /// <inheritdoc />
    public IRefCounted AddRef() => this;

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
