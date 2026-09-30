// OpenVinoProbe: on an Intel integrated GPU, does ONNX Runtime's OpenVINO execution provider run a
// model faster than FrameFlow's DirectML session?
//
// The two providers ship in different onnxruntime.dll builds (Intel.ML.OnnxRuntime.OpenVino and
// Microsoft.ML.OnnxRuntime.DirectML), and a process loads one of them. So this file builds twice,
// from OpenVino/ and Dml/. Run both on the same machine, alternating, and compare their rows.
//
// CONFIGURATIONS
//   ort-cpu     ONNX Runtime's own CPU provider. Both builds, as the common baseline.
//   dml         DmlInferenceSession with its defaults, as an app gets it.     (Dml build)
//   ov-cpu      OpenVINO on the CPU.                                         (OpenVino build)
//   ov-gpu      OpenVINO on the GPU with OpenVINO's default precision hint.  (OpenVino build)
//   ov-gpu-f32  OpenVINO on the GPU with the f32 precision hint.             (OpenVino build)
//   ov-gpu-low  ov-gpu with the GPU queue throttle hint at LOW.              (OpenVino build)
//
// The OpenVINO sessions are opened with CPU fallback disabled, so a model OpenVINO cannot take
// whole fails to open that way. The probe then reopens it with fallback allowed and marks the row
// "partial".
//
// Each row reports: the open time, then a second open (warm for OpenVINO, which caches the
// compiled model); the first run; then timed runs after warmup, as p50/p90/p99 wall time and
// process CPU time per run. max|d| is the largest difference from ort-cpu's output for the same
// input, and rel divides it by the largest ort-cpu output. The input is seeded noise, so the
// difference says whether the provider computes the same function, not how detection accuracy
// moves.
//
// RUN
//   dotnet publish spikes/OpenVinoProbe/OpenVino -c Release -o <dir>/ov
//   dotnet publish spikes/OpenVinoProbe/Dml -c Release -o <dir>/dml
//   <dir>/ov/OpenVinoProbe.exe [options] <model.onnx> [more.onnx ...]
//
//   --configs a,b   Configurations to run, in order. Default: every one in this build.
//   --warmup <n>    Untimed runs before timing. Default 20.
//   --runs <n>      Timed runs. Default 200.
//   --budget <s>    Stop timing a row after this many seconds, once it has 30 runs. Default 20.
//   --csv <path>    Append one line per row to this file.
//   --tag <text>    Written to the csv's tag column, to tell rounds apart.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FrameFlow.Graph;
using FrameFlow.Inference;
using Microsoft.ML.OnnxRuntime;
using TensorElementType = Microsoft.ML.OnnxRuntime.Tensors.TensorElementType;
#if DML
using FrameFlow.Inference.Dml;
#endif

// CA2000: Open returns each session to a caller that disposes it.
#pragma warning disable CA2000

#if DML
const string Build = "dml";
string[] allConfigs = ["ort-cpu", "dml"];
#elif OPENVINO
const string Build = "openvino";
string[] allConfigs = ["ort-cpu", "ov-cpu", "ov-gpu", "ov-gpu-f32", "ov-gpu-low"];
#endif

var configs = allConfigs;
int warmup = 20, runs = 200;
double budget = 20;
string? csv = null, tag = null;
var models = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--configs": configs = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
        case "--warmup": warmup = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--runs": runs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--budget": budget = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--csv": csv = args[++i]; break;
        case "--tag": tag = args[++i]; break;
        default: models.Add(args[i]); break;
    }
}

if (models.Count == 0)
{
    Console.Error.WriteLine("usage: OpenVinoProbe [--configs a,b] [--warmup n] [--runs n] [--budget s] [--csv path] [--tag text] <model.onnx>...");
    return 2;
}

var unknown = configs.Except(allConfigs).ToArray();
if (unknown.Length > 0)
{
    Console.Error.WriteLine($"not in the {Build} build: {string.Join(", ", unknown)}. This build has: {string.Join(", ", allConfigs)}");
    return 2;
}

var env = OrtEnv.Instance();
var ortVersion = env.GetVersionString();
Console.WriteLine($"build {Build} | ORT {ortVersion} | {Environment.OSVersion.VersionString} | {Environment.ProcessorCount} logical CPUs");
Console.WriteLine($"providers: {string.Join(", ", env.GetAvailableProviders())}");
try
{
    foreach (var d in env.GetEpDevices())
        Console.WriteLine($"  device: {d.EpName,-28} {d.HardwareDevice.Type,-4} {d.HardwareDevice.Vendor} (vendor 0x{d.HardwareDevice.VendorId:x4}, device 0x{d.HardwareDevice.DeviceId:x4})");
}
catch (Exception ex)
{
    Console.WriteLine($"  device list unavailable: {ex.GetType().Name}: {ex.Message}");
}
Console.WriteLine($"warmup {warmup} | runs {runs} | budget {budget}s");

if (csv is not null && !File.Exists(csv))
    File.AppendAllText(csv, "tag,build,ort,model,config,fallback,open_ms,reopen_ms,first_ms,runs,p50_ms,p90_ms,p99_ms,mean_ms,cpu_ms_per_run,max_abs_diff,rel_diff,error" + Environment.NewLine);

var cacheRoot = Path.Combine(Path.GetTempPath(), "OpenVinoProbe-cache", Guid.NewGuid().ToString("N"));
using var pool = new CpuTensorPool();

foreach (var model in models)
{
    Console.WriteLine();
    Console.WriteLine($"== {Path.GetFileName(model)}");
    if (!File.Exists(model))
    {
        Console.WriteLine($"   missing: {model}");
        continue;
    }

    ModelIo io;
    try
    {
        io = ModelIo.Read(model);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   cannot probe: {ex.Message}");
        continue;
    }

    Console.WriteLine($"   inputs  {string.Join("; ", io.Inputs.Select(t => t.Describe()))}");
    Console.WriteLine($"   outputs {string.Join("; ", io.Outputs.Select(t => t.Describe()))}");
    Console.WriteLine($"   {"config",-11} {"fallback",-8} {"open",8} {"reopen",8} {"first",8} {"runs",5} {"p50",8} {"p90",8} {"p99",8} {"cpu/run",8} {"max|d|",9} {"rel",9}");

    var inputs = io.Inputs.ToDictionary(t => t.Name, t => t.Rent(pool, fillSeed: 42));
    var outputs = io.Outputs.ToDictionary(t => t.Name, t => t.Rent(pool, fillSeed: null));
    float[][]? reference = null;

    foreach (var config in configs)
    {
        var cacheDir = Path.Combine(cacheRoot, Path.GetFileNameWithoutExtension(model), config);
        Row row;
        try
        {
            row = Measure(config, model, cacheDir, inputs, outputs, warmup, runs, budget);
            var got = io.Outputs.Select(t => ToFloats(outputs[t.Name])).ToArray();
            if (config == "ort-cpu")
                reference = got;
            else if (reference is not null)
                row = row with { Diff = Compare(reference, got) };
        }
        catch (Exception ex)
        {
            row = new Row(config) { Error = $"{ex.GetType().Name}: {FirstLine(ex.Message)}" };
            Console.Error.WriteLine($"   {config} failed: {ex.Message}");
        }

        Console.WriteLine("   " + row.Format());
        if (csv is not null)
            File.AppendAllText(csv, row.Csv(tag, Build, ortVersion, Path.GetFileName(model)) + Environment.NewLine);
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    foreach (var t in inputs.Values.Concat(outputs.Values))
        t.Dispose();
}

try
{
    if (Directory.Exists(cacheRoot))
        Directory.Delete(cacheRoot, recursive: true);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    // OpenVINO can still hold a cached blob open at exit; the directory is under %TEMP%.
    Console.WriteLine($"cache left at {cacheRoot}: {ex.Message}");
}

return 0;

static Row Measure(
    string config,
    string model,
    string cacheDir,
    Dictionary<string, ICpuTensor> inputs,
    Dictionary<string, ICpuTensor> outputs,
    int warmup,
    int runs,
    double budget)
{
    var sw = Stopwatch.StartNew();
    var (first, fallback) = Open(config, model, cacheDir);
    var openMs = sw.Elapsed.TotalMilliseconds;
    first.Dispose();

    sw.Restart();
    var (session, _) = Open(config, model, cacheDir);
    var reopenMs = sw.Elapsed.TotalMilliseconds;

    using (session)
    {
        sw.Restart();
        session.Run(inputs, outputs);
        var firstMs = sw.Elapsed.TotalMilliseconds;

        for (var i = 0; i < warmup; i++)
            session.Run(inputs, outputs);

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var times = new List<double>(runs);
        var wall = Stopwatch.StartNew();
        while (times.Count < runs && (times.Count < 30 || wall.Elapsed.TotalSeconds < budget))
        {
            var t0 = Stopwatch.GetTimestamp();
            session.Run(inputs, outputs);
            times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        process.Refresh();
        var cpuPerRun = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds / times.Count;

        times.Sort();
        return new Row(config)
        {
            Fallback = fallback,
            OpenMs = openMs,
            ReopenMs = reopenMs,
            FirstMs = firstMs,
            Runs = times.Count,
            P50 = Percentile(times, 0.50),
            P90 = Percentile(times, 0.90),
            P99 = Percentile(times, 0.99),
            Mean = times.Average(),
            CpuPerRun = cpuPerRun,
        };
    }
}

static (OrtInferenceSessionBase Session, string Fallback) Open(string config, string model, string cacheDir) => config switch
{
    "ort-cpu" => (new CpuInferenceSession(model), "-"),
#if DML
    "dml" => (new DmlInferenceSession(model), "-"),
#elif OPENVINO
    "ov-cpu" => OpenVinoSession.Open(model, "CPU", precisionHint: null, throttle: null, cacheDir: null),
    "ov-gpu" => OpenVinoSession.Open(model, "GPU", precisionHint: null, throttle: null, cacheDir),
    "ov-gpu-f32" => OpenVinoSession.Open(model, "GPU", precisionHint: "f32", throttle: null, cacheDir),
    "ov-gpu-low" => OpenVinoSession.Open(model, "GPU", precisionHint: null, throttle: "LOW", cacheDir),
#endif
    _ => throw new ArgumentException($"unknown configuration '{config}'"),
};

static double Percentile(List<double> sorted, double q) =>
    sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];

static float[] ToFloats(ICpuTensor tensor) => tensor switch
{
    CpuTensor<float> f => f.ReadOnlySpan.ToArray(),
    CpuTensor<Half> h => h.ReadOnlySpan.ToArray().Select(x => (float)x).ToArray(),
    _ => throw new NotSupportedException($"output dtype {tensor.Dtype}"),
};

static (double MaxAbs, double Rel) Compare(float[][] reference, float[][] got)
{
    double maxAbs = 0, maxRef = 0;
    for (var o = 0; o < reference.Length; o++)
    {
        for (var i = 0; i < reference[o].Length; i++)
        {
            maxAbs = Math.Max(maxAbs, Math.Abs((double)reference[o][i] - got[o][i]));
            maxRef = Math.Max(maxRef, Math.Abs((double)reference[o][i]));
        }
    }
    return (maxAbs, maxRef == 0 ? maxAbs : maxAbs / maxRef);
}

static string FirstLine(string s)
{
    var line = s.Split('\n', 2)[0].Trim();
    return line.Length > 240 ? line[..240] + "..." : line;
}

internal sealed record Row(string Config)
{
    public string Fallback { get; init; } = "-";
    public double OpenMs { get; init; }
    public double ReopenMs { get; init; }
    public double FirstMs { get; init; }
    public int Runs { get; init; }
    public double P50 { get; init; }
    public double P90 { get; init; }
    public double P99 { get; init; }
    public double Mean { get; init; }
    public double CpuPerRun { get; init; }
    public (double MaxAbs, double Rel)? Diff { get; init; }
    public string? Error { get; init; }

    public string Format()
    {
        if (Error is not null)
            return $"{Config,-11} FAILED   {Error}";
        var diff = Diff is { } d ? $"{d.MaxAbs,9:G3} {d.Rel,9:G3}" : $"{"",9} {"",9}";
        return $"{Config,-11} {Fallback,-8} {OpenMs,8:F0} {ReopenMs,8:F0} {FirstMs,8:F1} {Runs,5} {P50,8:F2} {P90,8:F2} {P99,8:F2} {CpuPerRun,8:F2} {diff}";
    }

    public string Csv(string? tag, string build, string ort, string model)
    {
        static string N(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
        static string Q(string? s) => s is null ? "" : "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return string.Join(',',
            Q(tag), build, ort, Q(model), Config, Fallback,
            N(OpenMs), N(ReopenMs), N(FirstMs), Runs.ToString(CultureInfo.InvariantCulture),
            N(P50), N(P90), N(P99), N(Mean), N(CpuPerRun),
            Diff is { } d ? d.MaxAbs.ToString("G6", CultureInfo.InvariantCulture) : "",
            Diff is { } r ? r.Rel.ToString("G6", CultureInfo.InvariantCulture) : "",
            Q(Error));
    }
}

/// <summary>A model's inputs and outputs, read once through a plain CPU session.</summary>
internal sealed record ModelIo(IReadOnlyList<TensorSpec> Inputs, IReadOnlyList<TensorSpec> Outputs)
{
    public static ModelIo Read(string model)
    {
        using var session = new InferenceSession(model);
        return new ModelIo(
            session.InputMetadata.Select(kv => TensorSpec.From(kv.Key, kv.Value)).ToArray(),
            session.OutputMetadata.Select(kv => TensorSpec.From(kv.Key, kv.Value)).ToArray());
    }
}

internal sealed record TensorSpec(string Name, DType Dtype, int[] Shape)
{
    public static TensorSpec From(string name, NodeMetadata meta)
    {
        var dtype = meta.ElementDataType switch
        {
            TensorElementType.Float => DType.Float32,
            TensorElementType.Float16 => DType.Float16,
            var other => throw new NotSupportedException($"'{name}' is {other}; the probe handles float32 and float16"),
        };
        if (meta.Dimensions.Any(d => d <= 0))
            throw new NotSupportedException($"'{name}' has a free dimension [{string.Join(',', meta.Dimensions)}]; the probe needs a static model");
        return new TensorSpec(name, dtype, meta.Dimensions);
    }

    public string Describe() => $"{Name} {Dtype} [{string.Join('x', Shape)}]";

    public ICpuTensor Rent(CpuTensorPool pool, int? fillSeed)
    {
        var shape = new TensorShape(Shape);
        var random = fillSeed is { } seed ? new Random(seed) : null;
        if (Dtype == DType.Float32)
        {
            var t = pool.Rent<float>(shape);
            var span = t.Span;
            for (var i = 0; i < span.Length; i++)
                span[i] = random is null ? 0f : random.NextSingle();
            return t;
        }
        else
        {
            var t = pool.Rent<Half>(shape);
            var span = t.Span;
            for (var i = 0; i < span.Length; i++)
                span[i] = random is null ? Half.Zero : (Half)random.NextSingle();
            return t;
        }
    }
}

#if OPENVINO
/// <summary>
/// ONNX Runtime's OpenVINO provider on FrameFlow's session base, so a run takes the same binding
/// path as <c>DmlInferenceSession</c>.
/// </summary>
internal sealed class OpenVinoSession : OrtInferenceSessionBase
{
    private const string FallbackRefused = "fallback to CPU EP has been explicitly disabled";

    private OpenVinoSession(string model, SessionOptions options)
        : base(model, options) { }

    /// <summary>
    /// Opens <paramref name="model"/> on OpenVINO's <paramref name="device"/> with CPU fallback
    /// disabled, or, when ONNX Runtime refuses that, with it allowed.
    /// </summary>
    public static (OrtInferenceSessionBase Session, string Fallback) Open(
        string model, string device, string? precisionHint, string? throttle, string? cacheDir)
    {
        try
        {
            return (new OpenVinoSession(model, Options(device, precisionHint, throttle, cacheDir, allowCpuFallback: false)), "none");
        }
        catch (OnnxRuntimeException ex) when (ex.Message.Contains(FallbackRefused, StringComparison.OrdinalIgnoreCase))
        {
            return (new OpenVinoSession(model, Options(device, precisionHint, throttle, cacheDir, allowCpuFallback: true)), "partial");
        }
    }

    private static SessionOptions Options(
        string device, string? precisionHint, string? throttle, string? cacheDir, bool allowCpuFallback)
    {
        var properties = new Dictionary<string, string> { ["PERFORMANCE_HINT"] = "LATENCY" };
        if (precisionHint is not null)
            properties["INFERENCE_PRECISION_HINT"] = precisionHint;
        // The GPU plugin passes this to the OpenCL queue as a throttle hint; at LOW the driver
        // may wait for a kernel without holding a CPU thread.
        if (throttle is not null)
            properties["GPU_QUEUE_THROTTLE"] = throttle;
        if (cacheDir is not null)
        {
            Directory.CreateDirectory(cacheDir);
            properties["CACHE_DIR"] = cacheDir;
        }

        var options = new SessionOptions();
        if (!allowCpuFallback)
            options.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
        options.AppendExecutionProvider("OpenVINO", new Dictionary<string, string>
        {
            ["device_type"] = device,
            ["load_config"] = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>> { [device] = properties }),
        });
        return options;
    }
}
#endif
