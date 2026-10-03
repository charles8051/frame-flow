// Exempt from the wall-clock ratchet (ADR-0072, rule 6). The soak looks for a blit that never
// returns, so the property under test is real elapsed time: no fake clock can stand in for a
// driver call that does not come back. It runs only on request (FRAMEFLOW_VP_SOAK_MINUTES).
// Scoped to this file.
#pragma warning disable RS0030

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FrameFlow.Avalonia.Windows.Core;
using FrameFlow.Decoding;
using FrameFlow.Inference.D3D12.Tests;
using FrameFlow.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit.Abstractions;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// Two video-processor converters on one NVIDIA adapter, each with its own decoder, device,
/// compositor-side reader and thread, blitting 1080p to 3840x2160 with the driver's super
/// resolution on, until a blit stalls or the time runs out (ADR-0082, amendment of 2026-10-03).
/// </summary>
/// <remarks>
/// <para>
/// Once a minute each stream blits frame 0 and compares the result with plain video-processor
/// scaling of the same frame. The driver upscales one stream at a time across the system, so the
/// test asks only that some stream got the upscale; the log says which.
/// </para>
/// <para>
/// Runs only when <c>FRAMEFLOW_VP_SOAK_MINUTES</c> is set, on a machine that also meets
/// <c>FRAMEFLOW_EXPECT_DRIVER_VSR=1</c>. Progress goes to <c>FRAMEFLOW_VP_SOAK_LOG</c> (default
/// <c>%TEMP%\frameflow-vp-soak.log</c>) once a minute. <c>FRAMEFLOW_VP_SOAK_FPS</c> paces each
/// stream (default unpaced). <c>FRAMEFLOW_VP_SOAK_STREAMS</c> sets the number of streams (default 2).
/// <c>FRAMEFLOW_VP_SOAK_INJECT_STALL=B</c> holds stream B for twice the
/// stall limit once, to show the stall detector fires.
/// </para>
/// </remarks>
[Collection(FrameCopyCountsCollection.Name)]
public sealed class D3D11VideoProcessorSoakTests(ITestOutputHelper testOutput)
{
    private const string FullHd = "test-1080p-h264-aac.mp4";
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(60);

    [RequiresVideoProcessorSoakFact(FullHd)]
    public async Task TwoConverters_BlitConcurrently_WithoutStalling()
    {
        double minutes = double.Parse(Environment.GetEnvironmentVariable("FRAMEFLOW_VP_SOAK_MINUTES")!, CultureInfo.InvariantCulture);
        double fps = double.TryParse(Environment.GetEnvironmentVariable("FRAMEFLOW_VP_SOAK_FPS"), CultureInfo.InvariantCulture, out var f) ? f : 0;
        string logPath = Environment.GetEnvironmentVariable("FRAMEFLOW_VP_SOAK_LOG")
            ?? Path.Combine(Path.GetTempPath(), "frameflow-vp-soak.log");
        var control = new SoakControl(Environment.GetEnvironmentVariable("FRAMEFLOW_VP_SOAK_INJECT_STALL"), fps);
        var output = new PresenterOutput(true, 3840, 2160);

        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Log(string line)
        {
            string stamped = $"{DateTime.Now:HH:mm:ss} {line}";
            log.WriteLine(stamped);
            testOutput.WriteLine(stamped);
        }

        int streamCount = int.TryParse(Environment.GetEnvironmentVariable("FRAMEFLOW_VP_SOAK_STREAMS"), out var n) ? n : 2;
        var streams = new SoakStream[streamCount];
        for (int i = 0; i < streamCount; i++)
            streams[i] = await SoakStream.OpenAsync(((char)('A' + i)).ToString(), output);
        Log($"soak: process {Environment.ProcessId}, {streamCount} streams, {minutes} min, {(fps > 0 ? $"{fps} fps per stream" : "unpaced")}, {streams[0].Width}x{streams[0].Height} -> {output.Width}x{output.Height}, "
            + $"adapter {streams[0].Adapter}, stall limit {StallLimit.TotalSeconds} s, inject stall {control.InjectStall ?? "none"}");
        foreach (var s in streams)
            Log($"{s.Name}: decode device 0x{s.Converter.SourceDevicePointer:X}, vendor 0x{s.Converter.AdapterVendorId:X4}");

        long start = Stopwatch.GetTimestamp();
        control.Start = start;
        // Streams open one after another, so the stall clock starts here, not at each open.
        foreach (var s in streams)
            Volatile.Write(ref s.LastProgress, start);
        var threads = streams.Select(s => new Thread(() => Run(s, control)) { IsBackground = true, Name = $"soak-{s.Name}" }).ToArray();
        foreach (var t in threads)
            t.Start();

        long end = start + Ticks(TimeSpan.FromMinutes(minutes));
        long nextReport = start + Ticks(ReportEvery);
        var problems = new List<string>();
        while (Stopwatch.GetTimestamp() < end && problems.Count == 0)
        {
            await Task.Delay(1000);
            long now = Stopwatch.GetTimestamp();
            foreach (var s in streams)
            {
                if (s.Error is { } error)
                    problems.Add($"{s.Name} failed in {s.Phase}: {error.GetType().Name}: {error.Message}");
                double idle = Seconds(now - Volatile.Read(ref s.LastProgress));
                if (idle > StallLimit.TotalSeconds && s.Error is null)
                    problems.Add($"{s.Name} made no progress for {idle:F1} s, stuck in {s.Phase}");
            }

            if (now >= nextReport)
            {
                foreach (var s in streams)
                    Log(s.Report(Seconds(now - start)));
                nextReport += Ticks(ReportEvery);
            }
        }

        control.Stop = true;
        foreach (var s in streams)
        {
            if (!s.Done.Wait(StallLimit * 2))
                problems.Add($"{s.Name} did not stop within {StallLimit.TotalSeconds * 2} s, stuck in {s.Phase}; its converter is left undisposed");
        }

        double elapsed = Seconds(Stopwatch.GetTimestamp() - start);
        foreach (var s in streams)
        {
            Log(s.Report(elapsed));
            Log($"{s.Name} upscaled by the driver on {s.Engagement.Count(e => e > 0.1)} of {s.Engagement.Count} checks");
        }

        if (!streams.Any(s => s.Engagement.Any(e => e > 0.1)))
            problems.Add("no stream was upscaled by the driver, so the run did not exercise super resolution");

        Log(problems.Count == 0 ? $"PASS after {elapsed:F0} s" : "FAIL: " + string.Join("; ", problems));
        foreach (var s in streams.Where(s => s.Done.IsSet))
            s.Dispose();

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static void Run(SoakStream s, SoakControl control)
    {
        try
        {
            long i = 0;
            long nextCheck = Stopwatch.GetTimestamp();
            long nextSlot = Stopwatch.GetTimestamp();
            bool injected = false;
            while (!control.Stop)
            {
                if (control.InjectStall == s.Name && !injected && Seconds(Stopwatch.GetTimestamp() - control.Start) > 5)
                {
                    injected = true;
                    s.Phase = Phase.Injected;
                    Thread.Sleep(StallLimit * 2);
                }

                int index = (int)(i % D3D11Nv12SharedConverter.BufferCount);
                bool check = Stopwatch.GetTimestamp() >= nextCheck;
                var frame = (GpuVideoFrame)s.Frames[check ? 0 : (int)(i % s.Frames.Count)];
                Assert.True(frame.TryGetD3D11Texture(out nint texture, out int slice, out _));

                s.Phase = Phase.Blit;
                long t0 = Stopwatch.GetTimestamp();
                if (!s.Converter.ConvertInto(index, texture, slice))
                    throw new InvalidOperationException("ConvertInto reported device loss");
                if (s.Converter.SuperResolutionFailed)
                    throw new InvalidOperationException("VideoProcessorBlt failed and the converter fell back to the shader");
                long t1 = Stopwatch.GetTimestamp();

                s.Phase = Phase.Consume;
                if (check)
                {
                    s.AddEngagement(Difference(s.Reference, s.Reader.Read(index)));
                    nextCheck = Stopwatch.GetTimestamp() + Ticks(CheckEvery);
                }
                else
                {
                    s.Reader.Consume(index);
                }

                long t2 = Stopwatch.GetTimestamp();

                long t3 = t2;
                if (index == D3D11Nv12SharedConverter.BufferCount - 1)
                {
                    s.Phase = Phase.Drain;
                    if (!s.Converter.WaitForSubmittedBlits(StallLimit))
                        throw new TimeoutException($"the ring's blits did not complete in {StallLimit.TotalSeconds} s");
                    t3 = Stopwatch.GetTimestamp();
                }

                s.Record(t1 - t0, t2 - t1, index == D3D11Nv12SharedConverter.BufferCount - 1 ? t3 - t2 : -1, t3 - t0);
                s.Phase = Phase.Idle;
                Volatile.Write(ref s.LastProgress, Stopwatch.GetTimestamp());
                i++;

                if (control.Fps > 0)
                {
                    nextSlot += (long)(Stopwatch.Frequency / control.Fps);
                    long wait = nextSlot - Stopwatch.GetTimestamp();
                    if (wait > 0)
                        Thread.Sleep(TimeSpan.FromSeconds(Seconds(wait)));
                    else
                        nextSlot = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (Exception ex)
        {
            s.Error = ex;
        }
        finally
        {
            s.Done.Set();
        }
    }

    private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    private static double Seconds(long ticks) => (double)ticks / Stopwatch.Frequency;

    /// <summary>Mean absolute difference over the colour channels, in levels of 255.</summary>
    private static double Difference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        long total = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (i % 4 != 3)
                total += Math.Abs(a[i] - b[i]);
        }

        return total / (a.Length * 0.75);
    }

    private enum Phase
    {
        Idle,
        Blit,
        Consume,
        Drain,
        Injected,
    }

    private sealed class SoakControl(string? injectStall, double fps)
    {
        public string? InjectStall { get; } = injectStall;
        public double Fps { get; } = fps;
        public long Start;
        public volatile bool Stop;
    }

    /// <summary>One view's worth of the presenter: a decoder's frames, a converter, a compositor-side reader.</summary>
    private sealed class SoakStream : IDisposable
    {
        private const int Window = 8192;
        private readonly object _gate = new();
        private readonly double[] _iterations = new double[Window];
        private readonly double[] _drains = new double[Window];
        private long _count;
        private long _drainCount;
        private double _maxBlit;
        private double _maxConsume;
        private double _maxDrain;
        private double _maxIteration;
        private readonly List<double> _engagement = [];

        private SoakStream(string name, List<IVideoFrame> frames, D3D11Nv12SharedConverter converter, RingReader reader, byte[] reference, string adapter, nint decodeDevice)
        {
            Name = name;
            Frames = frames;
            Converter = converter;
            Reader = reader;
            Reference = reference;
            Adapter = adapter;
            DecodeDevice = decodeDevice;
        }

        public string Name { get; }
        public List<IVideoFrame> Frames { get; }
        public D3D11Nv12SharedConverter Converter { get; }
        public RingReader Reader { get; }
        public byte[] Reference { get; }
        public string Adapter { get; }
        public nint DecodeDevice { get; }
        public int Width => Frames[0].Width;
        public int Height => Frames[0].Height;
        public List<double> Engagement { get { lock (_gate) return [.. _engagement]; } }
        public ManualResetEventSlim Done { get; } = new();
        public long LastProgress;
        public volatile Exception? Error;
        public volatile Phase Phase;

        public static async Task<SoakStream> OpenAsync(string name, PresenterOutput output)
        {
            // One frame: FFmpeg's D3D11VA pool is fixed-size, and the helper's decoder has no spare
            // surfaces for frames the test keeps.
            var frames = await Decode.FramesAsync(FullHd, HardwareDecodeBackendKind.D3D11Va, yieldHardware: true, count: 1);
            var first = (GpuVideoFrame)frames[0];
            Assert.True(first.TryGetD3D11Texture(out nint texture, out int slice, out _));

            // What plain video-processor scaling makes of frame 0, for the engagement checks.
            byte[] reference;
            using (var plain = new D3D11Nv12SharedConverter(texture, first.Width, first.Height, output, NullLogger.Instance, superResolutionExtension: false))
            using (var plainReader = new RingReader(texture, plain, output))
            {
                Assert.True(plain.ConvertInto(0, texture, slice));
                Assert.False(plain.SuperResolutionFailed);
                reference = plainReader.Read(0);
            }

            var converter = new D3D11Nv12SharedConverter(texture, first.Width, first.Height, output, NullLogger.Instance, superResolutionExtension: true);
            Assert.False(converter.SuperResolutionFailed, "the video processor failed to set up");
            Assert.Equal(output, converter.Output);
            var reader = new RingReader(texture, converter, output);
            return new SoakStream(name, frames, converter, reader, reference, reader.AdapterDescription, reader.DecodeDevicePointer);
        }

        public void AddEngagement(double mean)
        {
            lock (_gate)
                _engagement.Add(mean);
        }

        public void Record(long blit, long consume, long drain, long iteration)
        {
            lock (_gate)
            {
                _maxBlit = Math.Max(_maxBlit, Ms(blit));
                _maxConsume = Math.Max(_maxConsume, Ms(consume));
                _maxIteration = Math.Max(_maxIteration, Ms(iteration));
                _iterations[_count++ % Window] = Ms(iteration);
                if (drain >= 0)
                {
                    _maxDrain = Math.Max(_maxDrain, Ms(drain));
                    _drains[_drainCount++ % Window] = Ms(drain);
                }
            }
        }

        public string Report(double elapsedSeconds)
        {
            lock (_gate)
            {
                var iterations = _iterations.Take((int)Math.Min(_count, Window)).Order().ToArray();
                var drains = _drains.Take((int)Math.Min(_drainCount, Window)).Order().ToArray();
                string last = _engagement.Count == 0 ? "-" : _engagement[^1].ToString("F3", CultureInfo.InvariantCulture);
                string min = _engagement.Count == 0 ? "-" : _engagement.Min().ToString("F3", CultureInfo.InvariantCulture);
                return string.Concat(
                    Inv($"{Name} t={elapsedSeconds:F0}s blits={_count} ({_count / Math.Max(elapsedSeconds, 1):F0}/s) "),
                    Inv($"iteration p50 {P(iterations, 0.5):F2} p99 {P(iterations, 0.99):F2} max {_maxIteration:F2} ms; "),
                    Inv($"ring drain p50 {P(drains, 0.5):F2} p99 {P(drains, 0.99):F2} max {_maxDrain:F2} ms; "),
                    Inv($"blit call max {_maxBlit:F2} ms; consume max {_maxConsume:F2} ms; "),
                    $"VSR checks {_engagement.Count}, last mean {last}, min {min}",
                    Error is null ? "" : $"; ERROR {Error.Message}");
            }

            static string Inv(FormattableString text) => FormattableString.Invariant(text);

            static double P(double[] sorted, double q) => sorted.Length == 0 ? 0 : sorted[(int)Math.Min(sorted.Length - 1, q * sorted.Length)];
        }

        public void Dispose()
        {
            Reader.Dispose();
            Converter.Dispose();
            foreach (var frame in Frames)
                frame.Dispose();
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>
    /// The compositor's side of one converter's ring, on its own device on the decoder's adapter:
    /// takes key 1, copies the buffer as a draw from it would read it, and hands key 0 back.
    /// </summary>
    private sealed class RingReader : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly ID3D11Texture2D[] _ring;
        private readonly IDXGIKeyedMutex[] _mutexes;
        private readonly ID3D11Texture2D _target;
        private readonly ID3D11Texture2D _staging;
        private readonly int _width;
        private readonly int _height;

        public RingReader(nint decodeTexture, D3D11Nv12SharedConverter converter, PresenterOutput output)
        {
            _width = output.Width;
            _height = output.Height;
            Marshal.AddRef(decodeTexture);
            using var texture = new ID3D11Texture2D(decodeTexture);
            DecodeDevicePointer = texture.Device.NativePointer;
            using var dxgi = texture.Device.QueryInterface<IDXGIDevice>();
            dxgi.GetAdapter(out var adapter).CheckError();
            using (adapter)
            {
                AdapterDescription = adapter.Description.Description;
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null, out ID3D11Device? device)
                    .CheckError();
                _device = device!;
            }

            _context = _device.ImmediateContext;
            _ring = new ID3D11Texture2D[D3D11Nv12SharedConverter.BufferCount];
            _mutexes = new IDXGIKeyedMutex[_ring.Length];
            for (int i = 0; i < _ring.Length; i++)
            {
                _ring[i] = _device.OpenSharedResource<ID3D11Texture2D>(converter.GetSharedHandle(i));
                _mutexes[i] = _ring[i].QueryInterface<IDXGIKeyedMutex>();
            }

            var description = new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
            };
            _target = _device.CreateTexture2D(description);
            _staging = _device.CreateTexture2D(description with { Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read });
        }

        public string AdapterDescription { get; }

        public nint DecodeDevicePointer { get; }

        public void Consume(int index)
        {
            Acquire(index);
            _context.CopyResource(_target, _ring[index]);
            _mutexes[index].ReleaseSync(0);
            _context.Flush();
        }

        public unsafe byte[] Read(int index)
        {
            Acquire(index);
            _context.CopyResource(_staging, _ring[index]);
            _mutexes[index].ReleaseSync(0);

            var mapped = _context.Map(_staging, 0, MapMode.Read);
            try
            {
                var pixels = new byte[_width * _height * 4];
                for (int y = 0; y < _height; y++)
                {
                    new ReadOnlySpan<byte>((byte*)mapped.DataPointer + y * mapped.RowPitch, _width * 4)
                        .CopyTo(pixels.AsSpan(y * _width * 4));
                }

                return pixels;
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
        }

        public void Dispose()
        {
            _staging.Dispose();
            _target.Dispose();
            foreach (var mutex in _mutexes)
                mutex.Dispose();
            foreach (var texture in _ring)
                texture.Dispose();
            _context.Dispose();
            _device.Dispose();
        }

        private void Acquire(int index)
        {
            int hr = AcquireSync(_mutexes[index], 1, 5000);
            if (hr != 0)
                throw new TimeoutException($"compositor-side AcquireSync(1) on buffer {index} returned 0x{hr:X8}");
        }

        /// <summary><c>IDXGIKeyedMutex::AcquireSync</c>, vtable slot 8, for its HRESULT.</summary>
        private static unsafe int AcquireSync(IDXGIKeyedMutex mutex, ulong key, uint milliseconds)
        {
            nint* vtable = *(nint**)mutex.NativePointer;
            var acquire = (delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)vtable[8];
            return acquire(mutex.NativePointer, key, milliseconds);
        }
    }
}

/// <summary>
/// Skipped unless <c>FRAMEFLOW_VP_SOAK_MINUTES</c> is set and the machine meets
/// <see cref="RequiresNvidiaD3D11VaFactAttribute"/> with the driver's upscale on.
/// </summary>
internal sealed class RequiresVideoProcessorSoakFactAttribute : FactAttribute
{
    public RequiresVideoProcessorSoakFactAttribute(string clip)
    {
        if (Environment.GetEnvironmentVariable("FRAMEFLOW_VP_SOAK_MINUTES") is null)
        {
            Skip = "Set FRAMEFLOW_VP_SOAK_MINUTES to run the two-converter video processor soak.";
            return;
        }

        Skip = new RequiresNvidiaD3D11VaFactAttribute(clip, driverUpscale: true).Skip;
    }
}
