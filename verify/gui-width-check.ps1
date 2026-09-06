# ============================================================================
#  gui-width-check.ps1 - three-tier window width discipline probe (960/1280/1600)
#  Companion to gui-uia-check.ps1; reusable for every page-hosting sub-task.
#  Asserts via UIA BoundingRectangle (device px):
#    * rail (left column marker) x-position stable across widths & pages
#    * rail right edge strictly left of body marker (no overlap)
#    * rightmost body control inside the window (no clipping at any tier)
#  Also captures 4 pages x 3 widths and 4 pages x light/dark theme screenshots
#  for the human eye (this model has no image input; harmony = user review).
#  Usage: powershell -ExecutionPolicy Bypass -File verify\gui-width-check.ps1 -Exe <exe> -Out <dir>
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
public static class Win32W {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
[void][Win32W]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$results = @()
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
function Get-Rect([long]$hwnd, [string]$id) {
    $el = Find-ById $hwnd $id
    if ($null -eq $el -or $el.Current.IsOffscreen) { return $null }
    return $el.Current.BoundingRectangle
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
function Set-Width([long]$hwnd, [int]$logicalW) {
    $scale = [Win32W]::GetDpiForWindow([IntPtr]$hwnd) / 96.0
    $r = New-Object Win32W+RECT
    [void][Win32W]::GetWindowRect([IntPtr]$hwnd, [ref]$r)
    $targetPx = [int][Math]::Round($logicalW * $scale)
    [void][Win32W]::SetWindowPos([IntPtr]$hwnd, [IntPtr]::Zero, $r.Left, $r.Top, $targetPx, $r.Bottom - $r.Top, 0x2 -bor 0x4)
    Start-Sleep -Milliseconds 350
    return $scale
}
function Get-WinRect([long]$hwnd) {
    $r = New-Object Win32W+RECT
    [void][Win32W]::GetWindowRect([IntPtr]$hwnd, [ref]$r)
    return $r
}
function Save-Shot([long]$hwnd, [string]$name) {
    $r = Get-WinRect $hwnd
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $sz = New-Object System.Drawing.Size($w, $h)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $sz)
    $bmp.Save((Join-Path $Out ($name + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
# page model: button, rail marker = RailScroll (the shell's left-column ScrollViewer,
# same element type in all four shells -> strictly comparable), body/far markers per page
$pages = @(
    @{ key='inst'; btn='BtnPageInst'; body='TxtPageTitle'; far='BtnStart' },
    @{ key='plug'; btn='BtnPagePlug'; body='TxtFreshness'; far='TxtProfile' },   # revamp: CmbInstance removed; far marker = market page profile input
    @{ key='arch'; btn='BtnPageArch'; body='UsageHost';    far=$null },
    @{ key='api';  btn='BtnPageApi';  body=$null;          far=$null }
)
$railId = 'RailScroll'
function Wait-Rect([long]$hwnd, [string]$id, [int]$timeoutMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $rc = Get-Rect $hwnd $id
        if ($null -ne $rc) { return $rc }
        Start-Sleep -Milliseconds 100
    }
    return $null
}
$widths = @(960, 1280, 1600)

$p = Start-Process -FilePath $Exe -PassThru
$deadline = (Get-Date).AddSeconds($StartupSec); $hwnd = [long]0
while ((Get-Date) -lt $deadline) {
    $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle.ToInt64(); break }
    Start-Sleep -Milliseconds 250
}
if ($hwnd -eq 0) { Write-Output 'FATAL no window'; exit 1 }
Start-Sleep -Seconds 6
$scale = [Win32W]::GetDpiForWindow([IntPtr]$hwnd) / 96.0
Write-Output ('INFO dpi-scale=' + $scale)

$railLeftBase = $null
$script:railWidths = @()
foreach ($w in $widths) {
    [void](Set-Width $hwnd $w)
    $wr = Get-WinRect $hwnd
    foreach ($pg in $pages) {
        $id = $pg.key + '-w' + $w
        try { Invoke-Click $hwnd $pg.btn } catch { Add-Result $false ('click-' + $id) $_.Exception.Message; continue }
        $rr = Wait-Rect $hwnd $railId 3000
        if ($null -eq $rr) { Add-Result $false ('rail-missing-' + $id) ('marker ' + $railId); continue }
        if ($null -eq $railLeftBase) { $railLeftBase = $rr.Left }
        Add-Result ([Math]::Abs($rr.Left - $railLeftBase) -le 2) ('rail-x-stable-' + $id) ('rail.left=' + $rr.Left + ' base=' + $railLeftBase)
        Add-Result ($rr.Right -le ($wr.Right - 4)) ('rail-inwindow-' + $id) ('rail.right=' + $rr.Right + ' win.right=' + $wr.Right)
        $script:railWidths += [int][Math]::Round($rr.Width)
        if ($pg.body) {
            $br = Wait-Rect $hwnd $pg.body 3000
            if ($null -eq $br) { Add-Result $false ('body-missing-' + $id) ('marker ' + $pg.body) }
            else { Add-Result ($br.Left -ge ($rr.Right - 1)) ('no-overlap-' + $id) ('rail.right=' + $rr.Right + ' body.left=' + $br.Left) }
        }
        if ($pg.far) {
            $fr = Wait-Rect $hwnd $pg.far 6000
            if ($null -eq $fr) { Add-Result $false ('far-missing-' + $id) ('marker ' + $pg.far) }
            else { Add-Result ($fr.Right -le ($wr.Right - 2)) ('no-clip-' + $id) ($pg.far + '.right=' + $fr.Right + ' win.right=' + $wr.Right) }
        }
        Save-Shot $hwnd ($pg.key + '-w' + $w)
    }
}
# left-column width identical across all pages and all tiers (single source: SplitPageShell)
$uniqW = @($script:railWidths | Sort-Object -Unique)
Add-Result ($uniqW.Count -eq 1) ('rail-width-single') ('widths seen=' + ($uniqW -join ',') + ' samples=' + $script:railWidths.Count)

# light / dark theme passes at 1280: system -> light -> dark -> system
[void](Set-Width $hwnd 1280)
Invoke-Click $hwnd 'BtnTheme'; Start-Sleep -Milliseconds 600
foreach ($pg in $pages) { Invoke-Click $hwnd $pg.btn; Start-Sleep -Milliseconds 300; Save-Shot $hwnd ($pg.key + '-themeA') }
Invoke-Click $hwnd 'BtnTheme'; Start-Sleep -Milliseconds 600
foreach ($pg in $pages) { Invoke-Click $hwnd $pg.btn; Start-Sleep -Milliseconds 300; Save-Shot $hwnd ($pg.key + '-themeB') }
Invoke-Click $hwnd 'BtnTheme'; Start-Sleep -Milliseconds 400
Invoke-Click $hwnd 'BtnPageInst'

$p.Refresh()
if ($p.CloseMainWindow()) {
    if (-not $p.WaitForExit(15000)) { Add-Result $false 'close' 'timed out' } else { Add-Result $true 'close' 'closed-ok' }
} else { Add-Result $false 'close' 'refused' }
$crash = Join-Path (Split-Path $Exe) 'crash.log'
if (Test-Path $crash) { Add-Result $false 'crashlog' ('exists ' + $crash) } else { Add-Result $true 'crashlog' 'no crash.log' }

$fail = @($results | Where-Object { -not $_.Ok })
Write-Output ('SUMMARY total=' + $results.Count + ' pass=' + ($results.Count - $fail.Count) + ' fail=' + $fail.Count)
if ($fail.Count) { $fail | ForEach-Object { Write-Output ('FAILED: ' + $_.Id + ' :: ' + $_.Detail) }; exit 1 }
exit 0
