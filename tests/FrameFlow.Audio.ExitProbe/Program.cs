// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

// Activates an OpenAL sink on the real device and returns from Main, so a test can read how the
// process ends (#633). The modes:
//   leak     activate, and return without disposing the sink
//   playing  activate, start the source on silence, and return without disposing the sink
//   dispose  activate and dispose, the control
// It prints "device=yes" once it has a device, or "device=no" when none could be opened, so a
// clean exit is not mistaken for one that never reached the device.

using FrameFlow.Audio.OpenAL;
using FrameFlow.Media;

if (args is not [("leak" or "playing" or "dispose") and var mode])
{
    Console.Error.WriteLine("usage: FrameFlow.Audio.ExitProbe leak|playing|dispose");
    return 2;
}

var sink = new OpenAlAudioSink();
await sink.ActivateAsync();

// Silence, so nothing is heard. Eight 50 ms blocks pass the sink's pre-buffer depth, so in the
// playing mode the source is playing when the process ends.
var blocks = mode == "playing" ? 8 : 1;
for (int i = 0; i < blocks; i++)
{
    await sink.PresentAsync(
        PcmAudioBuffer.Create(
            4800,
            48000,
            2,
            TimeSpan.FromMilliseconds(50 * i),
            0,
            static (span, _) =>
            {
                span.Clear();
                return span.Length;
            }
        ),
        CancellationToken.None
    );
}

// The sink counts a block only once it holds a device to write it to.
Console.WriteLine(sink.BlocksWritten > 0 ? "device=yes" : "device=no");

if (mode == "dispose")
    await sink.DisposeAsync();

return 0;
