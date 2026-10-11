using FrameFlow.Examples.Multicast.Shared;

namespace FrameFlow.Examples.Camera.Multicast;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => MulticastApp.Run(args, () => new MainWindow());
}
