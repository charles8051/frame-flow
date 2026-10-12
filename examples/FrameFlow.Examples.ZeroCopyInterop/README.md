# FrameFlow.Examples.ZeroCopyInterop

Plays a video file on `CompositionInteropVideoView`, the Windows presenter in
`FrameFlow.Avalonia.Windows`. Hardware-decoded frames go from the decoder to
Avalonia's compositor without leaving the GPU: the view converts each frame to BGRA
on the GPU and hands the texture to the compositor through `ICompositionGpuInterop`,
with no readback and no `WriteableBitmap`.

```csharp
var player = await FrameFlowPlayer.Create()
    .WithMedia(path)
    .WithVideoSurface(VideoView, loggerFactory)
    .WithRepeatMode(RepeatMode.One)
    .BuildPlayerAsync();

var played = await player.PlayAsync();
if (!played.IsSuccess)
    StatusText.Text = $"{played.Error.Category}: {played.Error.Message}";
```

The view's sink takes D3D11 and D3D12 frames, so the player keeps hardware frames on
the GPU without `WithHardwareFrames()`. When hardware decode does not bind, the view
uploads the software-decoded frames instead. The log says which path is live:
`ZERO-COPY PATH LIVE` or `CPU-UPLOAD FALLBACK LIVE`.

The code is in [MainWindow.axaml.cs](MainWindow.axaml.cs).

## Requirements

- Windows 10 or 11.
- A GPU with Direct3D 11 video decode for the file's codec, such as H.264 or HEVC.
- For `--hw-device d3d12va`, a GPU and driver with D3D12 video decode.
- The FFmpeg binaries: `dotnet run scripts/fetch-ffmpeg.cs` once per clone.

The project builds on Linux and macOS, but the view needs Direct3D to run.

## Run

```powershell
dotnet run --project examples/FrameFlow.Examples.ZeroCopyInterop -- <video.mp4> [--hw-device d3d12va]
```

`--hw-device d3d12va` decodes on one D3D12VA device that the player borrows
(`WithHardwareDevice`). The view converts each frame on that device and copies it
across to the compositor's D3D11 side.

Press `S` to toggle the driver's video super resolution; the corner shows its status,
which names the first condition that keeps the view from asking for it. The driver
upscales only 8-bit D3D11VA frames, on an NVIDIA RTX GPU, in a local session, with
Super Resolution on in the NVIDIA App (System, Video, RTX Video Enhancements), and
only while the video is smaller than the window. A failure during playback replaces
the status line.

The log is written to `logs/zero-copy-interop.log` at the repository root.
