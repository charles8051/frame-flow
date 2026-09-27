// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Inference.Core;
using FrameFlow.Media;

namespace FrameFlow.Inference;

/// <summary>
/// Graph operators that run an <see cref="IImageModel{TResult}"/> on video frames: the input
/// prepared where the pixels are, the model run, and a result stamped with the frame's timestamp.
/// </summary>
/// <example>
/// A player that runs YOLO beside the picture and draws the result for the frame on screen:
/// <code>
/// using var detector = await Yolov8Detector.CreateAsync(factory);
/// using var onScreen = new PresentedResults&lt;IReadOnlyList&lt;Detection&gt;&gt;(videoView);
/// onScreen.Presented += (_, result) => overlay.Draw(result.Result);
///
/// await using var player = await FrameFlowPlayer.Create()
///     .WithVideoSink(videoView)
///     .ConfigureVideo(chain => chain.Infer("yolo", detector, onScreen.Post))
///     .BuildPlayerAsync();
/// </code>
/// </example>
public static class InferenceOperators
{
    /// <summary>
    /// Runs <paramref name="model"/> on a branch off <paramref name="chain"/> and hands each result
    /// to <paramref name="onResult"/>. Returns <paramref name="chain"/>, still open, for the
    /// configurator to return.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The branch keeps only the newest frame while a run is in progress, so a model slower than
    /// the video drops frames rather than delaying the picture. A branch is not paced: results
    /// arrive when the model finishes, which is before their frame is on screen. Match them to the
    /// presented frame with <see cref="PresentedResults{TResult}"/>.
    /// </para>
    /// <para>
    /// <paramref name="onResult"/> runs on the branch's thread; hand the result on rather than
    /// doing slow work there.
    /// </para>
    /// </remarks>
    /// <param name="chain">The video chain a configurator receives.</param>
    /// <param name="id">A node id, unique in the graph; the result sink is <c>{id}-results</c>.</param>
    /// <param name="model">The model. Not disposed by the graph.</param>
    /// <param name="onResult">Receives each result.</param>
    /// <param name="deviceStage">
    /// Prepares the input on the GPU for the frames it can read. Frames it cannot read take the CPU
    /// path when they are in CPU memory. Not disposed by the graph.
    /// </param>
    public static GraphChain<IVideoFrame> Infer<TResult>(
        this GraphChain<IVideoFrame> chain,
        string id,
        IImageModel<TResult> model,
        Action<InferenceResult<TResult>> onResult,
        IDeviceImageToTensor? deviceStage = null)
    {
        ArgumentNullException.ThrowIfNull(onResult);

        chain.Branch(EdgeOptions.LatestWins(1))
            .Then(Infer(id, model, deviceStage))
            .To(new SinkNode<InferenceResult<TResult>>(
                $"{id}-results",
                (result, _) =>
                {
                    onResult(result);
                    return ValueTask.CompletedTask;
                },
                holding: FrameHolding.InFlight));
        return chain;
    }

    /// <summary>
    /// A node that runs <paramref name="model"/> on each frame it receives and emits the result.
    /// For wiring by hand; <see cref="Infer{TResult}(GraphChain{IVideoFrame}, string, IImageModel{TResult}, Action{InferenceResult{TResult}}, IDeviceImageToTensor?)"/>
    /// wires it on a branch.
    /// </summary>
    /// <remarks>
    /// A frame goes the device path when <paramref name="deviceStage"/> can write it and the
    /// model's session is an <see cref="IDeviceInputSession"/> that can bind the stage's tensor. A
    /// frame in CPU memory otherwise goes the CPU path. A GPU frame with neither fails the node:
    /// put <c>ToCpu</c> before it, or give it a stage that reads the frame.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="deviceStage"/> writes a tensor other than the model's input.</exception>
    public static OperatorNode<IVideoFrame, InferenceResult<TResult>> Infer<TResult>(
        string id,
        IImageModel<TResult> model,
        IDeviceImageToTensor? deviceStage = null,
        FailureResponse onError = FailureResponse.Propagate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(model);
        if (deviceStage is not null && deviceStage.Options != model.Input)
        {
            throw new ArgumentException(
                $"The device stage writes {deviceStage.Options} and the model takes {model.Input}.",
                nameof(deviceStage));
        }

        var runner = new InferenceRunner<TResult>(model, deviceStage);
        return new OperatorNode<IVideoFrame, InferenceResult<TResult>>(
            id,
            (frame, _) => ValueTask.FromResult<InferenceResult<TResult>?>(runner.Run(frame)),
            onError,
            // The frame in the call, and what the stage keeps until the GPU has read it. The
            // output carries no frame.
            FrameHolding.AtMost(Math.Max(1, deviceStage?.MaxHeldFrames ?? 1), forwardsStorage: false));
    }
}

/// <summary>
/// One operator's run: the route, the input written on the CPU or the GPU, the session, and the
/// model's decode. Holds the host input and the outputs between runs.
/// </summary>
internal sealed class InferenceRunner<TResult>(IImageModel<TResult> model, IDeviceImageToTensor? stage)
{
    private readonly CpuTensorPool _pool = new();
    private CpuTensor<float>? _input;
    private Dictionary<string, ICpuTensor>? _outputs;

    public InferenceResult<TResult> Run(IVideoFrame frame)
    {
        var crop = model.CropFor(frame);
        bool stageCanWrite = stage?.CanWrite(frame) == true;
        var deviceSession = model.Session as IDeviceInputSession;
        bool sessionCanBind = stageCanWrite && deviceSession?.CanBind(stage!.DeviceTensor) == true;
        var route = InferenceRoutes.Choose(frame.MemoryDomain == FrameMemoryDomain.Cpu, stageCanWrite, sessionCanBind);

        var outputs = Outputs();
        TensorTransform transform;
        InferencePath path;
        switch (route)
        {
            case InferenceRoute.Device:
                transform = stage!.Write(frame, crop);
                deviceSession!.Run(new Dictionary<string, DeviceTensor> { [model.InputName] = stage.DeviceTensor }, outputs);
                path = InferencePath.Device;
                break;
            case InferenceRoute.Host:
                var input = Input();
                transform = ImageToTensor.Write(frame, crop, model.Input, input.Span);
                model.Session.Run(new Dictionary<string, ICpuTensor> { [model.InputName] = input }, outputs);
                path = InferencePath.Host;
                break;
            default:
                throw new NotSupportedException(
                    $"A {frame.MemoryDomain} frame has no way to the model: no device stage reads it and binds to "
                        + "the session. Put ToCpu before the node, or give it a device stage for this frame's API.");
        }

        return new InferenceResult<TResult>(model.Decode(outputs, transform, frame), frame.Timestamp, frame.Width, frame.Height, path);
    }

    private CpuTensor<float> Input() =>
        _input ??= _pool.Rent<float>(model.Input.Layout == TensorLayout.Nhwc
            ? new TensorShape(1, model.Input.Height, model.Input.Width, 3)
            : new TensorShape(1, 3, model.Input.Height, model.Input.Width));

    private Dictionary<string, ICpuTensor> Outputs()
    {
        if (_outputs is not null)
            return _outputs;

        var session = model.Session;
        var outputs = new Dictionary<string, ICpuTensor>(session.OutputNames.Count);
        for (int i = 0; i < session.OutputNames.Count; i++)
        {
            var dims = session.OutputShapes[i];
            if (dims.Any(d => d < 0))
            {
                throw new NotSupportedException(
                    $"Output '{session.OutputNames[i]}' has a dynamic dimension [{string.Join(", ", dims)}]; the operator "
                        + "allocates outputs from the model's static shapes.");
            }

            outputs[session.OutputNames[i]] = _pool.Rent<float>(new TensorShape(dims.Select(d => checked((int)d)).ToArray()));
        }

        return _outputs = outputs;
    }
}
