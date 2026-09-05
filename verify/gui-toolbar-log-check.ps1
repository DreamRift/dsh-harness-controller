# ============================================================================
#  gui-toolbar-log-check.ps1 - title-bar tool area & shared log dock probe
#  (sub-task: "tool area and log dock"; reusable for navigation-shell rounds)
#  Usage: powershell -ExecutionPolicy Bypass -File verify\gui-toolbar-log-check.ps1 -Exe <exe> -Out <dir>
#  Asserts:
#    normal pass: settings/tool/theme ordered & clickable; settings opens full
#      page and leaves; theme cycles 3 states; log dock expand/collapse on all
#      four pages (8 steps); auto-scroll toggle text flips; copy appends a line
#      that stays in view (scroll follow); clear empties list; clean close
#    dev pass: design-desk entry visible between settings and theme; opens the
#      DEV GALLERY page; back to instances works
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
public static class Win32T {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
[void][Win32T]::SetProcessDPIAware()
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
function Get-El([long]$hwnd, [string]$id) { return Find-ById $hwnd $id }
function Test-Visible([long]$hwnd, [string]$id) {
    try { $el = Find-ById $hwnd $id } catch { return $false }
    if ($null -eq $el) { return $false }
    try { return (-not $el.Current.IsOffscreen) } catch { return $false }
}
function Get-Name([long]$hwnd, [string]$id) {
    $el = Find-ById $hwnd $id
    if ($null -eq $el) { return $null }
    return $el.Current.Name
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
function Find-ListItems([long]$hwnd) {
    $lv = Find-ById $hwnd 'LogList'
    if ($null -eq $lv) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $col = $lv.FindAll($TS::Descendants, $cond)
    $out = @(); foreach ($i in $col) { $out += $i }
    return $out
}
function Save-Shot([long]$hwnd, [string]$name) {
    $r = New-Object Win32T+RECT
    [void][Win32T]::GetWindowRect([IntPtr]$hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $sz = New-Object System.Drawing.Size($w, $h)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $sz)
    $bmp.Save((Join-Path $Out ($name + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
function Wait-Cond([scriptblock]$sb, [int]$timeoutMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) { if (& $sb) { return $true }; Start-Sleep -Milliseconds 100 }
    return $false
}
function Run-Pass([bool]$devMode) {
    $tag = 'normal'; if ($devMode) { $tag = 'dev' }
    if ($devMode) { $p = Start-Process -FilePath $Exe -ArgumentList '--dev' -PassThru }
    else { $p = Start-Process -FilePath $Exe -PassThru }
    $script:currentProc = $p
    $deadline = (Get-Date).AddSeconds($StartupSec); $hwnd = [long]0
    while ((Get-Date) -lt $deadline) {
        $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle.ToInt64(); break }
        Start-Sleep -Milliseconds 250
    }
    if ($hwnd -eq 0) { Add-Result $false ('startup-' + $tag) 'no main window'; return }
    Start-Sleep -Seconds 6
    Add-Result $true ('startup-' + $tag) ('hwnd=' + $hwnd)

    # --- tool area ordering & entries ---
    $rs = (Get-El $hwnd 'BtnSettingsPage').Current.BoundingRectangle
    $rt = (Get-El $hwnd 'BtnTheme').Current.BoundingRectangle
    Add-Result (($rs.Right -le $rt.Left + 12) -and ($rs.Left -gt 0)) ('tools-order-' + $tag) ('settings.right=' + [int]$rs.Right + ' theme.left=' + [int]$rt.Left)
    if ($devMode) {
        $rd = (Get-El $hwnd 'BtnDevDesk').Current.BoundingRectangle
        Add-Result (($rd.Left -ge $rs.Right - 4) -and ($rd.Right -le $rt.Left + 12)) ('devdesk-placed-' + $tag) ('devdesk between settings and theme: L=' + [int]$rd.Left + ' R=' + [int]$rd.Right)
    }
    Save-Shot $hwnd ('tools-' + $tag)

    # --- settings entry opens the full page and leaves it ---
    Invoke-Click $hwnd 'BtnSettingsPage'
    Add-Result (Wait-Cond { Test-Visible $hwnd 'PageSettings' } 2000) ('settings-open-' + $tag) 'BtnSettingsPage -> PageSettings'
    Save-Shot $hwnd ('settings-from-tools-' + $tag)
    Invoke-Click $hwnd 'BtnPageInst'
    Add-Result (Wait-Cond { (Get-El $hwnd 'PageSettings') -eq $null } 2000) ('settings-leave-' + $tag) 'left settings via page button'

    # --- theme cycle: 3 clicks = light -> dark -> system, app alive all the way ---
    Invoke-Click $hwnd 'BtnTheme'; Start-Sleep -Milliseconds 700; Save-Shot $hwnd ('theme-1-' + $tag)
    Invoke-Click $hwnd 'BtnTheme'; Start-Sleep -Milliseconds 700; Save-Shot $hwnd ('theme-2-' + $tag)
    Invoke-Click $hwnd 'BtnTheme'; Start-Sleep -Milliseconds 700
    Add-Result (Test-Visible $hwnd 'BtnPageInst') ('theme-cycle-' + $tag) 'three-state cycle done, UI alive'

    # --- log dock: expand/collapse on all four pages ---
    $four = @('BtnPageInst','BtnPagePlug','BtnPageArch','BtnPageApi')
    foreach ($b in $four) {
        Invoke-Click $hwnd $b; Start-Sleep -Milliseconds 400
        Invoke-Click $hwnd 'BtnConsoleToggle'
        $exp = Wait-Cond { Test-Visible $hwnd 'LogList' } 1500
        $stateTxt = Get-Name $hwnd 'TxtConsoleToggle'
        Invoke-Click $hwnd 'BtnConsoleToggle'
        $col2 = Wait-Cond { -not (Test-Visible $hwnd 'LogList') } 1500
        Add-Result ($exp -and $col2) ('logdock-' + $b.Substring(7).ToLower() + '-' + $tag) ('expand=' + $exp + ' collapse=' + $col2 + ' toggle-text=' + $stateTxt)
    }

    # --- auto-scroll toggle text flips ---
    Invoke-Click $hwnd 'BtnConsoleToggle'; [void](Wait-Cond { Test-Visible $hwnd 'LogList' } 1500)
    $before = Get-Name $hwnd 'BtnAutoScroll'
    Invoke-Click $hwnd 'BtnAutoScroll'
    $after = Get-Name $hwnd 'BtnAutoScroll'
    Invoke-Click $hwnd 'BtnAutoScroll'
    $back = Get-Name $hwnd 'BtnAutoScroll'
    Add-Result (($before -ne $after) -and ($back -eq $before)) ('autoscroll-flip-' + $tag) ($before + ' -> ' + $after + ' -> ' + $back)

    # --- copy appends a line; latest line stays inside the list viewport (scroll follow) ---
    Invoke-Click $hwnd 'BtnCopyLog'
    [void](Wait-Cond { $items = @(Find-ListItems $hwnd); $items.Count -gt 0 } 2500)
    $items = @(Find-ListItems $hwnd)
    $lv = Get-El $hwnd 'LogList'
    $lastBottom = -1; if ($items.Count -gt 0) { $lastBottom = [int]$items[$items.Count-1].Current.BoundingRectangle.Bottom }
    $follow = ($items.Count -gt 0) -and ($lastBottom -le ([int]$lv.Current.BoundingRectangle.Bottom + 2))
    Add-Result $follow ('scroll-follow-' + $tag) ('items=' + $items.Count + ' last.bottom=' + $lastBottom + ' list.bottom=' + [int]$lv.Current.BoundingRectangle.Bottom)
    Save-Shot $hwnd ('log-open-' + $tag)

    # --- clear empties the list ---
    Invoke-Click $hwnd 'BtnClearLog'; Start-Sleep -Milliseconds 500
    $items2 = @(Find-ListItems $hwnd)
    Add-Result ($items2.Count -eq 0) ('clear-' + $tag) ('items after clear=' + $items2.Count)

    # --- dev pass extras: design desk page openable from tool area ---
    if ($devMode) {
        Invoke-Click $hwnd 'BtnDevDesk'
        $root = $AE::FromHandle([IntPtr]$hwnd)
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'DEV GALLERY')
        $found = Wait-Cond { ($root.FindFirst($TS::Descendants, $cond)) -ne $null } 3000
        Add-Result $found ('devdesk-page-' + $tag) 'DEV GALLERY header reached'
        Save-Shot $hwnd ('devdesk-' + $tag)
        Invoke-Click $hwnd 'BtnPageInst'
    }

    # --- report-dir button presence (clicking would spawn Explorer; presence = wiring kept) ---
    Add-Result (Test-Visible $hwnd 'BtnOpenReportDir') ('reportdir-' + $tag) 'BtnOpenReportDir present'

    $p.Refresh()
    if ($p.CloseMainWindow()) {
        if (-not $p.WaitForExit(15000)) { Add-Result $false ('close-' + $tag) 'timed out' }
        else { Add-Result $true ('close-' + $tag) 'closed-ok' }
    } else { Add-Result $false ('close-' + $tag) 'refused' }
    $crash = Join-Path (Split-Path $Exe) 'crash.log'
    if (Test-Path $crash) { Add-Result $false ('crashlog-' + $tag) ('exists ' + $crash) }
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
$fail = @($results | Where-Object { -not $_.Ok })
Write-Output ('SUMMARY total=' + $results.Count + ' pass=' + ($results.Count - $fail.Count) + ' fail=' + $fail.Count)
if ($fail.Count) { $fail | ForEach-Object { Write-Output ('FAILED: ' + $_.Id + ' :: ' + $_.Detail) }; exit 1 }
exit 0
