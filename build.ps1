<#
Publishes plv1-convert.exe as a single native executable (NativeAOT).

Normally `dotnet publish` finds the MSVC linker by itself. On machines where
vcvarsall is broken or only partially installed - it fails with "Platform
linker not found" - this script locates the toolset and the Windows SDK and
hands their paths to the compiler directly.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Plv1Convert'

Write-Host 'Publishing plv1-convert (NativeAOT)...'
Push-Location $project
try {
    # The straightforward path, when the C++ workload is properly installed.
    dotnet publish -c $Configuration --nologo
    if ($LASTEXITCODE -eq 0) { Write-Host 'Done.'; return }

    Write-Host 'Linker not found via vcvarsall - locating the toolset manually.'

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found - install Visual Studio Build Tools." }

    $vsPath = & $vswhere -products * -latest `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath
    if (-not $vsPath) { throw "No Visual Studio install with the C++ x64 toolset." }

    $msvc = Get-ChildItem "$vsPath\VC\Tools\MSVC" -Directory |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $msvc) { throw "No MSVC toolset under $vsPath." }

    $sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\Lib"
    $sdk = Get-ChildItem $sdkRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path "$($_.FullName)\um\x64\kernel32.lib" } |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $sdk) { throw "No Windows SDK with x64 libraries under $sdkRoot." }

    Write-Host "  toolset: $($msvc.Name)"
    Write-Host "  sdk:     $($sdk.Name)"

    $env:PATH = "$($msvc.FullName)\bin\Hostx64\x64;$env:PATH"
    $env:LIB = "$($msvc.FullName)\lib\x64;$($sdk.FullName)\ucrt\x64;$($sdk.FullName)\um\x64"
    $env:INCLUDE = "$($msvc.FullName)\include"

    dotnet publish -c $Configuration --nologo -p:IlcUseEnvironmentalTools=true
    if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

    $exe = Join-Path $project "bin\$Configuration\net10.0\win-x64\publish\plv1-convert.exe"
    Write-Host "Done: $exe"
}
finally {
    Pop-Location
}
