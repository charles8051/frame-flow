// Can Avalonia's compositor present a texture that Direct3D 12 wrote (#429)?
//
// The probe asks the compositor what it imports, then creates a BGRA texture in a shared heap on
// the compositor's own adapter, clears it to a known colour on a D3D12 queue, and signals a shared
// D3D12 fence. Then one of two hand-offs:
//
//   direct  The texture is imported as a D3D11 NT handle and the fence as a D3D12 fence semaphore,
//           and presented with UpdateWithTimelineSemaphoresAsync. No D3D11 device of ours.
//   bridge  A D3D11 device of ours opens the texture and the fence, waits on the fence on the GPU,
//           and copies into a keyed-mutex texture of its own, which is presented with
//           UpdateWithKeyedMutexAsync, as the D3D11VA presenter does today.
//
// Evidence is the screen showing the colour under the control, and for direct, the fence reaching
// the value the compositor was asked to signal.
//
//   dotnet run --project spikes/D3D12PresenterProbe -c Release -- direct|bridge
//
// Writes its findings to stdout and to probe-result-<mode>.txt, then closes.

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Themes.Fluent;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace D3D12PresenterProbe;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();
        // A second argument of "vulkan" asks for Avalonia's Vulkan renderer instead of ANGLE.
        if (args is [_, "vulkan", ..])
            builder = builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Vulkan] });
        builder.StartWithClassicDesktopLifetime(args);
    }
}

internal sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window
            {
                Title = "D3D12 presenter probe",
                Width = 360,
                Height = 360,
                // Blue, so a pixel outside the texture shows the screen read works.
                Background = Avalonia.Media.Brushes.Blue,
                Content = new ProbeControl(desktop, desktop.Args is [var mode, ..] ? mode : "direct"),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class ProbeControl(IClassicDesktopStyleApplicationLifetime desktop, string mode) : Control
{
    private const int Size = 256;

    // Orange: R 255, G 64, B 0.
    private static readonly Color4 Clear = new(1f, 64f / 255f, 0f, 1f);

    private readonly List<string> _log = [];
    private bool _started;

    // After layout, so the control has its size.
    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_started)
            return;
        _started = true;
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            await ProbeAsync();
        }
        catch (Exception ex)
        {
            Log($"FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            File.WriteAllLines($"probe-result-{mode}.txt", _log);
            desktop.Shutdown();
        }
    }

    private async Task ProbeAsync()
    {
        var compositor = ElementComposition.GetElementVisual(this)!.Compositor;
        var interop = await compositor.TryGetCompositionGpuInterop();
        if (interop is null)
        {
            Log("ICompositionGpuInterop: not available on this backend");
            return;
        }

        Log($"mode: {mode}");
        Log($"image handle types: [{string.Join(", ", interop.SupportedImageHandleTypes)}]");
        Log($"semaphore types: [{string.Join(", ", interop.SupportedSemaphoreTypes)}]");
        foreach (var type in interop.SupportedImageHandleTypes)
            Log($"sync for {type}: {interop.GetSynchronizationCapabilities(type)}");
        Log($"device LUID: {(interop.DeviceLuid is { } l ? Convert.ToHexString(l) : "none")}");

        // The compositor's adapter, so the shared texture needs no cross-adapter copy.
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
        var luidBytes = interop.DeviceLuid ?? throw new InvalidOperationException("The compositor reports no adapter LUID.");
        var luid = new Vortice.Luid(BitConverter.ToUInt32(luidBytes, 0), BitConverter.ToInt32(luidBytes, 4));
        using var adapter = factory.EnumAdapterByLuid<IDXGIAdapter1>(luid);
        Log($"adapter: {adapter.Description1.Description}");
        using var device = D3D12.D3D12CreateDevice<ID3D12Device>(adapter, Vortice.Direct3D.FeatureLevel.Level_11_0);

        using var texture = device.CreateCommittedResource(
            new HeapProperties(HeapType.Default),
            HeapFlags.Shared,
            ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, Size, Size, 1, 1, flags: ResourceFlags.AllowRenderTarget),
            ResourceStates.Common,
            new ClearValue(Format.B8G8R8A8_UNorm, Clear));
        using var fence = device.CreateFence(0, Vortice.Direct3D12.FenceFlags.Shared);
        using var queue = device.CreateCommandQueue(CommandListType.Direct);
        using var allocator = device.CreateCommandAllocator(CommandListType.Direct);
        using var rtvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
        var rtv = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        device.CreateRenderTargetView(texture, null, rtv);

        using var list = device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, allocator);
        list.ResourceBarrierTransition(texture, ResourceStates.Common, ResourceStates.RenderTarget);
        list.ClearRenderTargetView(rtv, Clear);
        list.ResourceBarrierTransition(texture, ResourceStates.RenderTarget, ResourceStates.Common);
        list.Close();
        queue.ExecuteCommandList(list);
        queue.Signal(fence, 1);

        // Vortice's overload fills in the access rights.
        nint textureHandle = device.CreateSharedHandle(texture, null, null);
        nint fenceHandle = device.CreateSharedHandle(fence, null, null);
        Log("D3D12 texture and fence created shared, texture cleared, fence signalled to 1");

        var surface = compositor.CreateDrawingSurface();
        var visual = compositor.CreateSurfaceVisual();
        visual.Surface = surface;
        visual.Size = new Vector(Size, Size);
        ElementComposition.SetElementChildVisual(this, visual);

        bool compositorSignalled = true;
        if (mode == "bridge")
            await BridgeAsync(interop, surface, adapter, textureHandle, fenceHandle);
        else
            compositorSignalled = await DirectAsync(interop, surface, fence, textureHandle, fenceHandle);

        // Give the compositor a few frames to show it, then read the screen under the control.
        await Task.Delay(500);
        var (br, bg, bb) = ScreenRgb(this.PointToScreen(new Point(Bounds.Width - 20, Bounds.Height - 20)));
        Log($"screen pixel outside the texture: R {br} G {bg} B {bb} (expected the window's blue, 0, 0, 255)");
        var (r, g, b) = ScreenRgb(this.PointToScreen(new Point(Size / 2.0, Size / 2.0)));
        Log($"screen pixel inside the texture: R {r} G {g} B {b} (expected 255, 64, 0)");
        Log(r == 255 && g == 64 && b == 0 && compositorSignalled ? "VERDICT: presented" : "VERDICT: not presented");

        CloseHandle(textureHandle);
        CloseHandle(fenceHandle);
    }

    private async Task<bool> DirectAsync(
        ICompositionGpuInterop interop, CompositionDrawingSurface surface, ID3D12Fence fence, nint textureHandle, nint fenceHandle)
    {
        var image = interop.ImportImage(
            new PlatformHandle(textureHandle, KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle),
            Properties());
        var semaphore = interop.ImportSemaphore(
            new PlatformHandle(fenceHandle, KnownPlatformGraphicsExternalSemaphoreHandleTypes.Direct3D12FenceNtHandle));
        Log("imported: texture as D3D11TextureNtHandle, fence as Direct3D12FenceNtHandle");

        // Wait for 1 before reading, signal 2 after.
        await surface.UpdateWithTimelineSemaphoresAsync(image, semaphore, 1, semaphore, 2);
        Log("UpdateWithTimelineSemaphoresAsync completed");

        using var signalled = new ManualResetEvent(false);
        fence.SetEventOnCompletion(2, signalled.SafeWaitHandle.DangerousGetHandle());
        bool compositorSignalled = signalled.WaitOne(TimeSpan.FromSeconds(5));
        Log($"fence reached 2, signalled by the compositor: {compositorSignalled} (completed value {fence.CompletedValue})");
        await image.DisposeAsync();
        await semaphore.DisposeAsync();
        return compositorSignalled;
    }

    private async Task BridgeAsync(
        ICompositionGpuInterop interop, CompositionDrawingSurface surface, IDXGIAdapter1 adapter, nint textureHandle, nint fenceHandle)
    {
        D3D11.D3D11CreateDevice(adapter, Vortice.Direct3D.DriverType.Unknown, DeviceCreationFlags.BgraSupport, null, out ID3D11Device? created)
            .CheckError();
        using var device = created!;
        using var device1 = device.QueryInterface<ID3D11Device1>();
        using var device5 = device.QueryInterface<ID3D11Device5>();
        using var context4 = device.ImmediateContext.QueryInterface<ID3D11DeviceContext4>();

        // The D3D12 texture and fence, opened on our D3D11 device.
        using var written = device1.OpenSharedResource1<ID3D11Texture2D>(textureHandle);
        using var fence = device5.OpenSharedFence<ID3D11Fence>(fenceHandle);
        Log("opened on D3D11: the D3D12 texture (NT handle) and the D3D12 fence");

        // A keyed-mutex texture of our own, as the D3D11VA presenter's ring is.
        using var shown = device.CreateTexture2D(new Texture2DDescription
        {
            Width = Size,
            Height = Size,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.SharedKeyedMutex,
        });
        using var mutex = shown.QueryInterface<IDXGIKeyedMutex>();
        nint shownHandle;
        using (var dxgi = shown.QueryInterface<IDXGIResource>())
            shownHandle = dxgi.SharedHandle;

        // GPU-side wait for the D3D12 write, then the copy into the keyed-mutex texture.
        mutex.AcquireSync(0, 1000);
        context4.Wait(fence, 1);
        context4.CopyResource(shown, written);
        context4.Flush();
        mutex.ReleaseSync(1);
        Log("D3D11 waited on the fence, copied into its keyed-mutex texture");

        var image = interop.ImportImage(
            new PlatformHandle(shownHandle, KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle),
            Properties());
        await surface.UpdateWithKeyedMutexAsync(image, 1, 0);
        Log("UpdateWithKeyedMutexAsync completed");
        await image.DisposeAsync();
    }

    private static PlatformGraphicsExternalImageProperties Properties() =>
        new()
        {
            Width = Size,
            Height = Size,
            Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
            TopLeftOrigin = true,
        };

    private void Log(string line)
    {
        _log.Add(line);
        Console.WriteLine(line);
    }

    private static (int R, int G, int B) ScreenRgb(PixelPoint point)
    {
        uint pixel = ScreenPixel(point.X, point.Y);
        return ((int)(pixel & 0xFF), (int)((pixel >> 8) & 0xFF), (int)((pixel >> 16) & 0xFF));
    }

    private static uint ScreenPixel(int x, int y)
    {
        nint dc = GetDC(0);
        try
        {
            return GetPixel(dc, x, y);
        }
        finally
        {
            ReleaseDC(0, dc);
        }
    }

    [DllImport("user32")]
    private static extern nint GetDC(nint window);

    [DllImport("user32")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32")]
    private static extern uint GetPixel(nint dc, int x, int y);

    [DllImport("kernel32")]
    private static extern bool CloseHandle(nint handle);
}
