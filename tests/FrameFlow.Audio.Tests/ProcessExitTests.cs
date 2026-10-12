// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// A process that ends with an OpenAL sink it never disposed exits with its own code (#633).
/// </summary>
/// <remarks>
/// <para>
/// Each test starts <c>FrameFlow.Audio.ExitProbe</c>, which activates a sink on the real device
/// and returns from <c>Main</c>, and reads its exit code. Before the shared context closed the
/// device at process exit, the two undisposed modes failed fast on Windows with
/// <c>0xC0000409</c> from <c>ucrtbase.dll</c>, and only the disposing control exited 0.
/// </para>
/// <para>
/// It needs the device, so it is opt-in like the other device tests, and it runs in the
/// "OpenAL device" collection so the child is not competing with another test for the device.
/// The timeout only bounds a probe that hangs.
/// </para>
/// </remarks>
[Collection("OpenAL device")]
public sealed class ProcessExitTests
{
    [RequiresAudioDeviceFact]
    public Task ASinkLeftActive_ExitsCleanly() => AssertExitsCleanlyAsync("leak");

    [RequiresAudioDeviceFact]
    public Task ASinkLeftPlaying_ExitsCleanly() => AssertExitsCleanlyAsync("playing");

    [RequiresAudioDeviceFact]
    public Task ASinkDisposedBeforeExit_ExitsCleanly() => AssertExitsCleanlyAsync("dispose");

    private static async Task AssertExitsCleanlyAsync(string mode)
    {
        var probe = Path.Combine(AppContext.BaseDirectory, "FrameFlow.Audio.ExitProbe.dll");
        Assert.True(File.Exists(probe), $"The exit probe was not built next to the tests: {probe}");

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add(mode);

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(bound.Token);

        var output = await stdout + await stderr;
        Assert.True(output.Contains("device=yes", StringComparison.Ordinal), $"The probe had no device:\n{output}");
        Assert.True(
            process.ExitCode == 0,
            $"The probe ({mode}) exited 0x{process.ExitCode:X8} instead of 0:\n{output}"
        );
    }
}
