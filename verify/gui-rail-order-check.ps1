# ============================================================================
#  gui-rail-order-check.ps1 - instances-rail launch-order live check
#  (sub-task "launch-ordered list"; reusable tool asset for the instances page)
#  ASCII-only source: PS 5.1 misreads BOM-less UTF-8; CJK UI strings are built
#  from char codes. Self-restoring: backups instances.json, injects RTEST-A
#  (stamped 2026-09-01) / RTEST-B (stamped 2026-08-01) on ports 3185/3186 with
#  temp HOMEs, launches GUI, checks order + row-click no-misfire, STARTS B via
#  the real UI, checks it jumps to top, stops it, restores everything.
#  Usage: powershell -ExecutionPolicy Bypass -File verify\gui-rail-order-check.ps1 -Exe <exe> -Out <dir>
# ============================================================================
param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [Parameter(Mandatory=$true)][string]$Out,
    [int]$StartupSec = 25
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Win32R {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
'@
[void][Win32R]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$results = @()
$script:currentProc = $null
$S_RUN  = [string]([char]0x8FD0) + [char]0x884C + [char]0x4E2D    # running state text
$S_STOP = [string]([char]0x5DF2) + [char]0x505C + [char]0x6B62    # stopped state text
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
function Invoke-Click([long]$hwnd, [string]$id) {
    for ($try = 1; $try -le 3; $try++) {
        $el = Find-ById $hwnd $id
        if ($null -ne $el) {
            $pat = $null
            if ($el.TryGetCurrentPattern($IPC::Pattern, [ref]$pat)) { $pat.Invoke(); return }
        }
        Start-Sleep -Milliseconds 300
    }
    throw ('element not clickable: ' + $id)
}
function Get-ItemText($item) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $col = $item.FindAll($TS::Descendants, $cond)
    $s = ''
    foreach ($t in $col) { $s += ' ' + $t.Current.Name }
    return $s.Trim()
}
function Get-RailItems([long]$hwnd) {
    $lv = Find-ById $hwnd 'RailList'
    if ($null -eq $lv) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $col = $lv.FindAll($TS::Descendants, $cond)
    $out = @(); foreach ($i in $col) { $out += $i }
    return $out
}
function Get-RailOrder([long]$hwnd) {
    $parts = @()
    foreach ($i in @(Get-RailItems $hwnd)) { $parts += (Get-ItemText $i) }
    return ($parts -join ' || ')
}
function Select-RailRow([long]$hwnd, [string]$contains) {
    foreach ($i in @(Get-RailItems $hwnd)) {
        if ((Get-ItemText $i) -like ('*' + $contains + '*')) {
            $pat = $null
            if ($i.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); return $true }
        }
    }
    return $false
}
function Get-SelectedRailText([long]$hwnd) {
    # WinUI ListItem.Current.Selected is not exposed over UIA; use the SelectionPattern set instead
    $lv = Find-ById $hwnd 'RailList'
    if ($null -eq $lv) { return '' }
    $sp = $null
    if (-not $lv.TryGetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern, [ref]$sp)) { return '' }
    $sel = $sp.Current.GetSelection()
    $out = ''
    foreach ($e in $sel) { $out += (Get-ItemText $e) + ' ' }
    return $out
}
function Select-ComboItem([long]$hwnd, [string]$comboId, [string]$contains) {
    $cmb = Find-ById $hwnd $comboId
    if ($null -eq $cmb) { return $false }
    $exp = $null
    if ($cmb.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$exp)) { $exp.Expand(); Start-Sleep -Milliseconds 500 }
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $col = $cmb.FindAll($TS::Descendants, $cond)
    $done = $false
    foreach ($i in $col) {
        if ($i.Current.Name -like ('*' + $contains + '*')) {
            $pat = $null
            if ($i.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); $done = $true; break }
        }
    }
    if ($null -ne $exp) { try { $exp.Collapse() } catch { } }
    return $done
}
function Test-PortListening([int]$port) {
    return [bool](Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
}
function Save-Shot([long]$hwnd, [string]$name) {
    $r = New-Object Win32R+RECT
    [void][Win32R]::GetWindowRect([IntPtr]$hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $sz = New-Object System.Drawing.Size($w, $h)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $sz)
    $bmp.Save((Join-Path $Out ($name + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
function Wait-RailState([long]$hwnd, [string]$rowContains, [string]$stateText, [int]$timeoutMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $order = Get-RailOrder $hwnd
        $idx = $order.IndexOf($rowContains)
        if ($idx -ge 0) {
            $segEnd = $order.IndexOf(' || ', $idx); if ($segEnd -lt 0) { $segEnd = $order.Length }
            $seg = $order.Substring($idx, $segEnd - $idx)
            if ($seg.Contains($stateText)) { return $true }
        }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

$state = Join-Path $env:LOCALAPPDATA 'DshController'
$reg = Join-Path $state 'instances.json'
$backup = Join-Path $env:TEMP ('dsh-rail-backup-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
$homeA = Join-Path $env:TEMP 'dsh-rail-home-a'
$homeB = Join-Path $env:TEMP 'dsh-rail-home-b'
New-Item -ItemType Directory -Force -Path $homeA, $homeB | Out-Null

try {
    if (Test-Path $reg) { Copy-Item $reg $backup -Force } else { Set-Content -Path $backup -Value '{}' }
    $doc = $null
    try { $doc = Get-Content $reg -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $doc = $null }
    if ($null -eq $doc) { $doc = [pscustomobject]@{ version = 2; instances = @() } }
    $existing = @(); if ($null -ne $doc.instances) { $existing = @($doc.instances) }
    $a = [pscustomobject]@{ id='RTEST-A'; name='RTEST-A'; port=3185; host='127.0.0.1'; runtime='windows'
        home=$homeA; workspace=$env:TEMP; trustedHosts=@(); autoOpenBrowser=$false; stopOnExit=$true
        createdAt='2026-08-20T00:00:00Z'; lastStartedAt='2026-09-01T09:00:00Z'; wslDistro=''; wslHome=''; harnessVersion='' }
    $b = [pscustomobject]@{ id='RTEST-B'; name='RTEST-B'; port=3186; host='127.0.0.1'; runtime='windows'
        home=$homeB; workspace=$env:TEMP; trustedHosts=@(); autoOpenBrowser=$false; stopOnExit=$true
        createdAt='2026-08-25T00:00:00Z'; lastStartedAt='2026-08-01T09:00:00Z'; wslDistro=''; wslHome=''; harnessVersion='' }
    $doc | Add-Member -NotePropertyName instances -NotePropertyValue (@($existing + @($a, $b))) -Force
    [IO.File]::WriteAllText($reg, ($doc | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))
    Write-Output ('injected: ' + $reg)

    $p = Start-Process -FilePath $Exe -PassThru
    $script:currentProc = $p
    $deadline = (Get-Date).AddSeconds($StartupSec); $hwnd = [long]0
    while ((Get-Date) -lt $deadline) {
        $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle.ToInt64(); break }
        Start-Sleep -Milliseconds 250
    }
    Add-Result ($hwnd -ne 0) 'window-smoke' ('hwnd=' + $hwnd)
    if ($hwnd -eq 0) { throw 'no main window' }
    Start-Sleep -Seconds 8

    $order0 = Get-RailOrder $hwnd
    $iA = $order0.IndexOf('RTEST-A'); $iB = $order0.IndexOf('RTEST-B')
    Add-Result (($iA -ge 0) -and ($iB -gt $iA)) 'order-initial' ('idxA=' + $iA + ' idxB=' + $iB)
    # naming: env:port labels + original name (revamp sub-task instance-env-port-naming)
    Add-Result ($order0 -match 'windows:3185') 'naming-env-port' ('row shows windows:3185; row0=' + (($order0 -split '\\|\|')[0]))
    # original-name proof: row line-2 always shows the original name (stronger than hover);
    # programmatic hover is not honored by WinUI pointer input, so the tooltip popup itself
    # is a human-eye ticket - string generation is unit-tested and XAML wires
    # ToolTipService.ToolTip="{x:Bind TooltipText}". Keep this file ASCII-only: PS 5.1
    # reads BOM-less UTF-8 as GBK and CJK comments can swallow the next line.
    Add-Result ($order0 -match 'windows:3185\s+RTEST-A') 'naming-original-visible' ('row line2 = original name; row0=' + (($order0 -split '\\|\|')[0]).Trim())
    # combo items are env:port too
    $cmbx = Find-ById $hwnd 'CmbInstance'
    $exp2 = $null
    if ($cmbx.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$exp2)) { $exp2.Expand(); Start-Sleep -Milliseconds 400 }
    $condT = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $cmbTexts = ''
    if ($null -ne $cmbx) { $col2 = $cmbx.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))); foreach ($ci in $col2) { $cmbTexts += (Get-ItemText $ci) + ' ~ ' }; }
    if ($null -ne $exp2) { try { $exp2.Collapse() } catch { } }
    Add-Result ($cmbTexts -match 'windows:3185') 'naming-combo-item' ('combo items=' + $cmbTexts.Substring(0, [Math]::Min(160, $cmbTexts.Length)))
    Write-Output ('  order: ' + $order0)
    Save-Shot $hwnd 'rail-initial'

    [void](Select-RailRow $hwnd 'RTEST-A'); Start-Sleep -Milliseconds 600
    [void](Select-RailRow $hwnd 'RTEST-B'); Start-Sleep -Milliseconds 800
    $selB = (Get-SelectedRailText $hwnd) -like '*RTEST-B*'
    $silent3185 = -not (Test-PortListening 3185)
    $silent3186 = -not (Test-PortListening 3186)
    Add-Result ($selB -and $silent3185 -and $silent3186) 'row-click-no-misfire' ('selectedB=' + $selB + ' 3185 silent=' + $silent3185 + ' 3186 silent=' + $silent3186)

    $picked = Select-ComboItem $hwnd 'CmbInstance' '3186'
    Add-Result $picked 'combo-select-B' 'CmbInstance -> item matching :3186 (env:port label)'
    Start-Sleep -Seconds 2
    Invoke-Click $hwnd 'BtnStart'
    $started = Wait-RailState $hwnd 'RTEST-B' $S_RUN 60000
    Add-Result $started 'start-B-running' ('row shows running=' + $started)
    $up3186 = Test-PortListening 3186
    Add-Result $up3186 'start-B-portup' ('3186 listening=' + $up3186)
    Save-Shot $hwnd 'rail-after-start'

    Invoke-Click $hwnd 'BtnPagePlug'; Start-Sleep -Milliseconds 500
    Invoke-Click $hwnd 'BtnPageInst'; Start-Sleep -Milliseconds 900
    $order1 = Get-RailOrder $hwnd
    $iB1 = $order1.IndexOf('RTEST-B'); $iA1 = $order1.IndexOf('RTEST-A')
    Add-Result (($iB1 -ge 0) -and (($iA1 -lt 0) -or ($iB1 -lt $iA1))) 'order-jumped' ('idxB=' + $iB1 + ' idxA=' + $iA1)
    Write-Output ('  order: ' + $order1)
    Save-Shot $hwnd 'rail-jumped'

    [void](Select-ComboItem $hwnd 'CmbInstance' '3186')
    Invoke-Click $hwnd 'BtnStop'
    $stopped = Wait-RailState $hwnd 'RTEST-B' $S_STOP 30000
    Add-Result $stopped 'stop-B' ('row shows stopped=' + $stopped)
    $p.Refresh(); [void]$p.CloseMainWindow(); [void]$p.WaitForExit(15000)
}
catch {
    Write-Output ('CAUGHT at line ' + $_.InvocationInfo.ScriptLineNumber + ': ' + $_.Exception.Message)
    $global:railCheckFailed = $true
}
finally {
    if ($script:currentProc -and -not $script:currentProc.HasExited) { Stop-Process -Id $script:currentProc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    if (Test-Path $backup) { Copy-Item $backup $reg -Force; Remove-Item $backup -Force -ErrorAction SilentlyContinue; Write-Output 'registry restored' }
    Get-ChildItem (Join-Path $state 'archives') -Filter 'RTEST-*' -ErrorAction SilentlyContinue | Remove-Item -Force
    Remove-Item -Recurse -Force $homeA, $homeB -ErrorAction SilentlyContinue
    Get-NetTCPConnection -State Listen -LocalPort 3185,3186 -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    $crash = Get-ChildItem (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'DshController\error-reports') -Filter '*-crash_*.md' -ErrorAction SilentlyContinue |
             Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-3) }
    if ($crash) { $crash | ForEach-Object { Write-Output ('CRASH-REPORT: ' + $_.Name) } ; $global:railCheckFailed = $true }
}
$fail = @($results | Where-Object { -not $_.Ok })
if ($global:railCheckFailed) { $fail += [pscustomobject]@{ Ok = $false; Id = 'exception'; Detail = 'see CAUGHT/CRASH-REPORT lines' } }
Write-Output ('SUMMARY total=' + $results.Count + ' pass=' + ($results.Count - @($results | Where-Object { -not $_.Ok }).Count) + ' fail=' + (@($results | Where-Object { -not $_.Ok }).Count + $(if ($global:railCheckFailed) { 1 } else { 0 })))
if ($fail.Count) { exit 1 }
exit 0
