using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// A D3D11 device on the decoder's adapter that opens a ring buffer by its shared handle, as the
/// compositor does, takes key 1, copies it out and hands key 0 back.
/// </summary>
internal sealed class D3D11CompositorSide : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    public D3D11CompositorSide(nint decodeTexture)
    {
        Marshal.AddRef(decodeTexture);
        using var texture = new ID3D11Texture2D(decodeTexture);
        using var dxgi = texture.Device.QueryInterface<IDXGIDevice>();
        dxgi.GetAdapter(out var adapter).CheckError();
        using (adapter)
        {
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null, out ID3D11Device? device)
                .CheckError();
            _device = device!;
        }

        _context = _device.ImmediateContext;
    }

    public unsafe byte[] Read(nint sharedHandle, int width, int height)
    {
        using var shown = _device.OpenSharedResource<ID3D11Texture2D>(sharedHandle);
        using var mutex = shown.QueryInterface<IDXGIKeyedMutex>();
        using var staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        // WAIT_TIMEOUT is a success code, which Vortice's AcquireSync does not surface.
        Assert.Equal(0, AcquireSync(mutex, 1, 5000));
        _context.CopyResource(staging, shown);
        mutex.ReleaseSync(0);

        var mapped = _context.Map(staging, 0, MapMode.Read);
        try
        {
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                new ReadOnlySpan<byte>((byte*)mapped.DataPointer + y * mapped.RowPitch, width * 4)
                    .CopyTo(pixels.AsSpan(y * width * 4));
            }

            return pixels;
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        _device.Dispose();
    }

    /// <summary><c>IDXGIKeyedMutex::AcquireSync</c>, vtable slot 8, for its HRESULT.</summary>
    private static unsafe int AcquireSync(IDXGIKeyedMutex mutex, ulong key, uint milliseconds)
    {
        nint* vtable = *(nint**)mutex.NativePointer;
        var acquire = (delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)vtable[8];
        return acquire(mutex.NativePointer, key, milliseconds);
    }
}
