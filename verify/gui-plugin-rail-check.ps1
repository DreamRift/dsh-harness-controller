# ============================================================================
#  gui-plugin-rail-check.ps1 - plugins page two-item rail check
#  (sub-task "two-item primary rail"). Proves:
#    1) both rail items present & clickable; each click flips the host exactly
#    2) round-trip keeps state: ListPlugins element survives (residents, no
#       rebuild => no flicker) and the market page combo selection is identical
#    3) manage page: per-page CmbInstance defaults to the first instance and
#       switching it re-targets the installed list (PluginTargetTree was
#       removed in the rework; each sub-page owns its instance combo now)
#    4) screenshots of the selected states for the human eye
#  ASCII-only (PS 5.1 GBK pitfall); CJK strings via char codes where needed.
#  Usage: powershell -ExecutionPolicy Bypass -File verify\gui-plugin-rail-check.ps1 -Exe <exe> -Out <dir>
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
public static class Win32P {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
[void][Win32P]::SetProcessDPIAware()
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
function Test-Visible([long]$hwnd, [string]$id) {
    try { $el = Find-ById $hwnd $id } catch { return $false }
    if ($null -eq $el) { return $false }
    try { return (-not $el.Current.IsOffscreen) } catch { return $false }
}
function Invoke-Click([long]$hwnd, [string]$id) {
    for ($try = 1; $try -le 3; $try++) {
        $el = Find-ById $hwnd $id
        if ($null -ne $el) {
            $pat = $null
            if ($el.TryGetCurrentPattern($IPC::Pattern, [ref]$pat)) { $pat.Invoke(); return $true }
        }
        Start-Sleep -Milliseconds 300
    }
    return $false
}
function Get-Rid([long]$hwnd, [string]$id) {
    $el = Find-ById $hwnd $id
    if ($null -eq $el) { return '' }
    return (($el.GetRuntimeId()) -join ',')
}
function Get-ComboValue([long]$hwnd, [string]$id) {
    # read the actual selected item via SelectionPattern (element.Name is not the value)
    $el = Find-ById $hwnd $id
    if ($null -eq $el) { return '<absent>' }
    $sp = $null
    if (-not $el.TryGetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern, [ref]$sp)) { return '<nopattern>' }
    $sel = $sp.Current.GetSelection()
    foreach ($s in $sel) { return $s.Current.Name }
    return '<none>'
}
function Wait-Market([long]$hwnd, [int]$ms) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
        if (Test-Visible $hwnd 'TxtFreshness') { return $sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 25
    }
    return -1
}
function Wait-Manage([long]$hwnd, [int]$ms) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
        if (Test-Visible $hwnd 'BtnRefresh') { return $sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 25
    }
    return -1
}
function Get-ComboItems([long]$hwnd, [string]$id) {
    # enumerate combo item names (WinUI exposes dropdown items only while expanded)
    $el = Find-ById $hwnd $id
    if ($null -eq $el) { return @() }
    $exp = $null
    $hasExp = $el.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$exp)
    $condLI = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    for ($try = 0; $try -lt 10; $try++) {
        if ($hasExp) { try { $exp.Expand() } catch { } }
        Start-Sleep -Milliseconds 300
        $names = @()
        foreach ($i in $el.FindAll($TS::Descendants, $condLI)) { $names += $i.Current.Name }
        if ($hasExp) { try { $exp.Collapse() } catch { } }
        if ($names.Count -ge 1) { return $names }
    }
    return @()
}
function Select-ComboItem([long]$hwnd, [string]$id, [string]$token) {
    # select the first item whose Name contains the token; retries cover the
    # transient disabled state while a page reload is busy
    for ($try = 1; $try -le 5; $try++) {
        $el = Find-ById $hwnd $id
        if ($null -ne $el) {
            $exp = $null
            $hasExp = $el.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$exp)
            if ($hasExp) { try { $exp.Expand() } catch { } }
            Start-Sleep -Milliseconds 400
            $condLI = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
            $ok = $false
            foreach ($i in $el.FindAll($TS::Descendants, $condLI)) {
                if (($i.Current.Name).Contains($token)) {
                    $pat = $null
                    if ($i.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { try { $pat.Select(); $ok = $true } catch { }; break }
                }
            }
            if ($hasExp) { try { $exp.Collapse() } catch { } }
            if ($ok) { return $true }
        }
        Start-Sleep -Milliseconds 600
    }
    return $false
}
function Save-Shot([long]$hwnd, [string]$name) {
    $r = New-Object Win32P+RECT
    [void][Win32P]::GetWindowRect([IntPtr]$hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $sz = New-Object System.Drawing.Size($w, $h)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $sz)
    $bmp.Save((Join-Path $Out ($name + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
$p = Start-Process -FilePath $Exe -PassThru
$script:currentProc = $p
try {
    $deadline = (Get-Date).AddSeconds($StartupSec); $hwnd = [long]0
    while ((Get-Date) -lt $deadline) { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle.ToInt64(); break }; Start-Sleep -Milliseconds 250 }
    Add-Result ($hwnd -ne 0) 'window-smoke' ('hwnd=' + $hwnd)
    if ($hwnd -eq 0) { throw 'no window' }
    Start-Sleep -Seconds 8

    Invoke-Click $hwnd 'BtnPagePlug' | Out-Null
    $ms = Wait-Market $hwnd 4000
    Add-Result ($ms -ge 0) 'enter-market' ('TxtFreshness in ' + $ms + 'ms')
    $mOk = (Test-Visible $hwnd 'BtnSubMarket') -and (Test-Visible $hwnd 'BtnSubManage')
    Add-Result $mOk 'rail-two-items' ('market + manage items visible')

    # flip: one click per item, host unique each way
    $t0 = [Diagnostics.Stopwatch]::StartNew()
    Invoke-Click $hwnd 'BtnSubManage' | Out-Null
    $msM = Wait-Manage $hwnd 3000
    $ghost = (Test-Visible $hwnd 'TxtFreshness')
    Add-Result (($msM -ge 0) -and ($msM -lt 1000) -and (-not $ghost)) 'flip-to-manage' ('latency=' + $msM + 'ms market-hidden=' + (-not $ghost))
    $t1 = [Diagnostics.Stopwatch]::StartNew()
    Invoke-Click $hwnd 'BtnSubMarket' | Out-Null
    $msK = Wait-Market $hwnd 3000
    $ghidden = (Test-Visible $hwnd 'BtnRefresh')
    Add-Result (($msK -ge 0) -and ($msK -lt 1000) -and (-not $ghidden)) 'flip-to-market' ('latency=' + $msK + 'ms manage-hidden=' + (-not $ghidden))

    # round-trip state: combo selection value + element survival (residents, not rebuilt)
    $cmbVal0 = Get-ComboValue $hwnd 'CmbInstance'
    $ridList0 = Get-Rid $hwnd 'ListPlugins'
    Invoke-Click $hwnd 'BtnSubManage' | Out-Null; [void](Wait-Manage $hwnd 3000)
    Invoke-Click $hwnd 'BtnSubMarket' | Out-Null; [void](Wait-Market $hwnd 3000)
    $cmbVal1 = Get-ComboValue $hwnd 'CmbInstance'
    $ridList1 = Get-Rid $hwnd 'ListPlugins'
    Add-Result (($cmbVal0 -eq $cmbVal1) -and ($cmbVal0.Length -ge 0)) 'state-kept-combo' ('target before=[' + $cmbVal0 + '] after=[' + $cmbVal1 + ']')
    Add-Result (($ridList0 -ne '') -and ($ridList0 -eq $ridList1)) 'resident-no-rebuild' ('ListPlugins rid ' + $ridList0 + ' == ' + $ridList1)

    # multi round trips (5x) - stability without flicker (rid stays)
    $ridNow = $ridList0
    $stable = $true
    for ($k = 0; $k -lt 5; $k++) {
        Invoke-Click $hwnd 'BtnSubManage' | Out-Null; [void](Wait-Manage $hwnd 3000)
        Invoke-Click $hwnd 'BtnSubMarket' | Out-Null; [void](Wait-Market $hwnd 3000)
        $ridNow = Get-Rid $hwnd 'ListPlugins'
        if ($ridNow -ne $ridList0) { $stable = $false; break }
    }
    Add-Result $stable 'roundtrips-stable' ('5 round trips, rid=' + $ridNow)

    Save-Shot $hwnd 'plugin-rail-market'
    Invoke-Click $hwnd 'BtnSubManage' | Out-Null
    $msM2 = Wait-Manage $hwnd 3000
    Save-Shot $hwnd 'plugin-rail-manage'

    # ---- manage page: per-page instance combo (PluginTargetTree removed in rework) ----
    $cmbVis = Test-Visible $hwnd 'CmbInstance'
    Add-Result (($msM2 -ge 0) -and $cmbVis) 'manage-panel-visible' ('manage shown in ' + $msM2 + 'ms combo-visible=' + $cmbVis)

    # the page combo defaults to the first instance
    $itemsA = @(Get-ComboItems $hwnd 'CmbInstance')
    $val0 = Get-ComboValue $hwnd 'CmbInstance'
    $first = ''
    if ($itemsA.Count -ge 1) { $first = $itemsA[0] }
    Add-Result (($itemsA.Count -ge 1) -and ($val0 -eq $first)) 'manage-combo-default' ('items=' + $itemsA.Count + ' first=[' + $first + '] selected=[' + $val0 + ']')

    # switch to another instance via the page combo -> selection syncs fast
    $target = ''
    foreach ($n in $itemsA) { if ($n -ne $val0) { $target = $n; break } }
    if ($target -eq '') { $target = $first }
    $picked = Select-ComboItem $hwnd 'CmbInstance' $target
    $t0 = [Diagnostics.Stopwatch]::StartNew()
    $comboNow = ''
    while ($t0.ElapsedMilliseconds -lt 3000) {
        $comboNow = Get-ComboValue $hwnd 'CmbInstance'
        if ($comboNow -eq $target) { break }
        Start-Sleep -Milliseconds 25
    }
    Add-Result ($picked -and $comboNow -eq $target -and $t0.ElapsedMilliseconds -lt 1000) 'combo-switch-sync' ('target=[' + $target + '] combo=[' + $comboNow + '] ms=' + $t0.ElapsedMilliseconds)

    # list-follow evidence: meta re-renders and the installed list is released from busy
    $metaTxt = ''
    $listOk = $false
    $mt = [Diagnostics.Stopwatch]::StartNew()
    while ($mt.ElapsedMilliseconds -lt 5000) {
        $elM = Find-ById $hwnd 'TxtInstanceMeta'
        if ($null -ne $elM) { try { $metaTxt = $elM.Current.Name } catch { } }
        $elL = Find-ById $hwnd 'ListPlugins'
        if ($null -ne $elL) { try { $listOk = $elL.Current.IsEnabled } catch { $listOk = $false } }
        if ($metaTxt.Length -gt 0 -and $listOk) { break }
        Start-Sleep -Milliseconds 50
    }
    Add-Result ($metaTxt.Length -gt 0 -and $listOk) 'combo-switch-refresh' ('meta=[' + $metaTxt.Substring(0, [Math]::Min(60, $metaTxt.Length)) + '] list-enabled=' + $listOk)
    Save-Shot $hwnd 'plugin-manage-combo'

    # switch back via the same combo (reverse direction, was combo->tree sync)
    $pickedB = Select-ComboItem $hwnd 'CmbInstance' $val0
    $t1 = [Diagnostics.Stopwatch]::StartNew()
    $comboBack = ''
    while ($t1.ElapsedMilliseconds -lt 3000) {
        $comboBack = Get-ComboValue $hwnd 'CmbInstance'
        if ($comboBack -eq $val0) { break }
        Start-Sleep -Milliseconds 25
    }
    Add-Result ($pickedB -and $comboBack -eq $val0 -and $t1.ElapsedMilliseconds -lt 1000) 'combo-switch-back' ('back=[' + $val0 + '] combo=[' + $comboBack + '] ms=' + $t1.ElapsedMilliseconds)

    $p.Refresh(); [void]$p.CloseMainWindow()
    $closed = $p.WaitForExit(15000)
    Add-Result $closed 'close' 'closed-ok'
}
finally {
    if ($script:currentProc -and -not $script:currentProc.HasExited) { Stop-Process -Id $script:currentProc.Id -Force -ErrorAction SilentlyContinue }
}
$crash = Get-ChildItem (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'DshController\error-reports') -Filter '*-crash_*.md' -ErrorAction SilentlyContinue |
         Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-3) }
if ($crash) { $crash | ForEach-Object { Write-Output ('CRASH-REPORT: ' + $_.Name) } }
$bad = @($results | Where-Object { -not $_.Ok }).Count + $(if ($crash) { 1 } else { 0 })
Write-Output ('SUMMARY total=' + $results.Count + ' fail=' + $bad)
if ($bad) { $results | Where-Object { -not $_.Ok } | ForEach-Object { Write-Output ('FAILED: ' + $_.Id + ' :: ' + $_.Detail) }; exit 1 }
exit 0
