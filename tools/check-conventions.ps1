# ============================================================================
#  check-conventions.ps1 — 反屎山守则的机器检查（重构 2.0 / P0.6）
#
#  在 build.ps1 中于构建前执行，违规即失败（exit 1）。规则见
#  docs\REFACTOR-2.0-PLAN.md 第 9 节。例外登记在 tools\conventions-allow.txt
#  （每行：<规则ID> <相对路径> # 理由），例外只减不增。
#
#  用法：
#    powershell -ExecutionPolicy Bypass -File tools\check-conventions.ps1
#    powershell -ExecutionPolicy Bypass -File tools\check-conventions.ps1 -Report
# ============================================================================

param([switch]$Report)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$violations = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

$allowFile = Join-Path $root 'tools\conventions-allow.txt'
$allow = @{}
if (Test-Path $allowFile) {
    foreach ($line in Get-Content $allowFile) {
        $t = $line.Trim()
        if ($t -eq '' -or $t.StartsWith('#')) { continue }
        $parts = (($t -split '#')[0]).Trim() -split '\s+', 2
        if ($parts.Count -ge 2) { $allow[($parts[0] + '|' + $parts[1].Trim())] = $true }
    }
}
function Test-Allowed([string]$rule, [string]$relPath) { return $allow.ContainsKey($rule + '|' + $relPath) }

$srcFiles = @()
foreach ($dir in @('src', 'tests')) {
    $p = Join-Path $root $dir
    if (Test-Path $p) {
        $srcFiles += Get-ChildItem $p -Recurse -File -Include *.cs |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    }
}
if ($srcFiles.Count -eq 0) { Write-Host 'check-conventions: 没有找到源文件，跳过'; exit 0 }

# ---------- R1 文件行数上限 ----------
$maxLines = 400
foreach ($f in $srcFiles) {
    $rel = $f.FullName.Substring($root.Length + 1)
    $n = [System.IO.File]::ReadAllLines($f.FullName).Length
    if ($n -gt $maxLines -and -not (Test-Allowed 'R1' $rel)) {
        $violations.Add('R1 文件超过 ' + $maxLines + ' 行（' + $n + '）: ' + $rel)
    }
}

# ---------- R2 Core 不得引用 UI 命名空间 ----------
$coreDir = Join-Path $root 'src\DshController.Core'
if (Test-Path $coreDir) {
    $coreFiles = Get-ChildItem $coreDir -Recurse -File -Include *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    foreach ($f in $coreFiles) {
        $rel = $f.FullName.Substring($root.Length + 1)
        $hits = Select-String -Path $f.FullName -Pattern '^\s*using\s+(Microsoft\.UI|Windows\.UI|Microsoft\.Xaml)'
        if ($hits -and -not (Test-Allowed 'R2' $rel)) {
            $violations.Add('R2 Core 引用了 UI 命名空间: ' + $rel + ':' + $hits[0].LineNumber)
        }
    }
}

# ---------- R3 裸 catch 必须写日志或 "理由:" 注释 ----------
foreach ($f in $srcFiles) {
    $rel = $f.FullName.Substring($root.Length + 1)
    if (Test-Allowed 'R3' $rel) { continue }
    $text = [System.IO.File]::ReadAllText($f.FullName)
    $found = [regex]::Matches($text, 'catch\s*(\([^)]*\))?\s*\{(?<body>[^{}]*)\}')
    foreach ($m in $found) {
        $body = $m.Groups['body'].Value
        $stripped = [regex]::Replace($body, '(?s)/\*.*?\*/|//[^\r\n]*', '')
        if ($stripped -match '\S') { continue }
        if ($body -match '理由') { continue }
        $line = ($text.Substring(0, $m.Index).Split([char]10)).Length
        $violations.Add('R3 裸 catch 未写理由/日志: ' + $rel + ':' + $line)
    }
}

# ---------- R4 async void 仅允许事件处理器 ----------
foreach ($f in $srcFiles) {
    $rel = $f.FullName.Substring($root.Length + 1)
    if (Test-Allowed 'R4' $rel) { continue }
    $hits = Select-String -Path $f.FullName -Pattern 'async\s+void\s+\w+'
    foreach ($h in $hits) {
        $name = [regex]::Match($h.Line, 'async\s+void\s+(\w+)').Groups[1].Value
        if ($name -match '_(Click|Changed|Toggled|Loaded|Tick|Opened|Closed)$') { continue }
        $warnings.Add('R4 async void 非事件处理器: ' + $rel + ':' + $h.LineNumber + ' (' + $name + ')')
    }
}

# ---------- R5 App 层（CommandLine 除外）禁止同步阻塞 ----------
$appDir = Join-Path $root 'src\DshController.App'
if (Test-Path $appDir) {
    $appFiles = Get-ChildItem $appDir -Recurse -File -Include *.cs |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|CommandLine)\\' }
    foreach ($f in $appFiles) {
        $rel = $f.FullName.Substring($root.Length + 1)
        if (Test-Allowed 'R5' $rel) { continue }
        $hits = Select-String -Path $f.FullName -Pattern '\.GetAwaiter\(\)\.GetResult\(\)|Task\.Wait\('
        foreach ($h in $hits) { $violations.Add('R5 App 层同步阻塞: ' + $rel + ':' + $h.LineNumber) }
    }
}

Write-Host ('check-conventions: 扫描 ' + $srcFiles.Count + ' 个文件')
if ($warnings.Count -gt 0) {
    Write-Host ('警告（' + $warnings.Count + '）：') -ForegroundColor Yellow
    $warnings | ForEach-Object { Write-Host ('  ' + $_) -ForegroundColor Yellow }
}
if ($violations.Count -gt 0) {
    Write-Host ('违规（' + $violations.Count + '）：') -ForegroundColor Red
    $violations | ForEach-Object { Write-Host ('  ' + $_) -ForegroundColor Red }
    if (-not $Report) { exit 1 }
} else {
    Write-Host 'check-conventions: PASS' -ForegroundColor Green
}
exit 0
