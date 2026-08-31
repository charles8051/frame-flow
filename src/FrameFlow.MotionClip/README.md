# FrameFlow.MotionClip (ADR-0052)

**MotionClip** is a motion-triggered, pre-roll clip recorder: it continuously
buffers the most recent few seconds of video and, when motion is detected,
writes a clip that starts *before* the motion began. It is the first consumer
of the H.264 → MP4 encoder terminal (ADR-0040 / ADR-0053, `FrameFlow.Encoding`).

It tracks a camera **resiliently** via Periphery's device layer
(`DeviceSessionHost`): the recorder starts whether or not the camera is plugged
in, connects when a matching device appears, and reconnects on replug. By
default it tracks the first camera and shows a **windowed preview**; it also
runs **headless** and against a **synthetic** scene (no camera needed).

## Install

```bash
# As a global dotnet tool — gives you the `motionclip` command:
dotnet tool install -g FrameFlow.MotionClip

# Or use the published self-contained exe + install script (ships in the
# release next to MotionClip.exe; no .NET SDK needed on the target):
#   ./Install-MotionClip.ps1               # → %LOCALAPPDATA%\Programs\MotionClip + PATH
#   ./Install-MotionClip.ps1 -Uninstall
```

Installed, the command is `motionclip` (`motionclip scan`,
`motionclip run --IdStartsWith "…"`). The examples below show the
run-from-source form; swap `dotnet run --project src/FrameFlow.MotionClip --`
for `motionclip` once installed.

## Commands

```bash
# List cameras and their Ids (copy an Id or a prefix for --IdStartsWith):
dotnet run --project src/FrameFlow.MotionClip -- scan

# A command is REQUIRED. A bare invocation prints help (so does -h/--help):
dotnet run --project src/FrameFlow.MotionClip                 # → help
dotnet run --project src/FrameFlow.MotionClip -- --version    # → version

# run (windowed preview, first available camera):
dotnet run --project src/FrameFlow.MotionClip -- run

# Track a specific camera by Id prefix (a prefix is enough; quote it):
dotnet run --project src/FrameFlow.MotionClip -- \
  run --IdStartsWith "USB\VID_046D&PID_085B"

# Headless (no window); synthetic scene (no camera / CI):
dotnet run --project src/FrameFlow.MotionClip -- run --headless
dotnet run --project src/FrameFlow.MotionClip -- run --headless --synthetic

# Bounded autonomous run:
dotnet run --project src/FrameFlow.MotionClip -- \
  run --headless --synthetic --output-dir D:\tmp\clips --exit-after 12
```

Deployed (or on PATH) the command is simply `MotionClip` —
`MotionClip scan`, `MotionClip run --IdStartsWith "…"`.

### Verbs & flags

| | Default | Meaning |
|------|---------|---------|
| `run` | — | Run the recorder. **No default — must be explicit.** |
| `scan` | — | Enumerate cameras and print Id + name, then exit. |
| `-h`, `--help` | — | Print usage and exit. **The default when no command is supplied.** |
| `--version` | — | Print the version and exit. |
| `--IdStartsWith <prefix>` | unset | Track the camera whose Id starts with this prefix. |
| `--camera <index>` | unset | Track the camera at this enumeration index (resolved to its Id). Lower precedence than `--IdStartsWith`. |
| `--synthetic` | off | Use the generated scene instead of a camera. |
| `--headless` | off | Run with no preview window. |
| `--output-dir <dir>` | `./clips` | Where clips are written. |
| `--fps <n>` | `30` | Source/clip frame rate. |
| `--sensitivity <0.0–1.0>` | `0.8` | Motion sensitivity on an intuitive scale. **Higher = more sensitive.** `0.1` ignores all but big movements; `0.8` (default) catches normal activity; `1.0` triggers on tiny twitches. Mapped internally to a changed-pixel ratio: `0.1 → 9 %`, `0.8 → 2 %`, `1.0 → 0.2 %`. |
| `--exit-after <seconds>` | `0` | Self-stop after N seconds (0 = until Ctrl+C / window close). |
| `--log-file <path>` | none | Opt-in file log sink (explicit path). |
| `--log-dir <dir>` | none | Opt-in file log sink into a directory — writes a timestamped `motionclip-<ts>.log`. `--log-file` wins if both are given. |
| `--log-level <level>` | `info` | Minimum log level: `trace`/`debug`/`info`/`warning`/`error`/`critical`/`none`. `debug` surfaces the FFmpeg-bootstrap + per-frame diagnostics. |

Camera selection precedence: `--IdStartsWith` → `--camera <index>` → first
available camera. The recorder **starts regardless** of whether the camera is
present — it waits for a matching device and begins recording when it connects,
so plugging the camera in afterwards "just works."

On Ctrl+C (headless) or window close / `--exit-after` (windowed), an in-progress
recording is drained and saved **before** the process exits, so the final clip
is complete rather than truncated.

## What it demonstrates

- **Resilient device tracking** (`CameraTracking`): a `DeviceProfile`
  (`OfCategory(Camera)` + `WithIdStartsWith`) tracked by a
  `DeviceSessionHost<CameraSession>` — start-regardless, connect-on-plug,
  reconnect-on-replug. The recorder + motion detector live outside the host, so
  clip count accumulates across reconnects.
- **Pre-roll buffer** (`PreRollBuffer`): a capped ring of un-pooled `CloneCpu()`
  copies kept alive outside the pipeline so a trigger can look back in time.
- **In-pipeline motion detection** (`MotionDetector`): a frame-delta tap
  (downsample → grayscale → abs-diff → changed-pixel ratio), run inline in the
  recorder sink (ADR-0052 §4). Its reference frame resets per camera session.
- **Content-driven state machine** (`ClipRecorder`): `Idle → Recording →
  Saving → Idle`, extending the clip on continued motion and writing it on a
  background task.
- **Filesystem side effect**: each clip is encoded to
  `<output-dir>/<timestamp>_clip.mp4` via `Mp4VideoWriter`.

## Pipeline

```
camera (tracked; ≤1280×720)              [or synthetic scene 800×600]
      ↓   session frames via CameraSession.AsPushVideoFrameSource (LatestOnly)
ResizeAndConvert → 640×480 BGRA32
      ↓
recorder-tap SinkNode
   ├── MotionDetector.Process(frame)         (inline, synchronous)
   ├── ClipRecorder.OnFrame(frame, moved)    (pre-roll ring + state machine)
   │                                          └─► Mp4VideoWriter (background save)
   └── preview.PresentAsync(clone)           (windowed mode only)
```

The window mirrors the camera examples: a single `FrameFlowVideoView` preview
plus a status bar (recording chip + clip count). In headless mode the preview
branch is omitted and Avalonia is never initialized.

## Notes

- **Console subsystem on purpose.** The project targets `Exe` (not `WinExe`)
  so the headless path keeps a real console for stdout logging + Ctrl+C. In
  windowed mode a console appears alongside the preview — fine for a dev example.
- **Memory budget.** The pre-roll ring holds full display-resolution un-pooled
  copies. At 640×480 a 2 s / 30 fps ring is ≈74 MB; see `PreRollBuffer` for the
  math and the hard cap.
