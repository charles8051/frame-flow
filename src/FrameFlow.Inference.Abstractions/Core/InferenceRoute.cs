// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.Core;

/// <summary>How an inference operator prepares one frame's input.</summary>
internal enum InferenceRoute
{
    /// <summary>On the CPU, by <see cref="ImageToTensor"/>.</summary>
    Host,

    /// <summary>On the GPU, by the device stage, bound in place.</summary>
    Device,

    /// <summary>Neither: a GPU frame with no device stage that reads it and binds.</summary>
    None,
}

/// <summary>Chooses a frame's route. Pure.</summary>
internal static class InferenceRoutes
{
    /// <summary>
    /// The device route when the stage can write the frame and the session can bind the stage's
    /// tensor; otherwise the host route for a frame in CPU memory; otherwise none.
    /// </summary>
    public static InferenceRoute Choose(bool frameInCpuMemory, bool stageCanWrite, bool sessionCanBind) =>
        stageCanWrite && sessionCanBind ? InferenceRoute.Device
        : frameInCpuMemory ? InferenceRoute.Host
        : InferenceRoute.None;
}
