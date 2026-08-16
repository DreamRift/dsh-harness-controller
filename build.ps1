# ============================================================================
#  Build DshController v0.2.0 (WinUI 3) with the dotnet SDK.
#  Requires: .NET SDK >= 6.0, NuGet connectivity (first build restores
#  Microsoft.WindowsAppSDK). No Visual Studio needed.
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File build.ps1             # Release -> publish\
#    powershell -ExecutionPolicy Bypass -File build.ps1 -Debug      # fast dev build (bin\)
#    powershell -ExecutionPolicy Bypass -File build.ps1 -Clean      # wipe bin/obj/publish
#    powershell -ExecutionPolicy Bypass -File build.ps1 -Portable   # also self-contain .NET
# ============================================================================

param(
    [switch]$Clean,
    [switch]$Debug,
    [switch]$Portable
)
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

# ---------- preflight ----------
if ($Clean) {
    foreach ($p in 'bin', 'obj', 'publish') {
        $t = Join-Path $dir $p
        if (Test-Path $t) { Remove-Item $t -Recurse -Force }
    }
    Write-Host "cleaned bin/ obj/ publish/" -ForegroundColor Yellow
    return
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw 'dotnet SDK not found. Install .NET SDK 6.0+ from https://dotnet.microsoft.com/download'
}
$sdkLine = & dotnet --version 2>$null
$sdkMajor = 0
if ($sdkLine -match '^(\d+)\.') { $sdkMajor = [int]$Matches[1] }
if ($sdkMajor -lt 6) {
    throw "dotnet SDK $sdkLine is too old; 6.0+ is required (found via '$($dotnet.Source)')."
}
Write-Host "dotnet SDK : $sdkLine"

$commonArgs = @()

if ($Debug) {
    # ---------- fast dev build ----------
    & dotnet build (Join-Path $dir 'DshController.csproj') @commonArgs -nologo
    if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED (exit $LASTEXITCODE)" -ForegroundColor Red; exit $LASTEXITCODE }
    $out = Join-Path $dir 'bin\x64\Debug\net6.0-windows10.0.19041.0'
    Write-Host "DONE -> $out\DshController.exe" -ForegroundColor Green
    exit 0
}

# ---------- release publish (WASDK self-contained, framework-dependent .NET by default) ----------
# 注：.NET 自包含由 Portable 参数显式控制；--no-self-contained 避免 NETSDK1179 警告
if ($Portable) {
    $commonArgs += '-p:Portable=true'
    $commonArgs += '--self-contained'
} else {
    $commonArgs += '--no-self-contained'
}
& dotnet publish (Join-Path $dir 'DshController.csproj') `
    -c Release -r win-x64 -p:Platform=x64 -o (Join-Path $dir 'publish') `
    @commonArgs -nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "PUBLISH FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $dir 'publish\DshController.exe'
if (-not (Test-Path $exe)) { throw 'publish finished but DshController.exe not found' }

# zip for distribution
$zip = Join-Path $dir 'publish\DshController-0.2.0-win-x64.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $dir 'publish\*') -DestinationPath $zip -Force

$size = '{0:N0} MB' -f ((Get-ChildItem (Join-Path $dir 'publish') -Recurse | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "DONE -> publish\DshController.exe ($size)" -ForegroundColor Green
Write-Host "zip   -> $zip" -ForegroundColor Green
Write-Host "self checks:" -ForegroundColor Yellow
Write-Host "  .\publish\DshController.exe --check" -ForegroundColor Yellow
Write-Host "  .\publish\DshController.exe --spawn-test --port 3137" -ForegroundColor Yellow
