<#
.SYNOPSIS
    Builds emu88.dll (gearmulator's Roland 88emu) and copies it to src/PlattaPlayer.Codecs.Midi/native, from where the MIDI codec plugin ships it.

.DESCRIPTION
    Fetches gearmulator at the pinned commit into .src/ (only the submodules 88emu needs), configures
    this folder's CMake wrapper with Visual Studio 2026 and builds Release. Requires git, CMake and the
    MSVC C++ workload. Re-running reuses the checkout and the build directory.
#>
param(
    [string]$Commit = '376d06a4c60a8e55957755ddaca16e6a4cadfeb4',
    [string]$Generator = 'Visual Studio 18 2026',
    [string]$Toolset = 'v145',
    [string]$Config = 'Release'
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$src = Join-Path $here '.src/gearmulator'
$build = Join-Path $here '.build'
$dest = Join-Path $here '../../src/PlattaPlayer.Codecs.Midi/native'

function Invoke-Native([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path (Join-Path $src '.git'))) {
    New-Item -ItemType Directory -Force (Split-Path $src) | Out-Null
    Invoke-Native git @('clone', '--no-checkout', 'https://github.com/dsp56300/gearmulator', $src)
}
Invoke-Native git @('-C', $src, 'fetch', '--depth', '1', 'origin', $Commit)
Invoke-Native git @('-C', $src, 'checkout', '--force', $Commit)
# 88emu needs the DSP56300 tree (it carries asmjit as a nested submodule). mc68k and the UI libraries are
# referenced by gearmulator's CMake unconditionally, so they must be present even though nothing here
# builds them. JUCE and the rest are only for the plugins.
Invoke-Native git @('-C', $src, 'submodule', 'update', '--init', '--recursive', '--depth', '1', 'source/cpu/mc68k',
    'source/3rdparty/freetype', 'source/3rdparty/lunasvg', 'source/3rdparty/RmlUi')
Invoke-Native git @('-C', $src, 'submodule', 'update', '--init', '--recursive', '--depth', '1', 'source/cpu/dsp56300')

Invoke-Native cmake @('-S', $here, '-B', $build, '-G', $Generator, '-A', 'x64', '-T', $Toolset)
Invoke-Native cmake @('--build', $build, '--config', $Config, '--target', 'emu88', '--parallel')

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $build 'out/emu88.dll') $dest -Force
Write-Host "emu88.dll -> $((Resolve-Path $dest).Path)"
