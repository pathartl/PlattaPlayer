<#
.SYNOPSIS
    Builds lazyusf2.dll (kode54's lazyusf2 N64 sound emulator) and copies it to src/PlattaPlayer.Codecs.Usf/native, from where the USF codec plugin ships it.

.DESCRIPTION
    Fetches lazyusf2 at the pinned commit into .src/, configures this folder's CMake wrapper with Visual
    Studio 2026 and builds Release x64. Requires git, CMake and the MSVC C++ workload. Re-running reuses the
    checkout and the build directory.
#>
param(
    [string]$Commit = '421f00bcaa1988b8e1825e91780129f24fbd1aa0',
    [string]$Repository = 'https://gitlab.com/kode54/lazyusf2.git',
    [string]$Generator = 'Visual Studio 18 2026',
    [string]$Toolset = 'v145',
    [string]$Config = 'Release'
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$src = Join-Path $here '.src/lazyusf2'
$build = Join-Path $here '.build'
$dest = Join-Path $here '../../src/PlattaPlayer.Codecs.Usf/native'

function Invoke-Native([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path (Join-Path $src '.git'))) {
    New-Item -ItemType Directory -Force (Split-Path $src) | Out-Null
    Invoke-Native git @('clone', '--no-checkout', $Repository, $src)
}
Invoke-Native git @('-C', $src, 'fetch', '--depth', '1', 'origin', $Commit)
Invoke-Native git @('-C', $src, 'checkout', '--force', $Commit)
# Small source fixes for MSVC x64 (see each patch's header). The forced checkout above undoes them first.
foreach ($patch in Get-ChildItem (Join-Path $here 'patches') -Filter *.patch | Sort-Object Name) {
    Invoke-Native git @('-C', $src, 'apply', '--ignore-whitespace', '--whitespace=nowarn', $patch.FullName)
}

Invoke-Native cmake @('-S', $here, '-B', $build, '-G', $Generator, '-A', 'x64', '-T', $Toolset)
Invoke-Native cmake @('--build', $build, '--config', $Config, '--target', 'lazyusf2', '--parallel')

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $build 'out/lazyusf2.dll') $dest -Force
Write-Host "lazyusf2.dll -> $((Resolve-Path $dest).Path)"
