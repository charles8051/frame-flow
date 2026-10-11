using FrameFlow.Examples.Multicast.Shared;
using FrameFlow.Inference;
using FrameFlow.Inference.Dml;

namespace FrameFlow.Examples.Multicast.Dml;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        MulticastApp.Run(args, () => new PlayerWindow
        {
            GpuProvider = ExecutionProvider.DirectML,
            OpenOnGpu = path => new DmlInferenceSession(path),
            StartupFile = args.FirstOrDefault(),
            LogFileName = "avalonia-multicast-dml.log",
        });
}
