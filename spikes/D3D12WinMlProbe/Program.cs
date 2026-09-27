using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using D3D12DmlProbe;
using D3D12WinMlProbe;
using FrameFlow.Decoding;
using FrameFlow.Decoding.Diagnostics;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Yolo;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Windows.AI.MachineLearning;
using Vortice.Direct3D12;

// Question: can the D3D12 tensor D3D12DmlProbe builds reach Windows ML's TensorRT-RTX provider
// with no CPU copy? CUDA imports the D3D12 buffer through an NT handle, and ORT binds the CUDA
// pointer as the input. Each stage prints PASS or FAIL.
//
//   dotnet run --project spikes/D3D12WinMlProbe -c Release -- <clip.mp4> [--model yolov8n.onnx] [--frames 90]

string? clip = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a));
if (clip is null)
{
    Console.WriteLine("usage: D3D12WinMlProbe <clip> [--model path] [--frames n]");
    return 2;
}

string model = Arg("--model") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameFlow.Yolo", "models", "yolov8n.onnx");
int frameCount = int.Parse(Arg("--frames") ?? "90", CultureInfo.InvariantCulture);

var boot = new FrameFlowBootstrapper(new FrameFlowNativeOptions(), NullLoggerFactory.Instance).Initialize();
if (!boot.IsSuccess)
{
    Console.WriteLine("FFmpeg bootstrap failed.");
    return 1;
}

// Teardown probe: which releases, in which order, crash the process. Each mode opens a
// TensorRT-RTX session and runs it once. `frames` decodes D3D12VA frames; `-device` also holds the
// decoder's device and a queue on it; `-buffer`, `-shader`, `-import` and `-bound` add the
// spike's pieces in turn; `nodecode-` uses a device of our own instead of decoding.
// `-release-first` releases our device before the frames, `-release-after-frames` after them.
// Only the modes that hold the decoder's device while its last frame is freed crash.
if (Arg("--teardown-probe") is { } probeMode)
{
    List<IVideoFrame> probeFrames = probeMode.StartsWith("frames", StringComparison.Ordinal)
        ? await DecodeAsync(clip, boot.Capabilities!, yieldHardware: true, frameCount)
        : [];
    ID3D12Device? probeDevice = null;
    ID3D12CommandQueue? probeQueue = null;
    GpuPreprocess? probeGpu = null;
    nint probeMemory = 0, probeHandle = 0;
    ulong probePointer = 0;
    if (probeMode.StartsWith("nodecode", StringComparison.Ordinal))
    {
        Vortice.Direct3D12.D3D12.D3D12CreateDevice(null, Vortice.Direct3D.FeatureLevel.Level_11_0, out probeDevice).CheckError();
    }
    else if (probeFrames.Count > 0 && probeMode != "frames")
    {
        var ph = D3D12FrameHandles.Read((GpuVideoFrame)probeFrames[0]);
        using (var pt = Borrow<ID3D12Resource>(ph.Texture))
            probeDevice = pt.GetDevice<ID3D12Device>();
    }
    if (probeDevice is not null)
    {
        probeQueue = probeDevice.CreateCommandQueue(new CommandQueueDescription(CommandListType.Compute));
        if (!probeMode.Contains("device", StringComparison.Ordinal))
            probeGpu = new GpuPreprocess(probeDevice, probeQueue, 640, shareable: true);
        if (probeGpu is not null && probeMode.Contains("shader", StringComparison.Ordinal))
        {
            foreach (var f in probeFrames)
            {
                var fh = D3D12FrameHandles.Read((GpuVideoFrame)f);
                using var ft = Borrow<ID3D12Resource>(fh.Texture);
                using var ff = Borrow<ID3D12Fence>(fh.Fence);
                probeGpu.Run(ft, ff, fh.FenceValue, f.Width, f.Height);
            }
        }
        if (probeGpu is not null && probeMode.Contains("import", StringComparison.Ordinal))
        {
            Cuda.OpenDevice(probeDevice.AdapterLuid);
            probeHandle = probeDevice.CreateSharedHandle(probeGpu.Tensor, null, null!);
            ulong size = probeDevice.GetResourceAllocationInfo(0, probeGpu.Tensor.Description).SizeInBytes;
            (probeMemory, probePointer) = Cuda.Import(probeHandle, size, (ulong)probeGpu.TensorBytes);
        }
    }
    await ExecutionProviderCatalog.GetDefault().RegisterCertifiedAsync();
    var probeEnv = OrtEnv.Instance();
    var probeTrt = probeEnv.GetEpDevices().First(d => d.EpName.Contains("TensorRT", StringComparison.OrdinalIgnoreCase));
    using var probeOptions = new SessionOptions();
    probeOptions.AppendExecutionProvider(probeEnv, [probeTrt], new Dictionary<string, string>());
    var probeSession = new InferenceSession(model, probeOptions);
    using (var zeros = OrtValue.CreateTensorValueFromMemory(new float[3 * 640 * 640], [1, 3, 640, 640]))
    using (var probeRun = new RunOptions())
    using (probeSession.Run(probeRun, [probeSession.InputNames[0]], [zeros], [probeSession.OutputNames[0]])) { }
    if (probeMode.Contains("bound", StringComparison.Ordinal))
    {
        using var bound = OrtValue.CreateTensorValueWithData(
            probeSession.GetMemoryInfosForInputs()[0], TensorElementType.Float, [1, 3, 640, 640], (nint)probePointer, probeGpu!.TensorBytes);
        using var probeRun = new RunOptions();
        using (probeSession.Run(probeRun, [probeSession.InputNames[0]], [bound], [probeSession.OutputNames[0]])) { }
    }
    if (probeMemory != 0)
    {
        Console.Error.WriteLine($"probe {probeMode}: releasing the import");
        Cuda.Release(probeMemory, probePointer);
        CloseHandle(probeHandle);
    }
    probeGpu?.Dispose();
    if (probeMode.Contains("release-first", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"probe {probeMode}: releasing our queue and device first");
        probeQueue?.Dispose();
        probeDevice?.Dispose();
        probeQueue = null;
        probeDevice = null;
    }
    Console.Error.WriteLine($"probe {probeMode}: freeing {probeFrames.Count} frames");
    foreach (var f in probeFrames)
        f.Dispose();
    if (probeMode.Contains("release-after-frames", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"probe {probeMode}: releasing our queue and device after the frames");
        probeQueue?.Dispose();
        probeDevice?.Dispose();
        probeQueue = null;
        probeDevice = null;
    }
    Console.Error.WriteLine($"probe {probeMode}: disposing the session");
    probeSession.Dispose();
    probeQueue?.Dispose();
    probeDevice?.Dispose();
    Console.Error.WriteLine($"probe {probeMode}: done");
    return 0;
}

// A. The same frames on the GPU and, as the reference, read back to the CPU.
var gpuFrames = await DecodeAsync(clip, boot.Capabilities!, yieldHardware: true, frameCount);
DecodeStageMetrics.Reset();
DecodeStageMetrics.Enabled = true;
var cpuFrames = await DecodeAsync(clip, boot.Capabilities!, yieldHardware: false, frameCount);
DecodeStageMetrics.Enabled = false;
var readback = DecodeStageMetrics.Snapshot();
bool decoded = gpuFrames.Count > 0
    && gpuFrames.All(f => f is GpuVideoFrame { Backend: HardwareDecodeBackendKind.D3D12Va })
    && cpuFrames.Count == gpuFrames.Count
    && gpuFrames.Zip(cpuFrames).All(p => p.First.Pts == p.Second.Pts);
Report(decoded, $"A  D3D12VA decode: {gpuFrames.Count} GPU frames and their CPU reference frames, timestamps aligned");
if (!decoded)
    return 1;

// B. The decoder's device, our queue, and the shader writing a buffer on a shared heap.
var firstHandles = D3D12FrameHandles.Read((GpuVideoFrame)gpuFrames[0]);
var firstTexture = Borrow<ID3D12Resource>(firstHandles.Texture);
var device = firstTexture.GetDevice<ID3D12Device>();
var queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Compute));
const int Size = 640;
// Not `using`: the teardown at the end disposes it, in the order that avoids the crash.
#pragma warning disable CA2000
var gpu = new GpuPreprocess(device, queue, Size, shareable: true);
#pragma warning restore CA2000
Report(true, "B  D3D12 tensor buffer on a shared heap, written by the NV12 shader");

// C. CUDA imports the buffer, on the CUDA device with the D3D12 adapter's LUID.
int cudaDevice = Cuda.OpenDevice(device.AdapterLuid);
nint sharedHandle = device.CreateSharedHandle(gpu.Tensor, null, null!);
ulong allocationSize = device.GetResourceAllocationInfo(0, gpu.Tensor.Description).SizeInBytes;
var (externalMemory, cudaPointer) = Cuda.Import(sharedHandle, allocationSize, (ulong)gpu.TensorBytes);
RunGpu(gpuFrames[0]);
float[] throughD3D12 = gpu.ReadBack();
float[] throughCuda = Cuda.ReadBack(cudaPointer, throughD3D12.Length);
Report(throughD3D12.AsSpan().SequenceEqual(throughCuda),
    $"C  CUDA reads the shader's tensor through the imported buffer (allocation {allocationSize} bytes, pointer 0x{cudaPointer:X})");

// D. Windows ML's certified providers, already on the machine; nothing is downloaded.
var catalog = ExecutionProviderCatalog.GetDefault();
foreach (var provider in await catalog.RegisterCertifiedAsync())
    Console.WriteLine($"        registered {provider.Name} ({provider.ReadyState})");
var env = OrtEnv.Instance();
// The provider on the decoder's adapter: ORT's hardware-device metadata carries the adapter LUID.
string adapterLuid = device.AdapterLuid.ToString(CultureInfo.InvariantCulture);
var tensorRt = env.GetEpDevices().FirstOrDefault(d =>
    d.EpName.Contains("TensorRT", StringComparison.OrdinalIgnoreCase)
    && d.HardwareDevice.Metadata.Entries.TryGetValue("LUID", out var luid)
    && luid == adapterLuid);
if (tensorRt is null)
{
    Report(false, $"D  no TensorRT-RTX provider on the decoder's adapter (LUID {adapterLuid}); "
        + "installing one is EnsureAndRegisterCertifiedAsync's job, not this spike's");
    return 1;
}

using var options = new SessionOptions();
options.AppendExecutionProvider(env, [tensorRt], new Dictionary<string, string>());
var open = Stopwatch.StartNew();
#pragma warning disable CA2000 // disposed in the teardown at the end
var session = new InferenceSession(model, options);
#pragma warning restore CA2000
open.Stop();
string inputName = session.InputNames[0];
string outputName = session.OutputNames[0];
var inputMemory = session.GetMemoryInfosForInputs()[0];
// The input has to live on the CUDA device that imported the buffer.
Report(inputMemory.Id == cudaDevice,
    $"D  ORT {env.GetVersionString()} {tensorRt.EpName} session on the decoder's adapter (LUID {adapterLuid}) "
    + $"in {open.Elapsed.TotalSeconds:F1}s; input memory '{inputMemory.Name}' on CUDA device {inputMemory.Id}, "
    + $"the buffer on {cudaDevice}");
if (inputMemory.Id != cudaDevice)
    return 1;

// E. The CUDA pointer bound as the input, in the memory the provider asks for.
long[] shape = [1, 3, Size, Size];
var cpuPreprocess = new Yolov8Preprocessor(Size);
var cpuTensor = new float[cpuPreprocess.InputElementCount];
var boundInput = OrtValue.CreateTensorValueWithData(inputMemory, TensorElementType.Float, shape, (nint)cudaPointer, gpu.TensorBytes);
using var cpuInput = OrtValue.CreateTensorValueFromMemory(cpuTensor, shape);
using var runOptions = new RunOptions();

// E1. The binding alone: the CPU tensor, uploaded into the D3D12 buffer and read by TensorRT-RTX
// through CUDA, must give the outputs of the CPU tensor handed to ORT.
float bindingWorst = 0;
for (int i = 0; i < Math.Min(10, gpuFrames.Count); i++)
{
    cpuPreprocess.Preprocess(cpuFrames[i], cpuTensor);
    gpu.Upload(cpuTensor);
    float[] bound = Infer(boundInput);
    float[] reference = Infer(cpuInput);
    for (int j = 0; j < bound.Length; j++)
        bindingWorst = MathF.Max(bindingWorst, MathF.Abs(bound[j] - reference[j]));
}
Report(bindingWorst < 1e-3f, $"E1 same input through the CUDA-bound buffer and through ORT's upload: max output |diff| {bindingWorst:G3}");

// E2. The shader's tensor against the CPU path's, end to end.
float outputWorst = 0;
double outputSum = 0;
long outputCount = 0;
for (int i = 0; i < gpuFrames.Count; i++)
{
    RunGpu(gpuFrames[i]);
    float[] onGpu = Infer(boundInput);
    cpuPreprocess.Preprocess(cpuFrames[i], cpuTensor);
    float[] onCpu = Infer(cpuInput);
    for (int j = 0; j < onGpu.Length; j++)
    {
        float d = MathF.Abs(onGpu[j] - onCpu[j]);
        outputWorst = MathF.Max(outputWorst, d);
        outputSum += d;
        outputCount++;
    }
}
Console.WriteLine($"  E2 shader tensor vs CPU tensor over {gpuFrames.Count} frames: output |diff| mean {outputSum / outputCount:G3}, max {outputWorst:G3}");

// F. Timing per frame. The shader's GPU work is waited on before the run, so CUDA reads a
// finished buffer; the product would order the two on the GPU with a shared fence instead.
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
        using (session.Run(runOptions, [inputName], [boundInput], [outputName])) { }
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
Console.WriteLine($"        GPU path: preprocess {P50(gpuPre):F2} ms (shader, waited on)  run {P50(gpuRun):F2} ms (input on the GPU, through CUDA)");
Console.WriteLine($"        CPU path: download {readback.HardwareTransfer.P50Ms:F2} ms + NV12 to BGRA {readback.ColorConvert.P50Ms:F2} ms (in the decoder)  "
    + $"preprocess {P50(cpuPre):F2} ms  run {P50(cpuRun):F2} ms (input uploaded by ORT)");

// Teardown order matters. Every reference to the decoder's device is released before the
// decoder's last frame is freed; the other order crashes in this process (see --teardown-probe).
boundInput.Dispose();
Cuda.Release(externalMemory, cudaPointer);
CloseHandle(sharedHandle);
gpu.Dispose();
queue.Dispose();
firstTexture.Dispose();
device.Dispose();
foreach (var f in gpuFrames.Concat(cpuFrames))
    f.Dispose();
session.Dispose();
Cuda.CloseDevice(cudaDevice);
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
    Marshal.AddRef(pointer);
    return (T)Activator.CreateInstance(typeof(T), pointer)!;
}

static double P50(List<double> values)
{
    var sorted = values.Order().ToList();
    return sorted[sorted.Count / 2];
}

static void Report(bool pass, string label) => Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {label}");

[DllImport("kernel32.dll")]
static extern bool CloseHandle(nint handle);
