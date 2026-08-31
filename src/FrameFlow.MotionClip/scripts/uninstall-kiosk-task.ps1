<#
.SYNOPSIS
Reverses `install-kiosk-task.ps1`: stops and unregisters the scheduled
task, kills the recorder process, and removes the install + log
directories. Leaves the clip-output directory intact by default --
those are production artefacts, not roam-owned state.

.DESCRIPTION
Paired with `install-kiosk-task.ps1` -- edited together. When the
installer learns to write to a new place or register another
side-effect, the inverse goes here. Roam doesn't track per-side-effect;
the pair IS the inventory, so anything the installer creates must have
a matching removal here or it survives an uninstall.

ASCII-only (see install script for the encoding rationale).

Defensive throughout: every removal uses -ErrorAction SilentlyContinue
so a partial install (or a re-run) tears down what's present without
failing on what isn't.

.PARAMETER InstallDir
Directory holding MotionClip.exe / scripts/. Removed recursively.
Defaults to the parent of the directory containing this script. The
default is filled in inside the script body (NOT as a param() default)
because under PS 5.1 over OpenSSH, $PSScriptRoot is empty during
param-block default evaluation. Mirrors the workaround in
install-kiosk-task.ps1.

.PARAMETER LogDir
Log directory created by the installer. Removed recursively. Defaults to
`motionclip-logs` under the profile of the account the registered task runs
as, read from the task before it is unregistered and resolved through that
account's SID -- an uninstall run by an administrator still finds the kiosk
account's logs.

.PARAMETER OutputDir
Clip output directory created by the installer. NOT removed by default --
clips are production artefacts. Pass -RemoveClips to nuke them too. Defaults
to `motionclip-clips` under the same profile, resolved the same way. If the
task is already gone or its account has no profile, both are skipped with a
warning; pass them explicitly to remove the directories in that case.

.PARAMETER RemoveClips
Switch. If set, also recursively removes -OutputDir. Use only for full
test-rig teardown.

.PARAMETER TaskName
Scheduled task to stop and unregister. Default `FrameFlow.MotionClip`.

.PARAMETER ProcessName
Recorder process to terminate. Default `MotionClip` (matches the exe
AssemblyName).

.EXAMPLE
PS> & .\uninstall-kiosk-task.ps1
Stops and unregisters the task, kills the process, removes install
and log dirs. Leaves the clips.

.EXAMPLE
PS> & .\uninstall-kiosk-task.ps1 -RemoveClips
As above, plus removes the clip output directory.
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [string]$LogDir,
    [string]$OutputDir,
    [switch]$RemoveClips,
    [string]$TaskName = "FrameFlow.MotionClip",
    [string]$ProcessName = "MotionClip"
)

# Don't bail on missing-target errors; an uninstall must succeed against
# any partial state.
$ErrorActionPreference = "Continue"

# Fill in $InstallDir from $PSScriptRoot here (NOT as a param default --
# under PS 5.1 over SSH, $PSScriptRoot is empty during param-block
# default evaluation, but reliably populated by the time the body runs).
if (-not $InstallDir) {
    if (-not $PSScriptRoot) {
        throw "Cannot determine -InstallDir: \$PSScriptRoot is empty and no value was passed. Re-run with -InstallDir <path>."
    }
    $InstallDir = Split-Path -Parent $PSScriptRoot
}

$removed = @()
$kept    = @()

# 1. Stop + unregister the scheduled task.
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

# Read the account the task runs as BEFORE unregistering it. Afterwards the
# registration is gone, and with it the only on-box record of which profile
# holds the clips and logs. An uninstall is usually run by an administrator
# rather than by the kiosk account, so resolving these paths from the invoking
# user would leave the kiosk's logs behind and could point -RemoveClips at the
# wrong profile.
function Get-AccountProfilePath {
    # Duplicated verbatim from install-kiosk-task.ps1 -- see the comment there.
    # Each script must run standalone over SSH, so neither dot-sources the other.
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

if (-not $LogDir -or -not $OutputDir) {
    # Fall back to the invoking account only when the task is already gone.
    $_taskUser    = if ($task) { $task.Principal.UserId } else { $env:USERNAME }
    $_taskProfile = Get-AccountProfilePath -Account $_taskUser
    if ($_taskProfile) {
        if (-not $LogDir)    { $LogDir    = Join-Path $_taskProfile "motionclip-logs"  }
        if (-not $OutputDir) { $OutputDir = Join-Path $_taskProfile "motionclip-clips" }
    } else {
        # An uninstall must succeed against any partial state, so this warns
        # rather than throwing. The directory steps below skip empty paths.
        Write-Warning ("Cannot resolve a profile directory for task account " +
                       "'$_taskUser'. Log and clip directories will be skipped; " +
                       "re-run with -LogDir / -OutputDir to remove them.")
    }
}

if ($task) {
    Stop-ScheduledTask       -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    $removed += "scheduled task '$TaskName'"
} else {
    $kept += "scheduled task '$TaskName' (not registered)"
}

# 2. Terminate any running process. The Task Scheduler stop above SHOULD
#    cover it, but a manually-launched process won't have a task to stop.
#
#    Scoped to executables under -InstallDir. An unqualified
#    `Get-Process -Name` also matches instances started by other accounts on
#    a shared host, and any unrelated program that happens to use the same
#    executable name -- force-stopping those would be collateral damage from
#    an uninstall that was only ever meant to remove this deployment.
#    Processes whose Path cannot be read (another user's, without elevation)
#    are deliberately left alone: a name match is not evidence of ownership.
$installFull = try { [IO.Path]::GetFullPath($InstallDir) } catch { $InstallDir }
# Compare against the directory WITH a trailing separator. A bare prefix test
# treats C:\Apps\MotionClip-old as living under C:\Apps\MotionClip, so an
# uninstall of one deployment would force-stop a sibling's recorder. The
# separator makes the boundary explicit while still matching MotionClip.exe
# sitting directly in the install directory.
$installPrefix = $installFull.TrimEnd(
    [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar
) + [IO.Path]::DirectorySeparatorChar
$procs = @(
    Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and ([IO.Path]::GetFullPath($_.Path)).StartsWith(
            $installPrefix, [StringComparison]::OrdinalIgnoreCase)
    }
)
if ($procs.Count -gt 0) {
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    $removed += "$($procs.Count) running '$ProcessName' process(es) under $InstallDir"
} else {
    $kept += "process '$ProcessName' (not running)"
}

# 3. Remove install + log directories.
foreach ($dir in @($InstallDir, $LogDir)) {
    if ($dir -and (Test-Path $dir)) {
        Remove-Item -Path $dir -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $dir) {
            $kept += "$dir (removal failed; check for locked files)"
        } else {
            $removed += $dir
        }
    } else {
        $kept += "$dir (not present)"
    }
}

# 4. Optionally remove clip output. Off by default -- production artefacts.
if ($RemoveClips) {
    if ($OutputDir -and (Test-Path $OutputDir)) {
        Remove-Item -Path $OutputDir -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $OutputDir) {
            $kept += "$OutputDir (removal failed)"
        } else {
            $removed += "$OutputDir (clips)"
        }
    } else {
        $kept += "$OutputDir (clips, not present)"
    }
} else {
    if ($OutputDir -and (Test-Path $OutputDir)) {
        $kept += "$OutputDir (clips, preserved -- pass -RemoveClips to wipe)"
    }
}

Write-Host "FrameFlow.MotionClip uninstall complete."
Write-Host ""
if ($removed.Count -gt 0) {
    Write-Host "Removed:"
    foreach ($r in $removed) { Write-Host "  - $r" }
} else {
    Write-Host "Removed: (nothing to remove)"
}
Write-Host ""
if ($kept.Count -gt 0) {
    Write-Host "Left in place:"
    foreach ($k in $kept) { Write-Host "  - $k" }
}
