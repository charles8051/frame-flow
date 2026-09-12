// WinMlProbe — feasibility probe for "should FrameFlow add a Windows ML EP
// alongside FrameFlow.Inference.Dml?"
//
// WHAT THIS ANSWERS
// -----------------
// DirectML is in sustained engineering; Windows ML is the successor runtime and
// still ships the DirectML EP in-box, marked legacy. So the question is not
// "when does DML break" but "what does Windows ML add that DML cannot give us,
// on hardware we actually have?". Five things have to be true before a
// FrameFlow.Inference.WinML package is worth writing, and this probe checks
// them in order:
//
//   1  the self-contained Windows ML runtime loads in a plain unpackaged
//      console app — no MSIX, no WinAppSDK bootstrap, no installer
//   2  ORT sees CPU + DmlExecutionProvider before any catalog call, so the
//      existing DML recipe still works unchanged under the new runtime
//   3  the EP catalog reports something beyond those two on the host it runs on
//   4  registration adds those EPs to ORT's device list
//   5  a real YOLOv8 model opens and runs on each of them, and the numbers
//      justify the dependency
//
// Step 6 is the one that decides it. Steps 1-5 can all pass and still leave
// DML the right answer if nothing beats it on the hardware in front of us.
//
// READING THE RESULT
// ------------------
//   Step 2 shows only CPU        -> the runtime loaded but DirectML.dll did
//                                   not; the self-contained layout is wrong.
//   Step 3 lists nothing         -> no vendor EP targets this hardware. On a
//                                   box like that Windows ML buys deployment
//                                   ergonomics and nothing else. That is a
//                                   real result, not a failed run.
//   Step 3 lists NotPresent EPs  -> they exist for this hardware but are not
//                                   installed. Re-run with --acquire.
//   Step 6 DML fastest           -> keep FrameFlow.Inference.Dml as the
//                                   Windows GPU path and do not add a package.
//   Step 6 a vendor EP fastest   -> the margin is the case for the new EP.
//
// HOW TO RUN IT
// -------------
//   dotnet run --project spikes/WinMlProbe
//
// The default run is READ-ONLY with respect to the machine: it calls
// RegisterCertifiedAsync(), which registers execution providers that are
// already installed and downloads nothing.
//
//   dotnet run --project spikes/WinMlProbe -- --acquire
//
// --acquire switches to EnsureAndRegisterCertifiedAsync(), which DOWNLOADS AND
// INSTALLS every EP compatible with this device, system-wide, from Windows
// Update. That is a machine-modifying, several-hundred-megabyte, minutes-long
// operation on first run. It is opt-in for that reason. Run the default first:
// the inventory in step 3 tells you what --acquire would fetch before you let
// it fetch anything.
//
//   --model <path>   ONNX model to benchmark. Defaults to the FrameFlow.Yolo
//                    cache (ADR-0051's pre-seeded path), yolov8n.onnx.
//   --runs <n>       Timed iterations per EP after warmup. Default 30.
//   --only <text>    Benchmark only configurations whose label contains <text>.
//   --no-register    Skip step 4 entirely, leaving ORT with only its in-box
//                    CPU and DirectML EPs. Pair it with --only to attribute a
//                    policy result: if "policy PREFER_GPU" is fast with
//                    registration and drops to DirectML speed without it, the
//                    speed came from the vendor EP the policy selected.
//
// USE --only FOR ANY NUMBER YOU INTEND TO QUOTE. Sessions in one process are
// not independent: DirectML rows warm the GPU for rows after them, and the
// first TensorRT-RTX session builds engines that later sessions in the same
// process reuse. On a discrete NVIDIA GPU, an all-in-one-process run put
// NvTensorRTRTX at 6.62 ms and the policy row that selects it at 1.93 ms —
// same EP, same model, 3:1 apart, entirely from ordering. One process per
// configuration is the only reading that compares like with like:
//
//   foreach ($c in 'recipe','defaults','CPU','NvTensorRTRTX','MAX_PERF','PREFER_GPU') {
//       dotnet run --project spikes/WinMlProbe -- --runs 50 --only $c
//   }

using System.Diagnostics;
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;

namespace WinMlProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        bool acquire = args.Contains("--acquire");
        int runs = ArgInt(args, "--runs", 30);
        string modelPath = ArgString(args, "--model", DefaultModelPath())!;
        string? only = ArgString(args, "--only", null);
        bool noRegister = args.Contains("--no-register");

        Console.WriteLine($"WinMlProbe  pid={Environment.ProcessId}  {DateTimeOffset.UtcNow:O}");
        Console.WriteLine($"OS {Environment.OSVersion.Version}  {(Environment.Is64BitProcess ? "x64" : "x86")}");
        Console.WriteLine($"mode={(acquire ? "ACQUIRE (downloads + installs EPs)" : "read-only (registers what is already present)")}");
        Console.WriteLine();

        // ---- 1  does the self-contained runtime load at all, unpackaged? ----
        Console.WriteLine("--- 1  Windows ML runtime ---");
        OrtEnv env;
        try
        {
            env = OrtEnv.Instance();
            Console.WriteLine($"  PASS  OrtEnv.Instance()  ORT {env.GetVersionString()}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAIL  OrtEnv.Instance()");
            Console.WriteLine($"        {ex.GetType().Name}: {Squash(ex.Message)}");
            return 1;
        }

        Console.WriteLine();

        // ---- 2  what ORT sees with no catalog involvement ----
        Console.WriteLine("--- 2  EP devices before registration ---");
        PrintDevices(env);
        Console.WriteLine();

        // ---- 3  what the catalog offers for this hardware ----
        Console.WriteLine("--- 3  EP catalog inventory ---");
        ExecutionProviderCatalog catalog;
        try
        {
            catalog = ExecutionProviderCatalog.GetDefault();
            ExecutionProvider[] providers = catalog.FindAllProviders();
            if (providers.Length == 0)
            {
                Console.WriteLine("  (none — no vendor EP targets this hardware)");
            }
            else
            {
                foreach (ExecutionProvider p in providers)
                    Console.WriteLine($"  {p.Name,-32} {p.ReadyState,-12} {p.Certification}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAIL  ExecutionProviderCatalog.GetDefault() / FindAllProviders()");
            Console.WriteLine($"        {ex.GetType().Name}: {Squash(ex.Message)}");
            return 1;
        }

        Console.WriteLine();

        // ---- 4  registration ----
        Console.WriteLine($"--- 4  {(noRegister ? "skipped (--no-register)" : acquire ? "EnsureAndRegisterCertifiedAsync()" : "RegisterCertifiedAsync()")} ---");
        var sw = Stopwatch.StartNew();
        if (noRegister)
        {
            Console.WriteLine("  in-box EPs only");
        }
        else
        {
            try
            {
                var operation = acquire
                    ? catalog.EnsureAndRegisterCertifiedAsync()
                    : catalog.RegisterCertifiedAsync();

                operation.Progress = (_, percent) =>
                    Console.WriteLine($"        {percent,6:F1}%  {sw.Elapsed.TotalSeconds,6:F1}s");

                IList<ExecutionProvider> registered = await operation;
                sw.Stop();

                Console.WriteLine($"  PASS  {registered.Count} provider(s) in {sw.Elapsed.TotalSeconds:F1}s");
                foreach (ExecutionProvider p in registered)
                    Console.WriteLine($"        {p.Name} ({p.ReadyState})");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"  FAIL  after {sw.Elapsed.TotalSeconds:F1}s");
                Console.WriteLine($"        {ex.GetType().Name}: {Squash(ex.Message)}");
            }
        }

        Console.WriteLine();

        // ---- 5  what ORT sees now ----
        Console.WriteLine("--- 5  EP devices after registration ---");
        IReadOnlyList<OrtEpDevice> devices = PrintDevices(env);
        Console.WriteLine();

        // ---- 6  the number that decides it ----
        Console.WriteLine("--- 6  YOLOv8 open + warmup + steady state ---");
        if (!File.Exists(modelPath))
        {
            Console.WriteLine($"  SKIP  no model at {modelPath}");
            Console.WriteLine("        pass --model <path>, or let an example populate the FrameFlow.Yolo cache.");
            return 0;
        }

        Console.WriteLine($"  model {modelPath}  ({new FileInfo(modelPath).Length / 1024 / 1024} MB)  runs={runs}");

        // Whichever configuration is benchmarked first pays for the whole
        // process: GPU clocks ramping off idle, the driver's shader cache, ORT's
        // one-time native init. On a discrete NVIDIA GPU, the first row came
        // out at 14.62 ms median / 51.75 ms p95 and the identical configuration
        // measured 3.41 / 4.81 once something had run before it — a 4:1 error,
        // large enough to invent an EP difference that does not exist. So burn
        // that cost on a session nobody reports.
        Prime(modelPath);

        Console.WriteLine();
        Console.WriteLine($"  {"configuration",-46} {"open",8} {"warmup",9} {"median",9} {"p95",9}");

        // The DML recipe FrameFlow.Inference.Dml already ships, verbatim, so the
        // comparison is against what we run today rather than a generic baseline.
        Benchmark("DML (DmlInferenceSession recipe)", only, modelPath, runs, o =>
        {
            o.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC;
            o.EnableMemoryPattern = false;
            o.AppendExecutionProvider_DML();
        });

        // The three rows below exist because the first run of this probe showed
        // the EP-selection-policy path beating the recipe row 4:1 on the same
        // DirectML EP and the same adapter. The only difference between them is
        // session options, so the recipe's two overrides get isolated here. Both
        // were carried in from an older ORT; DmlInferenceSession's comments
        // assert the EP requires them, and this is the measurement that says
        // whether that is still true.
        Benchmark("DML (ORT defaults)", only, modelPath, runs, o => o.AppendExecutionProvider_DML());

        Benchmark("DML (BASIC opt only)", only, modelPath, runs, o =>
        {
            o.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC;
            o.AppendExecutionProvider_DML();
        });

        Benchmark("DML (no memory pattern only)", only, modelPath, runs, o =>
        {
            o.EnableMemoryPattern = false;
            o.AppendExecutionProvider_DML();
        });

        Benchmark("CPU", only, modelPath, runs, o => o.AppendExecutionProvider_CPU(1));

        // Every EP device the catalog put in front of us, individually, so a
        // vendor EP that wins can be attributed rather than inferred.
        foreach (OrtEpDevice device in devices)
        {
            // Covered above by their own recipes.
            if (device.EpName is "CPUExecutionProvider" or "DmlExecutionProvider")
                continue;

            Benchmark(
                $"{device.EpName} ({device.HardwareDevice.Type})",
                only,
                modelPath,
                runs,
                o => o.AppendExecutionProvider(env, [device], new Dictionary<string, string>()));
        }

        // What Windows ML picks on its own. If this matches the fastest row
        // above, the policy is trustworthy and FrameFlow could defer to it
        // instead of maintaining its own fallback chain on this platform.
        foreach (var policy in new[]
                 {
                     ExecutionProviderDevicePolicy.MAX_PERFORMANCE,
                     ExecutionProviderDevicePolicy.PREFER_GPU,
                 })
        {
            Benchmark($"policy {policy}", only, modelPath, runs, o => o.SetEpSelectionPolicy(policy));
        }

        return 0;
    }

    /// <summary>
    /// Untimed, unreported run on the GPU EP, so the first row of the table is
    /// not the row that pays for process-wide and driver-wide warm-up. See the
    /// call site for the numbers that made this necessary.
    /// </summary>
    private static void Prime(string modelPath)
    {
        try
        {
            using var options = new SessionOptions();
            options.AppendExecutionProvider_DML();
            using var session = new InferenceSession(modelPath, options);

            (string name, long[] shape) = InputSpec(session);
            var data = new float[shape.Aggregate(1L, (a, b) => a * b)];
            using var runOptions = new RunOptions();
            string[] outputNames = [.. session.OutputMetadata.Keys];

            for (int i = 0; i < 20; i++)
                RunOnce(session, runOptions, name, data, shape, outputNames);

            Console.WriteLine("  primed on DML (20 untimed runs)");
        }
        catch (Exception ex)
        {
            // A box with no working DML still benchmarks fine; it just carries
            // the warm-up cost into whatever row comes first.
            Console.WriteLine($"  prime skipped: {ex.GetType().Name}: {Squash(ex.Message)}");
        }
    }

    private static IReadOnlyList<OrtEpDevice> PrintDevices(OrtEnv env)
    {
        IReadOnlyList<OrtEpDevice> devices = env.GetEpDevices();
        foreach (OrtEpDevice d in devices)
            Console.WriteLine($"  {d.EpName,-32} {d.HardwareDevice.Type,-4} {d.EpVendor} (device {d.HardwareDevice.DeviceId})");
        return devices;
    }

    private static void Benchmark(
        string label, string? only, string modelPath, int runs, Action<SessionOptions> configure)
    {
        if (only is not null && !label.Contains(only, StringComparison.OrdinalIgnoreCase))
            return;

        SessionOptions? options = null;
        InferenceSession? session = null;
        try
        {
            options = new SessionOptions();
            configure(options);

            var sw = Stopwatch.StartNew();
            session = new InferenceSession(modelPath, options);
            double openMs = sw.Elapsed.TotalMilliseconds;

            (string name, long[] shape) = InputSpec(session);
            var data = new float[shape.Aggregate(1L, (a, b) => a * b)];

            using var runOptions = new RunOptions();
            string[] outputNames = [.. session.OutputMetadata.Keys];

            // Warmup is a separate number on purpose: on compiling EPs it is
            // where the kernel build shows up, and a fast steady state behind a
            // multi-second warmup is a different deployment story than both
            // being fast.
            sw.Restart();
            RunOnce(session, runOptions, name, data, shape, outputNames);
            double warmupMs = sw.Elapsed.TotalMilliseconds;

            var samples = new double[runs];
            for (int i = 0; i < runs; i++)
            {
                sw.Restart();
                RunOnce(session, runOptions, name, data, shape, outputNames);
                samples[i] = sw.Elapsed.TotalMilliseconds;
            }

            Array.Sort(samples);
            double median = samples[samples.Length / 2];
            double p95 = samples[Math.Min((int)(samples.Length * 0.95), samples.Length - 1)];

            Console.WriteLine($"  {label,-46} {openMs,8:F0} {warmupMs,9:F1} {median,9:F2} {p95,9:F2}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {label,-46}   FAIL  {ex.GetType().Name}: {Squash(ex.Message)}");
        }
        finally
        {
            session?.Dispose();
            options?.Dispose();
        }
    }

    private static void RunOnce(
        InferenceSession session, RunOptions runOptions,
        string inputName, float[] data, long[] shape, string[] outputNames)
    {
        using var input = OrtValue.CreateTensorValueFromMemory(data, shape);
        var inputs = new Dictionary<string, OrtValue> { [inputName] = input };
        using var results = session.Run(runOptions, inputs, outputNames);
    }

    /// <summary>
    /// First input's name and shape, with dynamic dimensions pinned to 1.
    /// Mirrors YoloModelDescriptor.FromSession's stance (ADR-0050 §2): read the
    /// shape off the model rather than hardcoding 1x3x640x640, so a minted
    /// 320 / 416 model benchmarks without a code change.
    /// </summary>
    private static (string Name, long[] Shape) InputSpec(InferenceSession session)
    {
        var first = session.InputMetadata.First();
        long[] shape = [.. first.Value.Dimensions.Select(d => (long)(d < 0 ? 1 : d))];
        return (first.Key, shape);
    }

    /// <summary>ADR-0051's pre-seeded cache path — where the examples already put yolov8n.onnx.</summary>
    private static string DefaultModelPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FrameFlow.Yolo",
        "models",
        "yolov8n.onnx");

    private static string Squash(string message) =>
        message.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static int ArgInt(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length
               && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    private static string? ArgString(string[] args, string name, string? fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
}
