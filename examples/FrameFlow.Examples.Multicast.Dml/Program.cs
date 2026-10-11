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
            // DirectML runs on any DirectX 12 GPU; the CPU when there is none.
            Inference = InferenceSessionFactoryBuilder.Create(
                ExecutionProvider.DirectML,
                new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
                {
                    [ExecutionProvider.DirectML] = path => new DmlInferenceSession(path),
                    [ExecutionProvider.Cpu] = path => new CpuInferenceSession(path),
                }
            ),
            StartupFile = args.FirstOrDefault(),
            LogFileName = "avalonia-multicast-dml.log",
        });
}
