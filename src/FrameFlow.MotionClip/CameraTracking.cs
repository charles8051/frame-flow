// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Camera;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Periphery;
using Periphery.Camera;

namespace FrameFlow.MotionClip;

/// <summary>
/// Camera discovery + resilient tracking, built on Periphery's device layer —
/// the same lifecycle model a production kiosk's camera services use, scaled down
/// to a single camera and a single consumer (the recorder).
/// </summary>
/// <remarks>
/// The recorder tracks a camera by a <see cref="DeviceProfile"/> (category +
/// optional Id prefix). <see cref="DeviceSessionHost{TSession}"/> starts
/// regardless of whether the camera is plugged in, opens a session when a
/// matching device appears, tears it down on unplug, and reopens on replug —
/// so "start now, plug in later, it just works" falls out of the library.
/// </remarks>
internal static class CameraTracking
{
    /// <summary>Upper bound requested from the camera; it picks the best ≤ this.</summary>
    public const int CameraMaxWidth = 1280;
    public const int CameraMaxHeight = 720;

    /// <summary>
    /// <c>scan</c>: enumerate cameras and print Id + name. No FFmpeg bootstrap
    /// needed — enumeration is pure Periphery.
    /// </summary>
    public static async Task<int> ScanAsync(CancellationToken ct)
    {
        IReadOnlyList<DeviceInfo> devices;
        try
        {
            devices = await CameraDevice.EnumerateAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Camera enumeration failed: {ex.Message}");
            return 1;
        }

        if (devices.Count == 0)
        {
            Console.WriteLine("No cameras found.");
            return 0;
        }

        Console.WriteLine($"{devices.Count} camera(s):");
        Console.WriteLine();
        for (int i = 0; i < devices.Count; i++)
        {
            DeviceInfo d = devices[i];
            Console.WriteLine($"  [{i}] {d.Name ?? "(unnamed)"}");
            Console.WriteLine($"      Id: {d.Id}");
        }
        Console.WriteLine();
        Console.WriteLine(
            "Track one with:  motionclip run --IdStartsWith \"<id-or-prefix>\"   "
                + "(a prefix is enough; quote it)."
        );
        return 0;
    }

    /// <summary>
    /// Builds the camera-scoped <see cref="DeviceProfile"/> the host tracks.
    /// Precedence: <paramref name="idStartsWith"/> → <paramref name="cameraIndex"/>
    /// (resolved to an Id now) → first available camera (category only).
    /// </summary>
    public static async Task<DeviceProfile> BuildProfileAsync(
        string? idStartsWith,
        int? cameraIndex,
        ILogger logger,
        CancellationToken ct
    )
    {
        if (!string.IsNullOrWhiteSpace(idStartsWith))
        {
            logger.LogInformation(
                "Tracking camera with Id starting with '{Prefix}'.",
                idStartsWith
            );
            string prefix = idStartsWith;
            return new DeviceProfile(
                f =>
                {
                    f.OfCategory(DeviceCategory.Camera);
                    f.WithIdStartsWith(prefix);
                },
                name: "motionclip-camera"
            );
        }

        if (cameraIndex is int idx)
        {
            // Index is a "present now" convenience — resolve it to a stable Id so
            // the tracker still follows that device through disconnect/reconnect.
            try
            {
                IReadOnlyList<DeviceInfo> devices = await CameraDevice
                    .EnumerateAsync(ct)
                    .ConfigureAwait(false);
                if (idx >= 0 && idx < devices.Count)
                {
                    DeviceInfo dev = devices[idx];
                    logger.LogInformation(
                        "Tracking camera [{Index}] {Name} (Id={Id}).",
                        idx,
                        dev.Name ?? "(unnamed)",
                        dev.Id
                    );
                    string id = dev.Id;
                    return new DeviceProfile(
                        f =>
                        {
                            f.OfCategory(DeviceCategory.Camera);
                            f.WithId(id);
                        },
                        name: "motionclip-camera"
                    );
                }

                logger.LogWarning(
                    "Camera index {Index} out of range ({Count} found) — tracking the "
                        + "first available camera instead.",
                    idx,
                    devices.Count
                );
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Enumerating cameras for --camera {Index} failed — tracking the first "
                        + "available camera instead.",
                    idx
                );
            }
        }

        logger.LogInformation("Tracking the first available camera.");
        return new DeviceProfile(f => f.OfCategory(DeviceCategory.Camera), name: "motionclip-camera");
    }

    /// <summary>
    /// Starts the resilient camera host. Returns immediately; the host drives
    /// sessions in the background, running the recorder graph for the lifetime of
    /// each connection. The caller owns the host (dispose it to stop). The
    /// <paramref name="recorder"/> and <paramref name="motion"/> live outside the
    /// host, so clip count accumulates across reconnects; the motion detector's
    /// reference frame is reset at the start of each session so a replug doesn't
    /// fire a spurious "motion" event.
    /// </summary>
    public static Task<DeviceSessionHost<CameraSession>> StartTrackedHostAsync(
        DeviceProfile profile,
        RecordingGate gate,
        ClipEncoderSink encoderSink,
        IVideoSink? preview,
        int cameraBuffers,
        ILoggerFactory loggerFactory,
        ILogger logger,
        Action? onDisconnected,
        CancellationToken ct
    ) =>
        DeviceSessionHost<CameraSession>.StartAsync(
            profile,
            createSession: (device, c) =>
                CameraSession
                    .For(device)
                    .PreferPixelFormat(CameraPixelFormat.Bgra32)
                    .AllowOnlyPixelFormats(
                        CameraPixelFormat.Bgra32,
                        CameraPixelFormat.Rgba32,
                        CameraPixelFormat.Nv12,
                        CameraPixelFormat.Yuy2,
                        CameraPixelFormat.Uyvy
                    )
                    .MaxResolution(CameraMaxWidth, CameraMaxHeight)
                    // Override Periphery's default pool size (3) when the caller
                    // wants more headroom: under DropIncoming exhaustion, all-
                    // buffers-outstanding-while-encoder-busy is what produces the
                    // "Frame dropped (#N); pool exhausted" warnings. Raising
                    // BufferCount lets the camera ride out a slow encoder burst.
                    .WithSessionOptions(o => o with { BufferCount = cameraBuffers })
                    .WithLogger(loggerFactory.CreateLogger<CameraSession>())
                    .OpenAsync(c),
            onSessionEnded: async _ =>
            {
                logger.LogInformation("Camera disconnected — waiting for it to return.");
                onDisconnected?.Invoke();
                // Finalize any in-progress segment so the gate resets to Idle
                // before reconnect. The graph has already stopped, so the gate
                // can't push the segment through the substrate — hand it to
                // the encoder's worker channel. EnqueueAsync returns as soon
                // as the queue accepts the segment (sub-millisecond unless
                // the encoder is way behind), so the reconnect loop runs
                // immediately instead of blocking on a multi-second flush
                // encode. The worker keeps a refcount on the segment; we
                // release our own ref unconditionally in the finally.
                ClipSegment? pending = gate.Flush();
                if (pending is null)
                    return;
                try
                {
                    await encoderSink
                        .EnqueueAsync(pending, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Flush-on-disconnect enqueue threw.");
                }
                finally
                {
                    pending.Dispose();
                }
            },
            whileSessionActive: async (session, c) =>
            {
                logger.LogInformation(
                    "Camera connected: {Name}. Recording armed.",
                    session.DeviceInfo?.Name ?? "(unnamed)"
                );
                gate.ResetMotion();
                try
                {
                    await using var cam = session.AsPushVideoFrameSource(c, loggerFactory);
                    FrameFlow.Graph.Graph graph = RecorderPipeline.BuildGraph(
                        cam.Source,
                        gate,
                        encoderSink,
                        preview
                    );
                    try
                    {
                        await graph.RunAsync(c).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Clean path: tracker-detected unplug or normal shutdown.
                    }
                    catch (Exception ex)
                    {
                        // Typically the push pump's CameraTimeoutException when an
                        // unplug beats the OS device-removal notification (the
                        // tracker hasn't fired yet, but frames stopped 5 s ago).
                        // Let it propagate so DeviceProxyBase's worker catch
                        // triggers Deactivate + Reconnect.
                        logger.LogWarning(ex, "Camera stream faulted — treating as disconnect.");
                        throw;
                    }
                }
                finally
                {
                    // The host's SessionLease invokes onSessionEnded but does NOT
                    // dispose the session itself, so dispose it here to free the
                    // Media Foundation handle for re-open on replug. Bound it: on
                    // an UNCLEAN unplug, MF can hang inside DisposeAsync forever
                    // waiting on a dead device — better to leak briefly (the OS
                    // releases the handle on its own in seconds) than wedge the
                    // host so the reconnect loop never runs.
                    try
                    {
                        var disposeTask = session.DisposeAsync().AsTask();
                        var done = await Task.WhenAny(disposeTask, Task.Delay(2000, CancellationToken.None))
                            .ConfigureAwait(false);
                        if (done != disposeTask)
                            logger.LogWarning(
                                "Camera session disposal timed out after 2s; abandoning. "
                                    + "Re-open may need a few retries until the OS releases the "
                                    + "MF handle."
                            );
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Camera session disposal threw.");
                    }
                }
            },
            ct: ct
        );
}
