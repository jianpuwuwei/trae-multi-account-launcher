# Build the sandbox launcher with the .NET Framework 4.x csc (no SDK required).
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$srcDir = Join-Path $root 'src'
$binDir = Join-Path $root 'bin'
$outExe = Join-Path $binDir 'TraeSandbox.exe'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "csc.exe not found: $csc (.NET Framework 4.x is required)"
}

New-Item -ItemType Directory -Force -Path $binDir | Out-Null

$sources = Get-ChildItem -Path $srcDir -Filter '*.cs' | Sort-Object Name
if ($sources.Count -eq 0) {
    throw "No source files in: $srcDir"
}

$arguments = @(
    '/nologo',
    '/target:exe',
    '/platform:x64',
    '/codepage:65001',
    '/utf8output',
    '/optimize+',
    '/r:System.Management.dll',
    "/out:$outExe"
) + ($sources | ForEach-Object { $_.FullName })

& $csc $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Write-Host "Build OK: $outExe"
