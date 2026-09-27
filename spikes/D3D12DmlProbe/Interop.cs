using System.Reflection;
using System.Runtime.InteropServices;
using FrameFlow.Decoding;
using Microsoft.ML.OnnxRuntime;

namespace D3D12DmlProbe;

/// <summary>What FFmpeg's <c>AVD3D12VAFrame</c> holds for one decoded frame.</summary>
/// <param name="Texture">The <c>ID3D12Resource*</c> the frame was decoded into.</param>
/// <param name="Subresource">The subresource index; 0 unless the pool is a texture array.</param>
/// <param name="Fence">The <c>ID3D12Fence*</c> the decoder signals when the frame is written.</param>
/// <param name="FenceValue">The value the fence reaches when the frame is ready.</param>
internal readonly record struct D3D12FrameHandles(nint Texture, int Subresource, nint Fence, ulong FenceValue)
{
    /// <summary>Reads the frame's D3D12 handles through <see cref="GpuVideoFrame.TryGetD3D12Texture"/>.</summary>
    public static D3D12FrameHandles Read(GpuVideoFrame frame) =>
        frame.TryGetD3D12Texture(out nint texture, out int subresource, out nint fence, out ulong fenceValue)
            ? new D3D12FrameHandles(texture, subresource, fence, fenceValue)
            : throw new InvalidOperationException($"A {frame.Backend} frame has no D3D12 texture.");
}

/// <summary>
/// The parts of ORT's C API the managed binding does not expose: the DirectML provider table,
/// for a caller-owned device and queue and for binding a D3D12 resource as a tensor.
/// </summary>
/// <remarks>
/// Indices come from the 1.24.4 headers: <c>OrtApi</c> entry 195 is
/// <c>GetExecutionProviderApi</c>, 2 is <c>GetErrorMessage</c>, 93 is <c>ReleaseStatus</c>.
/// <c>OrtDmlApi</c> is <c>{ _DML, _DML1, CreateGPUAllocationFromD3DResource,
/// FreeGPUAllocation, GetD3D12ResourceFromAllocation, ... }</c>.
/// </remarks>
internal static unsafe class OrtDml
{
    private const uint ApiVersion = 24;
    private static nint* _api;
    private static nint* _dml;

    [DllImport("onnxruntime", ExactSpelling = true)]
    private static extern nint OrtGetApiBase();

    public static string Initialize()
    {
        nint* apiBase = (nint*)OrtGetApiBase();
        var getApi = (delegate* unmanaged<uint, nint*>)apiBase[0];
        var getVersion = (delegate* unmanaged<nint>)apiBase[1];
        _api = getApi(ApiVersion);
        if (_api is null)
            throw new InvalidOperationException($"ORT does not serve API version {ApiVersion}.");

        var getProviderApi = (delegate* unmanaged<byte*, uint, nint**, nint>)_api[195];
        nint* dml = null;
        fixed (byte* name = "DML\0"u8)
            Check(getProviderApi(name, ApiVersion, &dml));
        _dml = dml;
        return Marshal.PtrToStringUTF8(getVersion()) ?? "?";
    }

    /// <summary><c>SessionOptionsAppendExecutionProvider_DML1</c>: DirectML on a device and queue we own.</summary>
    public static void AppendDirectML(SessionOptions options, nint dmlDevice, nint commandQueue) =>
        Check(((delegate* unmanaged<nint, nint, nint, nint>)_dml[1])(
            options.DangerousGetHandle(), dmlDevice, commandQueue));

    /// <summary><c>CreateGPUAllocationFromD3DResource</c>: a D3D12 buffer as DirectML tensor memory.</summary>
    public static nint CreateAllocation(nint resource)
    {
        nint allocation = 0;
        Check(((delegate* unmanaged<nint, nint*, nint>)_dml[2])(resource, &allocation));
        return allocation;
    }

    public static void FreeAllocation(nint allocation) =>
        Check(((delegate* unmanaged<nint, nint>)_dml[3])(allocation));

    private static void Check(nint status)
    {
        if (status == 0)
            return;
        string message = Marshal.PtrToStringUTF8(((delegate* unmanaged<nint, nint>)_api[2])(status)) ?? "?";
        ((delegate* unmanaged<nint, void>)_api[93])(status);
        throw new InvalidOperationException(message);
    }
}

/// <summary>DirectML device creation, as DmlTdrProbe does it.</summary>
internal static class DirectML
{
    private const uint FeatureLevel5_0 = 0x5000;
    private static readonly Guid IidIdmlDevice = new("6dbd6437-96fd-423f-a98c-ae5e7c2a573f");

    [DllImport("DirectML.dll", ExactSpelling = true)]
    private static extern int DMLCreateDevice1(nint d3d12Device, uint flags, uint minimumFeatureLevel, in Guid riid, out nint device);

    public static nint CreateDevice(nint d3d12Device)
    {
        int hr = DMLCreateDevice1(d3d12Device, 0, FeatureLevel5_0, IidIdmlDevice, out nint device);
        Marshal.ThrowExceptionForHR(hr);
        return device;
    }
}

/// <summary>The three FFmpeg calls that feed a decoder packets, as the decoding tests do.</summary>
internal static class Packets
{
    [DllImport("avcodec-63", ExactSpelling = true)]
    private static extern nint av_packet_alloc();

    [DllImport("avcodec-63", ExactSpelling = true)]
    private static extern void av_packet_free(ref nint packet);

    [DllImport("avcodec-63", ExactSpelling = true)]
    private static extern int av_packet_ref(nint destination, nint source);

    [DllImport("avcodec-63", ExactSpelling = true)]
    private static extern void av_packet_unref(nint packet);

    [DllImport("avformat-63", ExactSpelling = true)]
    private static extern int av_read_frame(nint formatContext, nint packet);

    /// <summary>Queues every packet of <paramref name="stream"/> and completes the queue.</summary>
    public static async Task QueueAllAsync(nint formatContext, int stream, VideoDecoder decoder)
    {
        nint read = av_packet_alloc();
        try
        {
            while (av_read_frame(formatContext, read) >= 0)
            {
                if (StreamIndex(read) == stream)
                {
                    nint clone = av_packet_alloc();
                    av_packet_ref(clone, read);
                    await decoder.SendPacketAsync(clone);
                }

                av_packet_unref(read);
            }
        }
        finally
        {
            av_packet_free(ref read);
        }

        decoder.CompletePacketQueue();
    }

    /// <summary><c>AVPacket.stream_index</c>, at offset 36: buf, pts, dts, data and size precede it.</summary>
    private static unsafe int StreamIndex(nint packet) => *(int*)(packet + 36);
}

/// <summary>Reads a hardware frame's NV12 samples on the CPU, for diagnosing the shader only.</summary>
internal static unsafe class Nv12Readback
{
    [DllImport("avutil-61", ExactSpelling = true)]
    private static extern nint av_frame_alloc();

    [DllImport("avutil-61", ExactSpelling = true)]
    private static extern void av_frame_free(ref nint frame);

    [DllImport("avutil-61", ExactSpelling = true)]
    private static extern int av_hwframe_transfer_data(nint destination, nint source, int flags);

    private static readonly System.Reflection.PropertyInfo NativeAvFrame = typeof(GpuVideoFrame).GetProperty(
        "NativeAvFrame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    /// <summary>The Y, U and V samples at luma pixel (<paramref name="x"/>, <paramref name="y"/>) and its 2x2 chroma block.</summary>
    /// <remarks><c>AVFrame.data[8]</c> is at offset 0 and <c>linesize[8]</c> at 64.</remarks>
    public static (byte Y, byte U, byte V) Sample(GpuVideoFrame frame, int x, int y)
    {
        nint source = (nint)NativeAvFrame.GetValue(frame)!;
        nint cpu = av_frame_alloc();
        try
        {
            int hr = av_hwframe_transfer_data(cpu, source, 0);
            if (hr < 0)
                throw new InvalidOperationException($"av_hwframe_transfer_data failed: {hr}");
            byte** data = (byte**)cpu;
            int* linesize = (int*)(cpu + 64);
            byte luma = data[0][y * linesize[0] + x];
            byte* chroma = data[1] + (y / 2) * linesize[1] + (x / 2) * 2;
            return (luma, chroma[0], chroma[1]);
        }
        finally
        {
            av_frame_free(ref cpu);
        }
    }
}
