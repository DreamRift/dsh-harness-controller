# ============================================================================
#  Build DshController.exe with the .NET Framework compiler (no Visual Studio
#  needed). The script is intentionally ASCII-only so it works under any
#  Windows PowerShell code page.
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File build.ps1
#    powershell -ExecutionPolicy Bypass -File build.ps1 -Clean
# ============================================================================
param([switch]$Clean)
$ErrorActionPreference = 'Stop'

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Locate csc.exe (shipped with .NET Framework 4.x)
$cscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'csc.exe not found (.NET Framework 4.x required)' }

$out = Join-Path $dir 'DshController.exe'
if ($Clean -and (Test-Path $out)) { Remove-Item $out -Force }

Write-Host "csc  : $csc"
Write-Host "compiling..."

& $csc /nologo /target:winexe /codepage:65001 /optimize `
    "/out:$out" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    (Join-Path $dir 'DshController.cs')

if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "DONE -> $out" -ForegroundColor Green
Write-Host "self checks:" -ForegroundColor Yellow
Write-Host "  .\DshController.exe --check" -ForegroundColor Yellow
Write-Host "  .\DshController.exe --spawn-test --port 3137" -ForegroundColor Yellow
