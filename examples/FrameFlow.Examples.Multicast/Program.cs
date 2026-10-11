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
            GpuProvider = ExecutionProvider.Cuda,
            OpenOnGpu = path => new CudaInferenceSession(path),
            StartupFile = args.FirstOrDefault(),
            LogFileName = "avalonia-multicast.log",
        });
}
