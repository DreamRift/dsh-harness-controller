# ============================================================================
#  Build DshController (WinUI 3) with the dotnet SDK — 重构 2.0 三工程布局。
#
#  Layout:
#    src\DshController.Core   纯逻辑类库（net10.0，无 UI 依赖）
#    src\DshController.App    WinUI 3 可执行（产物名 DshController.exe）
#    tests\DshController.Tests 离线单测（xUnit）
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File build.ps1             # Release -> publish-fixed\
#    powershell -ExecutionPolicy Bypass -File build.ps1 -Debug      # 快速开发构建
#    powershell -ExecutionPolicy Bypass -File build.ps1 -Clean      # 清理 bin/obj/publish*
#    powershell -ExecutionPolicy Bypass -File build.ps1 -Portable   # .NET 亦自包含
#    powershell -ExecutionPolicy Bypass -File build.ps1 -SkipChecks # 跳过约定机检与单测
#  版本号单源：src\DshController.App\DshController.App.csproj 的 <Version>。
# ============================================================================

param(
    [switch]$Clean,
    [switch]$Debug,
    [switch]$Portable,
    [switch]$SkipChecks
)
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProj  = Join-Path $dir 'src\DshController.App\DshController.App.csproj'
$testProj = Join-Path $dir 'tests\DshController.Tests\DshController.Tests.csproj'

# ---------- clean ----------
if ($Clean) {
    foreach ($p in 'publish', 'publish-fixed') {
        $t = Join-Path $dir $p
        if (Test-Path $t) { Remove-Item $t -Recurse -Force }
    }
    Get-ChildItem $dir -Recurse -Directory -Include 'bin', 'obj' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(legacy|test-usage-stats)\\' } |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host "cleaned bin/ obj/ publish/ publish-fixed/" -ForegroundColor Yellow
    return
}

# ---------- preflight ----------
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw 'dotnet SDK not found. Install .NET SDK from https://dotnet.microsoft.com/download'
}
$sdkLine = & dotnet --version 2>$null
$sdkMajor = 0
if ($sdkLine -match '^(\d+)\.') { $sdkMajor = [int]$Matches[1] }
if ($sdkMajor -lt 8) {
    throw "dotnet SDK $sdkLine is too old; 8.0+ is required (found via '$($dotnet.Source)')."
}
Write-Host "dotnet SDK : $sdkLine"

# 版本号单源：从 App csproj 读取
$version = '0.0.0'
if ((Get-Content $appProj -Raw) -match '<Version>([^<]+)</Version>') { $version = $Matches[1] }
Write-Host "version    : $version"

# ---------- quality gates（重构 2.0：约定机检 + 离线单测） ----------
if (-not $SkipChecks) {
    $checker = Join-Path $dir 'tools\check-conventions.ps1'
    if (Test-Path $checker) {
        & powershell -ExecutionPolicy Bypass -File $checker
        if ($LASTEXITCODE -ne 0) { Write-Host "CONVENTION CHECK FAILED" -ForegroundColor Red; exit $LASTEXITCODE }
    }
    if (Test-Path $testProj) {
        & dotnet test $testProj -nologo -v q
        if ($LASTEXITCODE -ne 0) { Write-Host "UNIT TESTS FAILED" -ForegroundColor Red; exit $LASTEXITCODE }
    }
}

$commonArgs = @()

if ($Debug) {
    # ---------- fast dev build ----------
    & dotnet build $appProj @commonArgs -nologo
    if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED (exit $LASTEXITCODE)" -ForegroundColor Red; exit $LASTEXITCODE }
    $out = Join-Path $dir 'src\DshController.App\bin\x64\Debug\net10.0-windows10.0.19041.0'
    Write-Host "DONE -> $out\DshController.exe" -ForegroundColor Green
    exit 0
}

# ---------- release publish（WASDK 自包含；.NET 自包含由 -Portable 控制） ----------
if ($Portable) {
    $commonArgs += '-p:Portable=true'
    $commonArgs += '--self-contained'
} else {
    $commonArgs += '--no-self-contained'
}
$outDir = Join-Path $dir 'publish-fixed'
& dotnet publish $appProj -c Release -r win-x64 -p:Platform=x64 -o $outDir @commonArgs -nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "PUBLISH FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $outDir 'DshController.exe'
if (-not (Test-Path $exe)) { throw 'publish finished but DshController.exe not found' }

# zip for distribution（排除本机运行时文件：配置/日志/报告）
$zip = Join-Path $outDir "DshController-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
$zipItems = Get-ChildItem $outDir -Force | Where-Object {
    $n = $_.Name
    $n -notin @('launcher.json', 'instances.json', 'instances.json.tmp', 'cli.log', 'crash.log', 'reports', 'portable.marker') -and
    -not $n.StartsWith('launcher.json.') -and
    -not $n.EndsWith('.log') -and
    -not $n.EndsWith('.zip')
}
Compress-Archive -Path $zipItems.FullName -DestinationPath $zip -Force

$size = '{0:N0} MB' -f ((Get-ChildItem $outDir -Recurse | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "DONE -> publish-fixed\DshController.exe ($size)" -ForegroundColor Green
Write-Host "zip   -> $zip" -ForegroundColor Green
Write-Host "self checks:" -ForegroundColor Yellow
Write-Host "  .\publish-fixed\DshController.exe --check" -ForegroundColor Yellow
Write-Host "  .\publish-fixed\DshController.exe --selftest-plugins" -ForegroundColor Yellow
Write-Host "  .\publish-fixed\DshController.exe --spawn-test --port 3137" -ForegroundColor Yellow
