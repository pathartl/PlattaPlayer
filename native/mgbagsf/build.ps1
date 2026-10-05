<#
.SYNOPSIS
    Builds mgbagsf.dll (mGBA's Game Boy Advance core behind a small C interface) and copies it to src/PlattaPlayer.Codecs.Gsf/native, from where the GSF codec plugin ships it.

.DESCRIPTION
    Fetches mGBA at the pinned release (0.10.5) into .src/, applies patches/ if there are any, configures
    this folder's CMake wrapper with Visual Studio 2026 and builds Release x64. Requires git, CMake and the
    MSVC C++ workload. Re-running reuses the checkout and the build directory.
#>
param(
    # The commit tagged 0.10.5.
    [string]$Commit = '26b7884bc25a5933960f3cdcd98bac1ae14d42e2',
    [string]$Repository = 'https://github.com/mgba-emu/mgba.git',
    [string]$Generator = 'Visual Studio 18 2026',
    [string]$Toolset = 'v145',
    [string]$Config = 'Release'
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$src = Join-Path $here '.src/mgba'
$build = Join-Path $here '.build'
$dest = Join-Path $here '../../src/PlattaPlayer.Codecs.Gsf/native'

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
Invoke-Native cmake @('--build', $build, '--config', $Config, '--target', 'mgbagsf', '--parallel')

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $build 'out/mgbagsf.dll') $dest -Force
Write-Host "mgbagsf.dll -> $((Resolve-Path $dest).Path)"
