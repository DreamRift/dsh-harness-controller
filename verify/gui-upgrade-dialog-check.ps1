# ============================================================================
#  gui-upgrade-dialog-check.ps1 - upgrade entry & details dialog walk
#  (sub-task "upgrade details dialog"). Fixture: RTEST-U instance (port 3185,
#  temp HOME) with a FABRICATED old installed version of a real npm package
#  -> market latest > installed -> upgrade button lights next to uninstall.
#  Walk: dialog shows version pair + changelog-or-placeholder + three buttons
#  (upgrade / cancel / ignore-this-version); "ignore" hides the button and
#  persists across page re-entry. ASCII-only; CJK button names from char codes.
#  Self-restoring (registry/archives/home/ledger).
#  Usage: powershell -ExecutionPolicy Bypass -File verify\gui-upgrade-dialog-check.ps1 -Exe <exe> -Out <dir>
# ============================================================================
param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [Parameter(Mandatory=$true)][string]$Out,
    [int]$StartupSec = 25
)
$ErrorActionPreference = 'Stop'
$S_UP   = [string]([char]0x5347) + [char]0x7EA7                        # sheng-ji = upgrade
$S_UN   = [string]([char]0x5378) + [char]0x8F7D                        # xie-zai = uninstall
$S_IGN  = ([string]([char]0x5FFD) + [char]0x7565 + [char]0x672C) + ([string][char]0x6B21 + [char]0x5347 + [char]0x7EA7)  # hu-lue-ben-ci-sheng-ji
$S_CAN  = [string]([char]0x53D6) + [char]0x6D88                        # qu-xiao = cancel
$S_CUR  = [string]([char]0x5F53) + [char]0x524D + [char]0x7248 + [char]0x672C   # dang-qian-ban-ben
$S_NEWV = [string]([char]0x65B0) + [char]0x7248 + [char]0x672C                   # xin-ban-ben
$S_LOG  = [string]([char]0x66F4) + [char]0x65B0 + [char]0x5185 + [char]0x5BB9   # geng-xin-nei-rong
$S_PLACE = [string]([char]0x5E02) + [char]0x573A + [char]0x672A + [char]0x63D0 + [char]0x4F9B  # shi-chang-wei-ti-gong
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Win32U {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
[void][Win32U]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$results = @()
$script:currentProc = $null
function Add-Result([bool]$ok, [string]$id, [string]$detail) {
    $mark = 'FAIL'; if ($ok) { $mark = 'PASS' }
    $script:results += [pscustomobject]@{ Ok = $ok; Id = $id; Detail = $detail }
    Write-Output ($mark + ' ' + $id + ' ' + $detail)
}
$AE  = [System.Windows.Automation.AutomationElement]
$TS  = [System.Windows.Automation.TreeScope]
$IPC = [System.Windows.Automation.InvokePattern]
function Find-ById([long]$hwnd, [string]$id) {
    $root = $AE::FromHandle([IntPtr]$hwnd)
    if ($null -eq $root) { return $null }
    return $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)))
}
function Invoke-ClickEl($el) {
    if ($null -eq $el) { return $false }
    $pat = $null
    if ($el.TryGetCurrentPattern($IPC::Pattern, [ref]$pat)) { try { $pat.Invoke(); return $true } catch { return $false } }
    return $false
}
function Invoke-Click([long]$hwnd, [string]$id) {
    for ($k = 1; $k -le 4; $k++) {
        $el = Find-ById $hwnd $id
        if ($null -ne $el) { if (Invoke-ClickEl $el) { return $true } }
        Start-Sleep -Milliseconds 400
    }
    return $false
}
function Get-Row([long]$hwnd, [string]$rowText) {
    $lv = Find-ById $hwnd 'ListPlugins'
    if ($null -eq $lv) { return $null }
    $condLI = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $condTx = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    foreach ($it in $lv.FindAll($TS::Descendants, $condLI)) {
        $txt = ''
        foreach ($t in $it.FindAll($TS::Descendants, $condTx)) { $txt += ' ' + $t.Current.Name }
        if ($txt.Contains($rowText)) { return $it }
    }
    return $null
}
function Get-RowBtn($row, [string]$name) {
    if ($null -eq $row) { return $null }
    $condB = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    foreach ($b in $row.FindAll($TS::Descendants, $condB)) { if ($b.Current.Name -eq $name) { return $b } }
    return $null
}
function Get-Dialog([long]$hwnd) {
    $root = $AE::FromHandle([IntPtr]$hwnd)
    if ($null -eq $root) { return $null }
    $cClass = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'Popup')
    $cType = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    $and = New-Object System.Windows.Automation.AndCondition($cType, $cClass)
    $cb = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    foreach ($el in $root.FindAll($TS::Descendants, $and)) {
        if ($el.FindAll($TS::Descendants, $cb).Count -gt 0) { return $el }
    }
    return $null
}
function Save-Shot([long]$hwnd, [string]$name) {
    $r = New-Object Win32U+RECT
    [void][Win32U]::GetWindowRect([IntPtr]$hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $bmp.Save((Join-Path $Out ($name + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

$state = Join-Path $env:LOCALAPPDATA 'DshController'
$reg = Join-Path $state 'instances.json'
$backup = Join-Path $env:TEMP ('dsh-up-backup-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
$homeU = Join-Path $env:TEMP 'dsh-up-home-u'
$pkg = '@tt-a1i/archify-dsh'
try {
    $meta = Invoke-RestMethod -Uri ('https://registry.npmjs.org/' + [Uri]::EscapeDataString($pkg) + '/latest') -TimeoutSec 20
    $latest = [string]$meta.version
    if (-not $latest -or $latest -notmatch '^[0-9]') { throw ('cannot resolve latest version for ' + $pkg) }
    $fakeOld = '0.0.1'
    Write-Output ('  fixture: latest=' + $latest + ' fabricated installed=' + $fakeOld)

    if (Test-Path $homeU) { Remove-Item -Recurse -Force $homeU }
    New-Item -ItemType Directory -Force -Path (Join-Path $homeU 'profiles\web') | Out-Null
    $modDir = Join-Path $homeU ('profiles\web\node_modules\' + $pkg.Replace('/', '\'))   # PkgToRelPath 保留 @ 前缀（只转斜杠）
    New-Item -ItemType Directory -Force -Path $modDir | Out-Null
    $depObj = @{ name='profile' }
    $depObj.dependencies = @{ $pkg = '*' }
    [IO.File]::WriteAllText((Join-Path $homeU 'profiles\web\package.json'), ($depObj | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $modDir 'package.json'), (@{ name=$pkg; version=$fakeOld } | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))

    if (Test-Path $reg) { Copy-Item $reg $backup -Force } else { Set-Content -Path $backup -Value '{}' }
    $doc = $null
    try { $doc = Get-Content $reg -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $doc = $null }
    if ($null -eq $doc) { $doc = [pscustomobject]@{ version = 2; instances = @() } }
    $existing = @(); if ($null -ne $doc.instances) { $existing = @($doc.instances) }
    $u = [pscustomobject]@{ id='RTEST-U'; name='RTEST-U'; port=3185; host='127.0.0.1'; runtime='windows'
        home=$homeU; workspace=$env:TEMP; trustedHosts=@(); autoOpenBrowser=$false; stopOnExit=$true
        createdAt='2026-08-30T00:00:00Z'; lastStartedAt='2026-09-02T03:00:00Z'; wslDistro=''; wslHome=''; harnessVersion='' }
    $doc | Add-Member -NotePropertyName instances -NotePropertyValue (@($existing + @($u))) -Force
    [IO.File]::WriteAllText($reg, ($doc | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
    Get-NetTCPConnection -State Listen -LocalPort 3185 -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }

    $p = Start-Process -FilePath $Exe -PassThru
    $script:currentProc = $p
    $deadline = (Get-Date).AddSeconds($StartupSec); $hwnd = [long]0
    while ((Get-Date) -lt $deadline) { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle.ToInt64(); break }; Start-Sleep -Milliseconds 250 }
    Add-Result ($hwnd -ne 0) 'window-smoke' ('hwnd=' + $hwnd)
    if ($hwnd -eq 0) { throw 'no window' }
    Start-Sleep -Seconds 8

    Invoke-Click $hwnd 'BtnPagePlug' | Out-Null; Start-Sleep -Milliseconds 900
    Invoke-Click $hwnd 'BtnSubManage' | Out-Null; Start-Sleep -Milliseconds 1800
    $lvT = Find-ById $hwnd 'PluginTargetTree'
    $selU = $false
    if ($null -ne $lvT) {
        $condLI = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $condTx = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
        foreach ($i in $lvT.FindAll($TS::Descendants, $condLI)) {
            $txt = ''
            foreach ($tx in $i.FindAll($TS::Descendants, $condTx)) { $txt = $tx.Current.Name; break }
            if ($txt.StartsWith('windows:3185')) {
                $pat = $null
                if ($i.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); $selU = $true }
                break
            }
        }
    }
    Add-Result $selU 'select-fixture' ('tree row windows:3185=' + $selU)

    $row = $null; $hintMs = -1
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 45000) {
        $row = Get-Row $hwnd $pkg
        if ($null -ne $row) {
            if ($null -ne (Get-RowBtn $row $S_UP) -and $null -ne (Get-RowBtn $row $S_UN)) { $hintMs = $sw.ElapsedMilliseconds; break }
        }
        Start-Sleep -Milliseconds 1500
    }
    Add-Result ($hintMs -ge 0) 'upgrade-button-appears' ('lit in ' + $hintMs + 'ms next to uninstall')
    if ($hintMs -lt 0) { Save-Shot $hwnd 'fixture-miss'; throw 'fixture did not light the upgrade button' }
    Save-Shot $hwnd 'fixture-hint'

    [void](Invoke-ClickEl (Get-RowBtn $row $S_UP))
    $dlg = $null
    $swD = [Diagnostics.Stopwatch]::StartNew()
    while ($swD.ElapsedMilliseconds -lt 8000) { $dlg = Get-Dialog $hwnd; if ($null -ne $dlg) { break }; Start-Sleep -Milliseconds 300 }
    if ($null -eq $dlg) { throw 'dialog did not open' }
    $dlgText = ''
    $condTx2 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    foreach ($tx in $dlg.FindAll($TS::Descendants, $condTx2)) { $dlgText += '|' + $tx.Current.Name }
    $hasCur = $dlgText.Contains($S_CUR + ' v' + $fakeOld)
    $hasNew = $dlgText.Contains($S_NEWV + ' v' + $latest)
    $hasHdr = $dlgText.Contains($S_LOG)
    $condB = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $btnNames = @()
    foreach ($b in $dlg.FindAll($TS::Descendants, $condB)) { $btnNames += $b.Current.Name }
    $three = ($btnNames -contains $S_UP) -and ($btnNames -contains $S_IGN) -and ($btnNames -contains $S_CAN)
    Add-Result ($hasCur -and $hasNew -and $hasHdr) 'dialog-content-version-pair' ('cur=' + $hasCur + ' new=' + $hasNew + ' header=' + $hasHdr)
    Add-Result $three 'dialog-three-buttons' ('buttons=[' + ($btnNames -join ',') + ']')
    Write-Output ('  changelog branch: placeholder-shown=' + $dlgText.Contains($S_PLACE) + ' (either branch valid per spec)')
    Save-Shot $hwnd 'dialog-open'

    [void](Invoke-ClickEl (Get-RowBtn (Get-Dialog $hwnd) $S_IGN))
    Start-Sleep -Milliseconds 3500
    $row2 = Get-Row $hwnd $pkg
    $gone = $true
    if ($null -ne $row2) { if ($null -ne (Get-RowBtn $row2 $S_UP)) { $gone = $false } }
    Add-Result $gone 'ignore-hides-button' ('button gone after ignore=' + $gone)

    Invoke-Click $hwnd 'BtnPageInst' | Out-Null; Start-Sleep -Milliseconds 800
    Invoke-Click $hwnd 'BtnPagePlug' | Out-Null; Invoke-Click $hwnd 'BtnSubManage' | Out-Null
    Start-Sleep -Seconds 4
    $row3 = Get-Row $hwnd $pkg
    $stillGone = $true
    if ($null -ne $row3) { if ($null -ne (Get-RowBtn $row3 $S_UP)) { $stillGone = $false } }
    Add-Result $stillGone 'ignore-persists-reentry' ('still hidden after re-entry=' + $stillGone)
    Save-Shot $hwnd 'after-ignore'

    $p.Refresh(); [void]$p.CloseMainWindow()
    $closed = $p.WaitForExit(15000)
    Add-Result $closed 'close' 'closed-ok'
}
catch {
    Write-Output ('CAUGHT line ' + $_.InvocationInfo.ScriptLineNumber + ': ' + $_.Exception.Message)
    $global:upFailed = $true
}
finally {
    if ($script:currentProc -and -not $script:currentProc.HasExited) { Stop-Process -Id $script:currentProc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    if (Test-Path $backup) { Copy-Item $backup $reg -Force; Remove-Item $backup -Force -ErrorAction SilentlyContinue; Write-Output 'registry restored' }
    Get-ChildItem (Join-Path $state 'archives') -Filter 'RTEST-*' -ErrorAction SilentlyContinue | Remove-Item -Force
    Remove-Item -Recurse -Force $homeU -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path $state 'upgrade-ignores.json') -ErrorAction SilentlyContinue
    $crash = Get-ChildItem (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'DshController\error-reports') -Filter '*-crash_*.md' -ErrorAction SilentlyContinue |
             Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-6) }
    if ($crash) { $crash | ForEach-Object { Write-Output ('CRASH-REPORT: ' + $_.Name) }; $global:upFailed = $true }
}
$bad = @($results | Where-Object { -not $_.Ok }).Count + $(if ($global:upFailed) { 1 } else { 0 })
Write-Output ('SUMMARY total=' + $results.Count + ' fail=' + $bad)
if ($bad) { $results | Where-Object { -not $_.Ok } | ForEach-Object { Write-Output ('FAILED: ' + $_.Id + ' :: ' + $_.Detail) }; exit 1 }
exit 0
