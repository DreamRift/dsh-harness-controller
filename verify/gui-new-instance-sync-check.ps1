# ============================================================================
#  gui-new-instance-sync-check.ps1 - new-instance dialog sync-presets checkbox
#  (sub-task "new page sync checkbox")
#  Walks: open the new-instance dialog, assert the sync-presets checkbox exists
#  and is checked by default, then cancel (no instance created, zero writes).
#  The instances page opens against the current registry read-only; cancelling
#  never creates or writes. ASCII-only source.
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
public static class Win32N {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
[void][Win32N]::SetProcessDPIAware()
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
function Invoke-Click([long]$hwnd, [string]$id) {
    for ($try = 1; $try -le 4; $try++) {
        $el = Find-ById $hwnd $id
        if ($null -ne $el) {
            $pat = $null
            if ($el.TryGetCurrentPattern($IPC::Pattern, [ref]$pat)) { $pat.Invoke(); return $true }
        }
        Start-Sleep -Milliseconds 400
    }
    return $false
}
function Save-Shot([long]$hwnd, [string]$name) {
    $r = New-Object Win32N+RECT
    [void][Win32N]::GetWindowRect([IntPtr]$hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $sz = New-Object System.Drawing.Size($w, $h)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $sz)
    $bmp.Save((Join-Path $Out ($name + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
try {
    $p = Start-Process -FilePath $Exe -PassThru
    $script:currentProc = $p
    $deadline = (Get-Date).AddSeconds($StartupSec); $hwnd = [long]0
    while ((Get-Date) -lt $deadline) { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle.ToInt64(); break }; Start-Sleep -Milliseconds 250 }
    Add-Result ($hwnd -ne 0) 'window-smoke' ('hwnd=' + $hwnd)
    if ($hwnd -eq 0) { throw 'no window' }

    # instances page is default; click BtnNew (empty-state or rail) to open dialog
    $open = Invoke-Click $hwnd 'BtnNew'
    Start-Sleep -Milliseconds 400
    if (-not $open) { $open = Invoke-Click $hwnd 'BtnNewFromEmpty' }
    Add-Result $open 'new-dialog-open' ('btn=' + $open)

    # checkbox present + default checked
    $chk = $null
    for ($t = 0; $t -lt 20 -and $null -eq $chk; $t++) { $chk = Find-ById $hwnd 'ChkSyncPresets'; if ($null -eq $chk) { Start-Sleep -Milliseconds 400 } }
    $chkOk = $false
    if ($null -ne $chk) {
        $tg = $null
        if ($chk.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$tg)) { $chkOk = $tg.Current.ToggleState -eq 1 }
    }
    Add-Result $chkOk 'sync-checkbox-default-on' ('found=' + ($null -ne $chk) + ' checked=' + $chkOk)
    Save-Shot $hwnd 'new-instance-sync-checkbox'

    # cancel -> no create, no write
    $closeB = Find-ById $hwnd 'CloseButton'
    $cancelled = $false
    if ($null -ne $closeB) { $ci = $null; if ($closeB.TryGetCurrentPattern($IPC::Pattern, [ref]$ci)) { $ci.Invoke(); $cancelled = $true } }
    Start-Sleep -Milliseconds 900
    Add-Result $cancelled 'new-dialog-cancelled' ('cancel=' + $cancelled)

    $p.Refresh(); [void]$p.CloseMainWindow(); [void]$p.WaitForExit(12000)
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    $script:currentProc = $null
}
catch {
    Write-Output ('CAUGHT line ' + $_.InvocationInfo.ScriptLineNumber + ': ' + $_.Exception.Message)
    $global:newSyncFailed = $true
}
finally {
    if ($script:currentProc -and -not $script:currentProc.HasExited) { Stop-Process -Id $script:currentProc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
}
$bad = @($results | Where-Object { -not $_.Ok }).Count + $(if ($global:newSyncFailed) { 1 } else { 0 })
Write-Output ('SUMMARY total=' + $results.Count + ' fail=' + $bad)
if ($bad) { $results | Where-Object { -not $_.Ok } | ForEach-Object { Write-Output ('FAILED: ' + $_.Id + ' :: ' + $_.Detail) }; exit 1 }
exit 0
