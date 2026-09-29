<#
.SYNOPSIS
  Fails when a published Windows app lacks an FFmpeg library the loader requires.

.DESCRIPTION
  A published app's FFmpeg comes from the FrameFlow.Native.Runtime package, and nothing ties
  that package's FFmpeg major to the one FFmpegLibraryResolver loads. A floating reference once
  resolved to a package holding FFmpeg 7.1, and the FFmpeg 9 loader refused it at startup.

  The required names are read from FFmpegLibraryResolver.cs itself, so this check moves with an
  FFmpeg bump instead of going stale. Windows only: it reads the resolver's Windows names and
  looks for .dll files. Runs under Windows PowerShell 5.1 and pwsh.

.PARAMETER PublishDir
  The publish output directory, with the natives beside the executable
  (IncludeNativeLibrariesForSelfExtract=false).

.EXAMPLE
  ./scripts/Assert-PublishedFFmpeg.ps1 -PublishDir artifacts/MotionClip-win-x64
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDir
)

$ErrorActionPreference = 'Stop'

$resolverPath = Join-Path $PSScriptRoot '..\src\FrameFlow.Native\FFmpegLibraryResolver.cs'
$resolver = Get-Content -Raw $resolverPath

$block = [regex]::Match($resolver, 'WindowsSuffixes[\s\S]*?\{([\s\S]*?)\};')
if (-not $block.Success) {
    throw "Found no WindowsSuffixes table in $resolverPath. Update this script to match the resolver."
}

$names = @([regex]::Matches($block.Groups[1].Value, '\] = "([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
if ($names.Count -eq 0) {
    throw "WindowsSuffixes in $resolverPath names no libraries. Update this script to match the resolver."
}

if (-not (Test-Path $PublishDir)) {
    throw "Publish directory '$PublishDir' does not exist."
}

$missing = @($names | Where-Object { -not (Test-Path (Join-Path $PublishDir "$_.dll")) })
if ($missing.Count -gt 0) {
    $present = @(Get-ChildItem $PublishDir -Filter '*.dll' |
        Where-Object { $_.Name -match '^(av|sw)[a-z]+-[0-9]+\.dll$' } |
        ForEach-Object { $_.Name }) -join ', '
    if (-not $present) { $present = 'none' }
    throw ("The published app lacks $(($missing | ForEach-Object { "$_.dll" }) -join ', '), which " +
        "FFmpegLibraryResolver loads. FFmpeg libraries present: $present. Check " +
        "FrameFlowNativeRuntimeVersion in Directory.Build.props against the resolver's FFmpeg major.")
}

"ok: $(($names | ForEach-Object { "$_.dll" }) -join ', ')"
