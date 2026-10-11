using FrameFlow.Examples.Multicast.Shared;
using FrameFlow.Inference;
using FrameFlow.Inference.Cuda;

namespace FrameFlow.Examples.Multicast;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        MulticastApp.Run(args, () => new PlayerWindow
        {
            // CUDA first, and the CPU when there is no NVIDIA GPU.
            Inference = InferenceSessionFactoryBuilder.Create(
                ExecutionProvider.Cuda,
                new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
                {
                    [ExecutionProvider.Cuda] = path => new CudaInferenceSession(path),
                    [ExecutionProvider.Cpu] = path => new CpuInferenceSession(path),
                }
            ),
            StartupFile = args.FirstOrDefault(),
            LogFileName = "avalonia-multicast.log",
        });
}
