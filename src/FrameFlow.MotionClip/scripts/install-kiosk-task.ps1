<#
.SYNOPSIS
Registers (or updates, idempotent) the FrameFlow.MotionClip scheduled task
on a kiosk so the recorder survives reboots and crash-restarts.

.DESCRIPTION
Creates a Task Scheduler entry that runs MotionClip headless at the kiosk
user's logon. Configuration:

- Trigger: "At log on" of the named user. The kiosk auto-logs-in as that
  user, so the trigger fires immediately after every reboot.
- Logon type: Interactive. The task runs *in the user's session*, with
  no service password storage and no Session 0 / display fight (which
  is why we don't use a Windows Service for an Avalonia / D3D app).
- Restart-on-failure: up to 99 restarts at 1-minute intervals if the
  process exits non-zero. Combined with the in-process camera reconnect
  loop, this gives two layers of resilience: the recorder rides through
  USB hot-unplugs on its own, and crash-recovers via the Task Scheduler.
- No execution time limit. Default would kill the process after 72 h.
- Single instance: IgnoreNew. PS 5.1 (the kiosk's PowerShell version)
  only supports {Parallel, Queue, IgnoreNew} for MultipleInstances --
  the more obvious StopExisting value is PS 7+ only. Stale "Running"
  state is handled by an explicit Stop-then-Start dance in the
  roamfile's start: block, NOT here.

This file MUST stay ASCII-only. The kiosk runs Windows PowerShell 5.1,
which reads .ps1 files without a BOM as Windows-1252; non-ASCII chars
(em-dash, arrows, smart quotes) break the parser at runtime even though
the file looks fine in a UTF-8 editor.

Has a companion uninstaller, `uninstall-kiosk-task.ps1`, that mirrors
each side-effect this script creates. Keep them edited in lockstep:
when this script writes to a new path or registers another side-effect,
the uninstaller learns to reverse it.

.PARAMETER InstallDir
Directory holding MotionClip.exe. Defaults to the parent of the
directory containing this script (i.e. correct when published under
`scripts/install-kiosk-task.ps1` at the install root). The default
is filled in inside the script body rather than as a `param()`
default expression -- under PS 5.1, when the script is launched via
SSH (`powershell -File ...` over OpenSSH), BOTH `$PSScriptRoot` and
`$PSCommandPath` come back empty during param-block default
evaluation, even though they're documented to be available. They're
reliably set by the time the body runs.

.PARAMETER OutputDir
Where clip MP4s are written. Created if missing. Defaults to
`motionclip-clips` under the profile of the -User account, resolved through
that account's SID and the ProfileList registry rather than from whoever runs
this script -- an install performed as an administrator still writes into the
kiosk account's profile. If the account has no profile yet (Windows creates it
at first logon) the script stops and asks for explicit paths.

.PARAMETER LogDir
Where timestamped log files are written. Created if missing. Defaults to
`motionclip-logs` under the same profile, resolved the same way.

.PARAMETER LogLevel
trace | debug | info | warning | error | critical | none. Default `info`.
Use `debug` during reconnect-loop / native-bootstrap investigations.

.PARAMETER Sensitivity
Motion sensitivity 0.0-1.0 (matches the CLI flag). Default 0.8.

.PARAMETER IdStartsWith
Camera selector passed through to MotionClip as --IdStartsWith. The recorder
tracks the camera whose Periphery device Id starts with this prefix. That Id is
the Windows PnP instance id, of the form USB\VID_xxxx&PID_xxxx\<instance>; run
the recorder once without this flag to see the ids of the cameras it finds. The
match is case-sensitive (ordinal). Empty (default) omits the flag entirely, so
MotionClip tracks the first available camera.

.PARAMETER CameraBuffers
Pre-allocated frame buffers in the camera capture pool. Larger = more
headroom for slow-encoder bursts (camera doesn't drop frames as long as
some buffer is free) at the cost of memory (~1.4 MB each at 720p NV12).
Default 3 matches Periphery's own default; bump to 6-8 on units where the
encoder workload regularly produces "Frame dropped (#N); pool exhausted"
warnings. Passed through to MotionClip as --camera-buffers.

.PARAMETER MotionSectors
Numpad-numbered subset of a 3x3 grid (1-9) that motion detection watches.
Layout: 7-8-9 top row, 4-5-6 middle, 1-2-3 bottom. Preview and recording
are unaffected -- only the motion trigger is restricted. Empty string
(default) or "all" arms every sector and is identical to the historic
behaviour. Examples: "5" (centre only), "4,5,6" (middle row),
"1,2,3,4,5,6" (ignore the ceiling). Passed through to MotionClip as
--motion-sectors.

.PARAMETER User
Kiosk account the task runs as. Must be the auto-login account, so on a
kiosk you almost always pass this explicitly. Defaults to the account
running this script, which is right for a local trial and wrong for a
deployment where you install as an administrator.

.PARAMETER TaskName
Scheduled-task display name. Default `FrameFlow.MotionClip`.

.EXAMPLE
PS> & .\install-kiosk-task.ps1 -LogLevel debug
Registers the task with debug logging.
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [string]$OutputDir,
    [string]$LogDir,
    [string]$LogLevel = "info",
    [double]$Sensitivity = 0.8,
    [string]$IdStartsWith = "",
    [int]$CameraBuffers = 3,
    [string]$MotionSectors = "",
    [string]$User = "$env:COMPUTERNAME\$env:USERNAME",
    [string]$TaskName = "FrameFlow.MotionClip"
)

$ErrorActionPreference = "Stop"

# Resolve the clip/log directories from -User, NOT from whoever runs this
# script. The task executes as $User, so its output has to land in that
# account's profile. A kiosk install is normally performed from an elevated
# or SSH session under a different account, so defaulting to the installer's
# own profile would point the task at a directory it may not be able to write
# -- and would silently succeed at install time.
function Get-AccountProfilePath {
    # Resolve an account's real profile directory through its SID and the
    # ProfileList registry, which is where Windows records it. Guessing
    # <ProfilesRoot>\<account-name> is wrong for domain accounts, redirected or
    # customised profiles, and service accounts -- SYSTEM's profile is under
    # system32\config, not C:\Users. Returns $null when the account does not
    # resolve or has no profile yet; Windows creates it at first logon, so a
    # never-used kiosk account has no entry.
    #
    # Duplicated verbatim in uninstall-kiosk-task.ps1. The two are edited and
    # deployed as a pair, but each must run standalone over SSH, so neither
    # dot-sources the other.
    param([Parameter(Mandatory)][string]$Account)
    try {
        $sid = (New-Object System.Security.Principal.NTAccount($Account)).Translate(
            [System.Security.Principal.SecurityIdentifier]).Value
        $key = Join-Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList' $sid
        $raw = (Get-ItemProperty -Path $key -Name ProfileImagePath -ErrorAction Stop).ProfileImagePath
        return [Environment]::ExpandEnvironmentVariables($raw)
    } catch {
        return $null
    }
}

if (-not $OutputDir -or -not $LogDir) {
    $_userProfile = Get-AccountProfilePath -Account $User
    if (-not $_userProfile) {
        throw ("Cannot resolve a profile directory for -User '$User'. The account may " +
               "not exist on this machine, or may never have logged on -- Windows " +
               "creates the profile at first logon. Pass -OutputDir and -LogDir " +
               "explicitly.")
    }
    if (-not $OutputDir) { $OutputDir = Join-Path $_userProfile "motionclip-clips" }
    if (-not $LogDir)    { $LogDir    = Join-Path $_userProfile "motionclip-logs"  }
}

# Fill in $InstallDir from $PSScriptRoot here (NOT as a param default --
# under PS 5.1 over SSH, $PSScriptRoot is empty during param-block
# default evaluation, but reliably populated by the time the body runs).
if (-not $InstallDir) {
    if (-not $PSScriptRoot) {
        throw "Cannot determine -InstallDir: \$PSScriptRoot is empty and no value was passed. Re-run with -InstallDir <path>."
    }
    $InstallDir = Split-Path -Parent $PSScriptRoot
}

$exe = Join-Path $InstallDir "MotionClip.exe"
if (-not (Test-Path $exe)) {
    throw "MotionClip.exe not found at '$exe'. Pass -InstallDir to point at the install root."
}

foreach ($dir in @($OutputDir, $LogDir)) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
}

# Arguments string -- Task Scheduler stores this as one verbatim line.
# Quote any path that might contain spaces.
$argParts = @(
    'run',
    '--headless',
    '--log-dir',       "`"$LogDir`"",
    '--output-dir',    "`"$OutputDir`"",
    '--log-level',     $LogLevel,
    '--sensitivity',   ([string]$Sensitivity),
    '--camera-buffers',([string]$CameraBuffers)
)
# Optional camera selector. Omitted by default so MotionClip tracks the first
# available camera. Quote the value: a PnP id can
# contain '&' (USB\VID_xxxx&PID_yyyy...) and must stay a single token. Task
# Scheduler launches the exe directly (not via cmd.exe), so '&' is otherwise
# harmless in the stored argument line.
if ($IdStartsWith) {
    $argParts += '--IdStartsWith'
    $argParts += "`"$IdStartsWith`""
}
# Optional motion-sector mask. Omit when empty so MotionClip's default
# ("all 9 armed") applies; otherwise pass through as-is (the recorder
# tolerates "5", "4,5,6", "4 5 6", "all", etc.). Quote it because a
# comma-separated value may contain spaces if the operator wrote them.
if ($MotionSectors) {
    $argParts += '--motion-sectors'
    $argParts += "`"$MotionSectors`""
}
$arguments = $argParts -join ' '

$action  = New-ScheduledTaskAction `
    -Execute $exe `
    -Argument $arguments `
    -WorkingDirectory $InstallDir

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $User

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 99 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew

# Interactive logon principal: runs in the user's desktop session WITHOUT
# requiring a stored password. Limited (non-elevated) -- camera and
# filesystem access don't need admin.
$principal = New-ScheduledTaskPrincipal `
    -UserId $User `
    -LogonType Interactive `
    -RunLevel Limited

# -Force makes this idempotent: re-running with changed args UPDATES the
# existing registration in place. Same task name, fresh action/settings.
Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Force | Out-Null

Write-Host "Registered scheduled task '$TaskName':"
Write-Host "  Run as : $User (interactive logon, session 1)"
Write-Host "  Trigger: at logon of $User"
Write-Host "  Action : $exe $arguments"
Write-Host "  Restart: up to 99 times, 1 min interval, no time limit."
