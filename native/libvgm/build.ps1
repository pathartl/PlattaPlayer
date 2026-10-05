<#
.SYNOPSIS
    Builds ppvgm.dll (libvgm's VGM player and sound-chip emulations behind a small C interface) and copies it to src/PlattaPlayer.Codecs.Vgm/native, from where the VGM codec plugin ships it.

.DESCRIPTION
    Fetches libvgm at the pinned commit into .src/, applies patches/ if there are any, configures this
    folder's CMake wrapper with Visual Studio 2026 and builds Release x64. Requires git, CMake and the MSVC
    C++ workload. Re-running reuses the checkout and the build directory.
#>
param(
    # libvgm has no releases; this is master as of 2026-09-05.
    [string]$Commit = 'c8b998b606895990c409a512b86c5509070f9f0d',
    [string]$Repository = 'https://github.com/ValleyBell/libvgm.git',
    [string]$Generator = 'Visual Studio 18 2026',
    [string]$Toolset = 'v145',
    [string]$Config = 'Release'
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$src = Join-Path $here '.src/libvgm'
$build = Join-Path $here '.build'
$dest = Join-Path $here '../../src/PlattaPlayer.Codecs.Vgm/native'

function Invoke-Native([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path (Join-Path $src '.git'))) {
    New-Item -ItemType Directory -Force (Split-Path $src) | Out-Null
    Invoke-Native git @('clone', '--no-checkout', '--filter=blob:none', $Repository, $src)
}
Invoke-Native git @('-C', $src, 'fetch', '--depth', '1', 'origin', $Commit)
Invoke-Native git @('-C', $src, 'checkout', '--force', $Commit)
# Source fixes, if any (see each patch's header). The forced checkout above undoes them first.
$patches = Join-Path $here 'patches'
if (Test-Path $patches) {
    foreach ($patch in Get-ChildItem $patches -Filter *.patch | Sort-Object Name) {
        Invoke-Native git @('-C', $src, 'apply', '--ignore-whitespace', '--whitespace=nowarn', $patch.FullName)
    }
}

Invoke-Native cmake @('-S', $here, '-B', $build, '-G', $Generator, '-A', 'x64', '-T', $Toolset)
Invoke-Native cmake @('--build', $build, '--config', $Config, '--target', 'ppvgm', '--parallel')

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $build 'out/ppvgm.dll') $dest -Force
Write-Host "ppvgm.dll -> $((Resolve-Path $dest).Path)"
