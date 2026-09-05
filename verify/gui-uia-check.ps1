# ============================================================================
#  gui-uia-check.ps1 - DshController GUI probe (reusable verification tool,
#  introduced by the top-bar navigation revamp round).
#  Usage: powershell -ExecutionPolicy Bypass -File verify\gui-uia-check.ps1 -Exe <exe> -Out <dir>
#  Host visibility markers (WinUI prunes collapsed subtrees from the UIA
#  control view; UserControls are not control elements, so we probe named
#  descendants):
#    PanelWin / PanelWsl -> TxtPageTitle (Name carries 'Windows'/'WSL')
#    PanelMarket -> TxtFreshness | PanelPlugins -> BtnRefresh
#    PanelUsage -> UsageHost | PageSettings -> PageSettings
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
public static class Win32Probe {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
[void][Win32Probe]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$results = @()
$script:maxLatency = 0
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
function Find-AllById([long]$hwnd, [string]$id) {
    $root = $AE::FromHandle([IntPtr]$hwnd)
    if ($null -eq $root) { return @() }
    $col = $root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)))
    $out = @(); foreach ($el in $col) { $out += $el }
    return $out
}
function Test-Visible([long]$hwnd, [string]$id) {
    try { $el = Find-ById $hwnd $id } catch { return $false }
    if ($null -eq $el) { return $false }
    try { return (-not $el.Current.IsOffscreen) } catch { return $false }
}
function Select-RailRowByPrefix([long]$hwnd, [string]$prefix) {
    # 改版·详情操作主区：环境子页由左栏行选中驱动
    $lv = Find-ById $hwnd 'RailList'
    if ($null -eq $lv) { return $false }
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $col = $lv.FindAll($TS::Descendants, $cond)
    $condText = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    foreach ($i in $col) {
        # ListItem.Name 是 ToString()，行文本要读 Text 子元素（首元素=显示名）
        $tc = $i.FindAll($TS::Descendants, $condText)
        $first = ''
        foreach ($tx in $tc) { $first = $tx.Current.Name; break }
        if ($first.StartsWith($prefix)) {
            $pat = $null
            if ($i.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); return $true }
        }
    }
    return $false
}
function Wait-AnyInst([long]$hwnd, [int]$timeoutMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $vh = @(Get-Hosts $hwnd)
        if ($vh.Count -eq 1 -and (($vh[0] -eq 'PanelWin') -or ($vh[0] -eq 'PanelWsl'))) { return $sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 50
    }
    return -1
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
    throw ('element not clickable after retries: ' + $id)
}
function Get-Hosts([long]$hwnd) {
    $v = @()
    $titles = @(Find-AllById $hwnd 'TxtPageTitle') | Where-Object { -not $_.Current.IsOffscreen }
    foreach ($t in $titles) {
        $n = $t.Current.Name
        if ($n -match 'Windows') { $v += 'PanelWin' }
        elseif ($n -match 'WSL') { $v += 'PanelWsl' }
    }
    if (Test-Visible $hwnd 'TxtFreshness') { $v += 'PanelMarket' }
    if (Test-Visible $hwnd 'BtnRefresh')   { $v += 'PanelPlugins' }
    if (Test-Visible $hwnd 'UsageHost')    { $v += 'PanelUsage' }
    if (Test-Visible $hwnd 'PageSettings') { $v += 'PageSettings' }
    return $v
}
function Wait-Host([long]$hwnd, [string]$expect, [int]$timeoutMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $vh = @(Get-Hosts $hwnd)
        if ($expect -eq '') { if ($vh.Count -eq 0) { return $sw.ElapsedMilliseconds } }
        elseif ($vh.Count -eq 1 -and $vh[0] -eq $expect) { return $sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 25
    }
    return -1
}
# cheap wait for "no host": poll the 5 single-id markers (one FindFirst each)
function Wait-NoHost([long]$hwnd, [int]$timeoutMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $any = $false
        foreach ($id in @('TxtPageTitle','TxtFreshness','BtnRefresh','UsageHost','PageSettings')) {
            if (Test-Visible $hwnd $id) { $any = $true; break }
        }
        if (-not $any) { return $sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 25
    }
    return -1
}
function Save-Shot([long]$hwnd, [string]$name) {
    $r = New-Object Win32Probe+RECT
    [void][Win32Probe]::GetWindowRect([IntPtr]$hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $sz = New-Object System.Drawing.Size($w, $h)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $sz)
    $path = Join-Path $Out ($name + '.png')
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
function Step([long]$hwnd, [string]$btn, [string]$expect, [string]$name, [string]$tag) {
    $lbl = 'switch-' + $name + '-' + $tag
    $eshow = $expect; if ($eshow -eq '') { $eshow = '<none>' }
    try { Invoke-Click $hwnd $btn } catch { Add-Result $false $lbl $_.Exception.Message; return }
    if ($expect -eq '') { $ms = Wait-NoHost $hwnd 5000 } else { $ms = Wait-Host $hwnd $expect 3000 }
    $vh = @(Get-Hosts $hwnd)
    if ($ms -gt $script:maxLatency) { $script:maxLatency = $ms }
    $ok = ($ms -ge 0 -and $ms -lt 1000) -and (($expect -eq '' -and $vh.Count -eq 0) -or ($expect -ne '' -and $vh.Count -eq 1 -and $vh[0] -eq $expect))
    Add-Result $ok $lbl ('btn=' + $btn + ' expect=' + $eshow + ' latency=' + $ms + 'ms hosts=' + ($vh -join '+'))
}
function Run-Pass([bool]$devMode) {
    $tag = 'normal'; if ($devMode) { $tag = 'dev' }
    if ($devMode) { $p = Start-Process -FilePath $Exe -ArgumentList '--dev' -PassThru }
    else { $p = Start-Process -FilePath $Exe -PassThru }
    $script:currentProc = $p
    $deadline = (Get-Date).AddSeconds($StartupSec); $hwnd = [long]0
    while ((Get-Date) -lt $deadline) {
        $p.Refresh()
        if ($p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle.ToInt64(); break }
        Start-Sleep -Milliseconds 250
    }
    if ($hwnd -eq 0) { Add-Result $false ('startup-' + $tag) 'no main window'; return }
    Start-Sleep -Seconds 6
    Add-Result $true ('startup-' + $tag) ('hwnd=' + $hwnd)

    $missing = @()
    foreach ($id in @('BtnPageInst','BtnPagePlug','BtnPageArch','BtnPageApi')) {
        if (-not (Test-Visible $hwnd $id)) { $missing += $id }
    }
    Add-Result ($missing.Count -eq 0) ('topbar-buttons-' + $tag) ('missing=' + ($missing -join ','))

    # 改版·详情操作主区：默认页 = 左栏第一行驱动的实例面板（环境一致即正确）
    Start-Sleep -Milliseconds 800
    $vh0 = @(Get-Hosts $hwnd)
    $isInst = ($vh0.Count -eq 1) -and (($vh0[0] -eq 'PanelWin') -or ($vh0[0] -eq 'PanelWsl'))
    $t = @(Find-AllById $hwnd 'TxtPageTitle') | Where-Object { -not $_.Current.IsOffscreen }
    $tname = ''; if ($t.Count -ge 1) { $tname = $t[0].Current.Name }
    $firstRowEnv = ''
    $lv0 = Find-ById $hwnd 'RailList'
    if ($null -ne $lv0) {
        $condLI = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $condTx = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
        foreach ($li0 in $lv0.FindAll($TS::Descendants, $condLI)) {
            foreach ($tx0 in $li0.FindAll($TS::Descendants, $condTx)) { $firstRowEnv = $tx0.Current.Name; break }
            break
        }
    }
    $envOk = ($firstRowEnv -match '^windows:' -and $tname -match 'Windows') -or ($firstRowEnv -match '^wsl:' -and $tname -match 'WSL')
    Add-Result ($isInst -and $envOk) ('default-firstrow-consistent-' + $tag) ('hosts=' + ($vh0 -join '+') + ' title=' + $tname + ' firstRow=' + $firstRowEnv)

    Step $hwnd 'BtnPagePlug'     'PanelMarket'   'plug-market'       $tag
    Step $hwnd 'BtnSubManage'    'PanelPlugins'  'plug-manage'       $tag
    Step $hwnd 'BtnPageArch'     'PanelUsage'    'arch-usage'        $tag
    # 详情主区（改版·实例页收口）：先回实例页，WSL/WIN 子页由左栏行选中驱动，入口不再分叉。
    # 用量页首开有 3s 级加载占住 UI 线程，点击前先让页面落稳（人工节奏本不会连点）。
    Start-Sleep -Seconds 3
    Invoke-Click $hwnd 'BtnPageInst'
    $ms = Wait-AnyInst $hwnd 6000
    Add-Result ($ms -ge 0) ('inst-enter-' + $tag) ('any instance panel within ' + $ms + 'ms hosts=' + ((Get-Hosts $hwnd) -join '+'))
    $pickedWsl = Select-RailRowByPrefix $hwnd 'wsl:'
    $ms = Wait-Host $hwnd 'PanelWsl' 5000
    Add-Result ($pickedWsl -and $ms -ge 0) ('inst-wsl-via-row-' + $tag) ('selected=' + $pickedWsl + ' latency=' + $ms + 'ms hosts=' + ((Get-Hosts $hwnd) -join '+'))
    $pickedWin = Select-RailRowByPrefix $hwnd 'windows:3080'
    $ms = Wait-Host $hwnd 'PanelWin' 5000
    Add-Result ($pickedWin -and $ms -ge 0) ('inst-win-via-row-' + $tag) ('selected=' + $pickedWin + ' latency=' + $ms + 'ms hosts=' + ((Get-Hosts $hwnd) -join '+'))
    # keep-alive 基线：在确定的 PanelWin 状态取 rid
    $rid0 = ''
    $el0 = Find-ById $hwnd 'TxtPageTitle'; if ($el0) { $rid0 = ($el0.GetRuntimeId() -join ',') }
    Step $hwnd 'BtnPagePlug'     'PanelPlugins'  'leave-remembers-plug-sub' $tag
    Start-Sleep -Milliseconds 800
    Invoke-Click $hwnd 'BtnPageInst'
    $ms = Wait-Host $hwnd 'PanelWin' 5000
    Add-Result ($ms -ge 0) ('inst-remembers-last-row-' + $tag) ('back to inst keeps windows row: ' + $ms + 'ms hosts=' + ((Get-Hosts $hwnd) -join '+'))
    Step $hwnd 'BtnPageApi'      ''              'api-empty'         $tag
    Step $hwnd 'BtnSettingsPage' 'PageSettings'  'settings'          $tag

    $rid1 = ''
    Invoke-Click $hwnd 'BtnPagePlug'; [void](Wait-AnyInst $hwnd 2000 -ErrorAction SilentlyContinue)
    Invoke-Click $hwnd 'BtnPageInst'
    Start-Sleep -Milliseconds 600
    [void](Wait-Host $hwnd 'PanelWin' 5000)
    $el1 = Find-ById $hwnd 'TxtPageTitle'; if ($el1) { $rid1 = ($el1.GetRuntimeId() -join ',') }
    Add-Result ($rid0 -ne '' -and $rid0 -eq $rid1) ('keep-alive-' + $tag) ('rid t0=' + $rid0 + ' t1=' + $rid1)

    $devVisible = Test-Visible $hwnd 'BtnDevDesk'
    if ($devMode) {
        Add-Result $devVisible ('devdesk-entry-' + $tag) 'visible under --dev'
        if ($devVisible) {
            Step $hwnd 'BtnDevDesk' '' 'gallery-page' $tag
            Save-Shot $hwnd ('page-gallery-' + $tag)
        }
        Invoke-Click $hwnd 'BtnPageInst'; [void](Wait-Host $hwnd 'PanelWin' 2000)
    } else {
        Add-Result (-not $devVisible) ('devdesk-entry-' + $tag) 'hidden without --dev'
    }

    Invoke-Click $hwnd 'BtnPageInst';  [void](Wait-Host $hwnd 'PanelWin' 2000);    Save-Shot $hwnd ('page-instances-' + $tag)
    Invoke-Click $hwnd 'BtnPagePlug';  [void](Wait-Host $hwnd 'PanelPlugins' 2000); Save-Shot $hwnd ('page-plugins-manage-' + $tag)
    Invoke-Click $hwnd 'BtnSubMarket'; [void](Wait-Host $hwnd 'PanelMarket' 2000); Save-Shot $hwnd ('page-plugins-market-' + $tag)
    Invoke-Click $hwnd 'BtnPageArch';  [void](Wait-Host $hwnd 'PanelUsage' 2000);  Save-Shot $hwnd ('page-archive-' + $tag)
    Invoke-Click $hwnd 'BtnSettingsPage'; [void](Wait-Host $hwnd 'PageSettings' 2000); Save-Shot $hwnd ('page-settings-' + $tag)
    Invoke-Click $hwnd 'BtnPageApi';   Start-Sleep -Milliseconds 500;              Save-Shot $hwnd ('page-api-' + $tag)

    Invoke-Click $hwnd 'BtnTheme'; Start-Sleep -Seconds 1; Save-Shot $hwnd ('page-api-theme-' + $tag)
    Invoke-Click $hwnd 'BtnTheme'; Start-Sleep -Milliseconds 400

    $p.Refresh()
    if ($p.CloseMainWindow()) {
        if (-not $p.WaitForExit(15000)) { Add-Result $false ('close-' + $tag) 'close timed out' }
        else { Add-Result $true ('close-' + $tag) 'closed-ok' }
    } else { Add-Result $false ('close-' + $tag) 'CloseMainWindow refused' }
    $crash = Join-Path (Split-Path $Exe) 'crash.log'
    if (Test-Path $crash) { Add-Result $false ('crashlog-' + $tag) ('exists: ' + $crash) }
    else { Add-Result $true ('crashlog-' + $tag) 'no crash.log' }
}

try { Run-Pass $false } finally {
    if ($script:currentProc -and -not $script:currentProc.HasExited) { Stop-Process -Id $script:currentProc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
}
try { Run-Pass $true } finally {
    if ($script:currentProc -and -not $script:currentProc.HasExited) { Stop-Process -Id $script:currentProc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
}
Write-Output ('MAX-SWITCH-LATENCY ' + $script:maxLatency + 'ms')
$fail = @($results | Where-Object { -not $_.Ok })
Write-Output ('SUMMARY total=' + $results.Count + ' pass=' + ($results.Count - $fail.Count) + ' fail=' + $fail.Count)
if ($fail.Count) { $fail | ForEach-Object { Write-Output ('FAILED: ' + $_.Id + ' :: ' + $_.Detail) }; exit 1 }
exit 0
