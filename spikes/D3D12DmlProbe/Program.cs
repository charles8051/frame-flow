using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using D3D12DmlProbe;
using FrameFlow.Decoding;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Yolo;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

// Question: can a D3D12VA-decoded frame reach a DirectML session with no CPU copy? The queue
// waits on the decoder's fence, a compute shader writes the model input into a D3D12 buffer,
// and ORT binds that buffer as the tensor. Each stage prints PASS or FAIL.
//
//   dotnet run --project spikes/D3D12DmlProbe -c Release -- <clip.mp4> [--model yolov8n.onnx] [--frames 60]

string? clip = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a));
if (clip is null)
{
    Console.WriteLine("usage: D3D12DmlProbe <clip> [--model path] [--frames n]");
    return 2;
}

string model = Arg("--model") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameFlow.Yolo", "models", "yolov8n.onnx");
int frameCount = int.Parse(Arg("--frames") ?? "60", CultureInfo.InvariantCulture);

var boot = new FrameFlowBootstrapper(new FrameFlowNativeOptions(), NullLoggerFactory.Instance).Initialize();
if (!boot.IsSuccess)
{
    Console.WriteLine("FFmpeg bootstrap failed.");
    return 1;
}

// A. Decode the same frames twice: on the GPU, and read back to the CPU as the reference.
var gpuFrames = await DecodeAsync(clip, boot.Capabilities!, yieldHardware: true, frameCount);
FrameFlow.Decoding.Diagnostics.DecodeStageMetrics.Reset();
FrameFlow.Decoding.Diagnostics.DecodeStageMetrics.Enabled = true;
var cpuFrames = await DecodeAsync(clip, boot.Capabilities!, yieldHardware: false, frameCount);
FrameFlow.Decoding.Diagnostics.DecodeStageMetrics.Enabled = false;
var readback = FrameFlow.Decoding.Diagnostics.DecodeStageMetrics.Snapshot();
bool decoded = gpuFrames.Count > 0
    && gpuFrames.All(f => f is GpuVideoFrame { Backend: HardwareDecodeBackendKind.D3D12Va })
    && cpuFrames.Count == gpuFrames.Count
    && cpuFrames.All(f => f.Format == PixelFormat.Bgra32);
Report(decoded, $"A  D3D12VA decode: {gpuFrames.Count} GPU frames, {cpuFrames.Count} CPU reference frames");
int misaligned = gpuFrames.Zip(cpuFrames).Count(p => p.First.Pts != p.Second.Pts);
Console.WriteLine($"        frames whose timestamps differ between the two decodes: {misaligned}"
    + (misaligned > 0 ? $" (first: {gpuFrames[0].Pts} vs {cpuFrames[0].Pts}, {gpuFrames[1].Pts} vs {cpuFrames[1].Pts})" : ""));
if (!decoded)
    return 1;

// B. The frame's texture and fence, and whether a device we create is the decoder's.
var first = (GpuVideoFrame)gpuFrames[0];
var handles = D3D12FrameHandles.Read(first);
using var firstTexture = Borrow<ID3D12Resource>(handles.Texture);
using var device = firstTexture.GetDevice<ID3D12Device>();
var textureDesc = firstTexture.Description;
Console.WriteLine(
    $"        texture {textureDesc.Format} {textureDesc.Width}x{textureDesc.Height}, frame {first.Width}x{first.Height}, "
    + $"subresource {handles.Subresource}, fence value {handles.FenceValue}");

using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
long adapterLuid = device.AdapterLuid;
using var adapter = factory.EnumAdapterByLuid<IDXGIAdapter1>(System.Runtime.CompilerServices.Unsafe.As<long, Vortice.Luid>(ref adapterLuid));
D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? ours).CheckError();
using (ours)
{
    Report(ours!.NativePointer == device.NativePointer,
        $"B  D3D12CreateDevice on the decoder's adapter ({adapter.Description1.Description}) returns the decoder's device");
}

// C. Our compute queue, a DirectML device on the decoder's device, and a session on both.
using var queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Compute));
nint dmlDevice = DirectML.CreateDevice(device.NativePointer);
string ortVersion = OrtDml.Initialize();
using var options = new SessionOptions
{
    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
    EnableMemoryPattern = false,
    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
};
OrtDml.AppendDirectML(options, dmlDevice, queue.NativePointer);
using var session = new InferenceSession(model, options);
string inputName = session.InputNames[0];
string outputName = session.OutputNames[0];
int size = session.InputMetadata[inputName].Dimensions[2];
Report(true, $"C  ORT {ortVersion} DirectML session on our device and queue ({Path.GetFileName(model)}, input {size})");

// D. The compute shader against the CPU path's preprocessing, frame by frame.
using var gpu = new GpuPreprocess(device, queue, size);
var cpuPreprocess = new Yolov8Preprocessor(size);
var cpuTensor = new float[cpuPreprocess.InputElementCount];
var diffs = new List<float>();
(float Diff, int Frame, int Index) worstValue = default;
for (int i = 0; i < Math.Min(10, gpuFrames.Count); i++)
{
    RunGpu(gpuFrames[i]);
    float[] gpuTensor = gpu.ReadBack();
    cpuPreprocess.Preprocess(cpuFrames[i], cpuTensor);
    for (int j = 0; j < cpuTensor.Length; j++)
    {
        float d = MathF.Abs(gpuTensor[j] - cpuTensor[j]);
        diffs.Add(d);
        if (d > worstValue.Diff)
            worstValue = (d, i, j);
    }
}
diffs.Sort();
float Pct(double q) => diffs[(int)(q * (diffs.Count - 1))] * 255;
int plane = size * size;
Console.WriteLine(
    $"  D  GPU preprocess vs CPU over 10 frames, |diff| in /255: p50 {Pct(0.5):F1}, p99 {Pct(0.99):F1}, "
    + $"p99.9 {Pct(0.999):F1}, max {worstValue.Diff * 255:F1} "
    + $"(frame {worstValue.Frame}, {"RGB"[worstValue.Index / plane]} at {worstValue.Index % plane % size},{worstValue.Index % plane / size})");

{
    int wi = worstValue.Index % plane, tx = wi % size, ty = wi / size;
    var wf = gpuFrames[worstValue.Frame];
    int sx = Math.Min((int)((tx + 0.5) * wf.Width / size), wf.Width - 1);
    int sy = Math.Min((int)((ty + 0.5) * wf.Height / size), wf.Height - 1);
    var (yy, uu, vv) = Nv12Readback.Sample((GpuVideoFrame)wf, sx, sy);
    var cpuData = cpuFrames[worstValue.Frame].AsCpu()!.Value;
    int o = sy * cpuData.StrideY + sx * 4;
    var bgra = cpuData.PlaneY.Span;
    RunGpu(wf);
    float[] t = gpu.ReadBack();
    Console.WriteLine(
        $"        worst pixel: frame ({sx},{sy}) Y {yy} U {uu} V {vv}; CPU BGRA path R {bgra[o + 2]} G {bgra[o + 1]} B {bgra[o]}; "
        + $"shader R {t[wi] * 255:F0} G {t[plane + wi] * 255:F0} B {t[2 * plane + wi] * 255:F0}");
}

// E. The D3D12 buffer bound to ORT as DirectML memory, on the same session as the CPU tensor.
nint allocation = OrtDml.CreateAllocation(gpu.Tensor.NativePointer);
long[] shape = [1, 3, size, size];
using var dmlMemory = new OrtMemoryInfo("DML", OrtAllocatorType.DeviceAllocator, 0, OrtMemType.Default);
// Disposed before the allocation it wraps is freed, at the end.
var gpuInput = OrtValue.CreateTensorValueWithData(dmlMemory, TensorElementType.Float, shape, allocation, gpu.TensorBytes);
using var cpuInput = OrtValue.CreateTensorValueFromMemory(cpuTensor, shape);
using var runOptions = new RunOptions();
var postprocess = new Yolov8Postprocessor();

// E1. The binding alone: the CPU tensor, uploaded into the buffer, must give the CPU run's outputs.
float bindingWorst = 0;
for (int i = 0; i < Math.Min(10, gpuFrames.Count); i++)
{
    cpuPreprocess.Preprocess(cpuFrames[i], cpuTensor);
    gpu.Upload(cpuTensor);
    float[] bound = Infer(gpuInput);
    float[] reference = Infer(cpuInput);
    for (int j = 0; j < bound.Length; j++)
        bindingWorst = MathF.Max(bindingWorst, MathF.Abs(bound[j] - reference[j]));
}
Report(bindingWorst < 1e-3f, $"E1 same input through the bound buffer and through ORT's upload: max output |diff| {bindingWorst:G3}");

// E2. The shader's tensor against the CPU path's, end to end.
float outputWorst = 0;
double outputSum = 0;
long outputCount = 0;
int agreeing = 0, gpuOnly = 0, cpuOnly = 0;
double worstIou = 1;
for (int i = 0; i < gpuFrames.Count; i++)
{
    float scaleX = (float)gpuFrames[i].Width / size, scaleY = (float)gpuFrames[i].Height / size;
    RunGpu(gpuFrames[i]);
    float[] onGpu = Infer(gpuInput);
    cpuPreprocess.Preprocess(cpuFrames[i], cpuTensor);
    float[] onCpu = Infer(cpuInput);
    for (int j = 0; j < onGpu.Length; j++)
    {
        float d = MathF.Abs(onGpu[j] - onCpu[j]);
        outputWorst = MathF.Max(outputWorst, d);
        outputSum += d;
        outputCount++;
    }

    var unmatched = postprocess.Decode(onCpu, scaleX, scaleY);
    foreach (var d in postprocess.Decode(onGpu, scaleX, scaleY))
    {
        var best = unmatched.Where(c => c.ClassId == d.ClassId).OrderByDescending(c => Iou(c, d)).FirstOrDefault();
        if (best != default && Iou(best, d) > 0.5)
        {
            agreeing++;
            worstIou = Math.Min(worstIou, Iou(best, d));
            unmatched.Remove(best);
        }
        else
        {
            gpuOnly++;
        }
    }
    cpuOnly += unmatched.Count;
}
Console.WriteLine(
    $"  E2 shader tensor vs CPU tensor over {gpuFrames.Count} frames: output |diff| mean {outputSum / outputCount:G3}, "
    + $"max {outputWorst:G3}; detections {agreeing} agree (worst IoU {(agreeing > 0 ? worstIou.ToString("F3") : "n/a")}), "
    + $"{gpuOnly} GPU-only, {cpuOnly} CPU-only");

// F. Timing, per frame. The CPU path's download and NV12 conversion happened in its decoder, timed
// there by DecodeStageMetrics.
var gpuPre = new List<double>();
var gpuRun = new List<double>();
var cpuPre = new List<double>();
var cpuRun = new List<double>();
for (int pass = 0; pass < 3; pass++)
{
    for (int i = 0; i < gpuFrames.Count; i++)
    {
        long t0 = Stopwatch.GetTimestamp();
        RunGpu(gpuFrames[i]);
        long t1 = Stopwatch.GetTimestamp();
        using (session.Run(runOptions, [inputName], [gpuInput], [outputName])) { }
        long t2 = Stopwatch.GetTimestamp();
        cpuPreprocess.Preprocess(cpuFrames[i], cpuTensor);
        long t3 = Stopwatch.GetTimestamp();
        using (session.Run(runOptions, [inputName], [cpuInput], [outputName])) { }
        long t4 = Stopwatch.GetTimestamp();
        if (pass == 0)
            continue;
        gpuPre.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
        gpuRun.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
        cpuPre.Add(Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds);
        cpuRun.Add(Stopwatch.GetElapsedTime(t3, t4).TotalMilliseconds);
    }
}
Console.WriteLine("  F  p50 per frame");
Console.WriteLine($"        GPU path: preprocess {P50(gpuPre):F2} ms (shader, waited on)  run {P50(gpuRun):F2} ms (input already on the GPU)");
Console.WriteLine($"        CPU path: download {readback.HardwareTransfer.P50Ms:F2} ms + NV12 to BGRA {readback.ColorConvert.P50Ms:F2} ms (in the decoder)  "
    + $"preprocess {P50(cpuPre):F2} ms (ImageToTensor)  run {P50(cpuRun):F2} ms (input uploaded by ORT)");

gpuInput.Dispose();
OrtDml.FreeAllocation(allocation);
Marshal.Release(dmlDevice);
foreach (var f in gpuFrames.Concat(cpuFrames))
    f.Dispose();
return 0;

void RunGpu(IVideoFrame frame)
{
    var h = D3D12FrameHandles.Read((GpuVideoFrame)frame);
    using var texture = Borrow<ID3D12Resource>(h.Texture);
    using var fence = Borrow<ID3D12Fence>(h.Fence);
    gpu.Run(texture, fence, h.FenceValue, frame.Width, frame.Height);
}

float[] Infer(OrtValue input)
{
    using var outputs = session.Run(runOptions, [inputName], [input], [outputName]);
    return outputs[0].GetTensorDataAsSpan<float>().ToArray();
}

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static async Task<List<IVideoFrame>> DecodeAsync(string clip, HardwareDecodeCapabilities capabilities, bool yieldHardware, int max)
{
    await using var demux = (DemuxSession)await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(clip));
    int stream = demux.MediaInfo.VideoStreams[0].StreamIndex;
    await using var decoder = VideoDecoder.Open(
        demux.FormatContextPtr,
        stream,
        new HardwareDecodeOptions
        {
            Mode = HardwareDecodeMode.Required,
            PreferredBackends = [HardwareDecodeBackendKind.D3D12Va],
        },
        capabilities,
        loggerFactory: null);
    decoder.YieldHardwareFrames = yieldHardware;

    var frames = new List<IVideoFrame>();
    var consume = Task.Run(async () =>
    {
        await foreach (var frame in decoder.DecodeAsync())
        {
            if (frames.Count < max)
                frames.Add(frame);
            else
                frame.Dispose();
        }
    });
    await Packets.QueueAllAsync(demux.FormatContextPtr, stream, decoder);
    await consume;
    return frames;
}

static T Borrow<T>(nint pointer) where T : SharpGen.Runtime.ComObject
{
    // The wrapper releases on dispose, so it takes its own reference.
    Marshal.AddRef(pointer);
    return (T)Activator.CreateInstance(typeof(T), pointer)!;
}

static double Iou(Detection a, Detection b)
{
    float x0 = MathF.Max(a.X, b.X), y0 = MathF.Max(a.Y, b.Y);
    float x1 = MathF.Min(a.X + a.Width, b.X + b.Width), y1 = MathF.Min(a.Y + a.Height, b.Y + b.Height);
    float inter = MathF.Max(0, x1 - x0) * MathF.Max(0, y1 - y0);
    return inter / (a.Width * a.Height + b.Width * b.Height - inter);
}

static double P50(List<double> values)
{
    var sorted = values.Order().ToList();
    return sorted[sorted.Count / 2];
}

static void Report(bool pass, string label) => Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {label}");
