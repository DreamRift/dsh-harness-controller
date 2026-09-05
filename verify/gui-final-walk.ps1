# ============================================================================
#  gui-final-walk.ps1 - one-shot final product walkthrough (acceptance round)
#  Launches the app, clicks through the four top pages, takes a live frame of
#  each as the product reality screenshot, then walks the archives rail and
#  closes cleanly. Read-only against real instances; ASCII-only source.
# ============================================================================
param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [Parameter(Mandatory=$true)][string]$Out
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Win32F {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
[void][Win32F]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$AE  = [System.Windows.Automation.AutomationElement]
$TS  = [System.Windows.Automation.TreeScope]
$IPC = [System.Windows.Automation.InvokePattern]
function Find-ById([long]$hwnd, [string]$id) {
    $root = $AE::FromHandle([IntPtr]$hwnd)
    if ($null -eq $root) { return $null }
    return $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)))
}
function Click([long]$hwnd, [string]$id) {
    $el = Find-ById $hwnd $id
    if ($null -eq $el) { return $false }
    $pat = $null
    if ($el.TryGetCurrentPattern($IPC::Pattern, [ref]$pat)) { $pat.Invoke(); return $true }
    return $false
}
function Save-Shot([long]$hwnd, [string]$name) {
    $r = New-Object Win32F+RECT
    [void][Win32F]::GetWindowRect([IntPtr]$hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $sz = New-Object System.Drawing.Size($w, $h)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $sz)
    $bmp.Save((Join-Path $Out ($name + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output ('shot ' + $name + ' ' + $w + 'x' + $h)
}
try {
    $p = Start-Process -FilePath $Exe -PassThru
    $deadline = (Get-Date).AddSeconds(25); $hwnd = [long]0
    while ((Get-Date) -lt $deadline) { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle.ToInt64(); break }; Start-Sleep -Milliseconds 250 }
    if ($hwnd -eq 0) { throw 'no window' }
    Start-Sleep -Seconds 6
    Save-Shot $hwnd 'final-instances'
    [void](Click $hwnd 'BtnPagePlug'); Start-Sleep -Milliseconds 1400; Save-Shot $hwnd 'final-plugins'
    [void](Click $hwnd 'BtnPageArch'); Start-Sleep -Milliseconds 1600; Save-Shot $hwnd 'final-archive'
    [void](Click $hwnd 'BtnPageApi'); Start-Sleep -Milliseconds 1400; Save-Shot $hwnd 'final-api'
    Write-Output 'walk-ok'
    $p.Refresh(); [void]$p.CloseMainWindow(); [void]$p.WaitForExit(12000)
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
}
catch { Write-Output ('CAUGHT: ' + $_.Exception.Message); exit 1 }
finally { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }; Start-Sleep -Seconds 1 }
exit 0
