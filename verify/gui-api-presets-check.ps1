# ============================================================================
#  gui-api-presets-check.ps1 - API page provider preset editor walk
#  (sub-task "provider editor page")
#  Walks: page visible / add via dialog with validation-passing fields /
#  edit rename / delete with confirm. Global store file (api-presets.json) is
#  backed up, cleared and restored (self-recovering). External instances are
#  untouched (page never reads/writes them). ASCII-only source.
#  Usage: powershell -ExecutionPolicy Bypass -File verify\gui-api-presets-check.ps1 -Exe <exe> -Out <dir>
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
$COND_LI = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$COND_TX = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
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
            if ($el.TryGetCurrentPattern($IPC::Pattern, [ref]$pat)) { $pat.Invoke(); return $true }
        }
        Start-Sleep -Milliseconds 300
    }
    return $false
}
function Set-Value([long]$hwnd, [string]$id, [string]$val) {
    for ($try = 1; $try -le 3; $try++) {
        $el = Find-ById $hwnd $id
        if ($null -ne $el) {
            $vp = $null
            if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $vp.SetValue($val); return $true }
        }
        Start-Sleep -Milliseconds 300
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
function Get-Rows([long]$hwnd) {
    $rows = @()
    $lv = Find-ById $hwnd 'PresetList'
    if ($null -eq $lv) { return $rows }
    foreach ($i in $lv.FindAll($TS::Descendants, $COND_LI)) {
        $texts = @()
        foreach ($tx in $i.FindAll($TS::Descendants, $COND_TX)) { $texts += $tx.Current.Name }
        $rows += [pscustomobject]@{ Text = ($texts -join '|'); Item = $i }
    }
    return $rows
}
function Wait-Rows([long]$hwnd, [int]$minRows, [int]$timeoutMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $rows = @(Get-Rows $hwnd)
        if ($rows.Count -ge $minRows) { return $sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 350
    }
    return -1
}
function Select-RowContains([long]$hwnd, [string]$needle) {
    $lv = Find-ById $hwnd 'PresetList'
    if ($null -eq $lv) { return $false }
    foreach ($i in $lv.FindAll($TS::Descendants, $COND_LI)) {
        $texts = @()
        foreach ($tx in $i.FindAll($TS::Descendants, $COND_TX)) { $texts += $tx.Current.Name }
        if (($texts -join '|').Contains($needle)) {
            $pat = $null
            if ($i.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); return $true }
        }
    }
    return $false
}
$state = Join-Path $env:LOCALAPPDATA 'DshController'
$presetFile = Join-Path $state 'api-presets.json'
$bak = Join-Path $env:TEMP ('dsh-apipreset-bak-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
$reg = Join-Path $state 'instances.json'
$regBak = Join-Path $env:TEMP ('dsh-apipreset-reg-bak-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
$homeS = Join-Path $env:TEMP 'dsh-apipreset-home-s'

try {
    if (Test-Path $presetFile) { Copy-Item $presetFile $bak -Force } else { Set-Content -Path $bak -Value '{}' }
    if (Test-Path $reg) { Copy-Item $reg $regBak -Force } else { Set-Content -Path $regBak -Value '{}' }
    # fresh empty store + single RTEST instance pointing at a temp HOME with a fixture settings.yaml
    [IO.File]::WriteAllText($presetFile, '[]', (New-Object System.Text.UTF8Encoding($false)))
    Remove-Item -Recurse -Force $homeS -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $homeS | Out-Null
    $fixture = @('version: 1', '', 'providers:', '  other:', '    displayName: other', '    api: openai-completions', '    baseURL: https://other.example.com') -join [Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $homeS 'settings.yaml'), $fixture, (New-Object System.Text.UTF8Encoding($false)))
    $rtest = @{ id='RTEST-S'; name='RTEST-S'; port=3185; host='127.0.0.1'; runtime='windows'; home=$homeS; workspace=$env:TEMP; trustedHosts=@(); autoOpenBrowser=$false; stopOnExit=$true }
    $regDoc = [pscustomobject]@{ version=2; instances=@($rtest) }
    [IO.File]::WriteAllText($reg, ($regDoc | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))

    $p = Start-Process -FilePath $Exe -PassThru
    $script:currentProc = $p
    $deadline = (Get-Date).AddSeconds($StartupSec); $hwnd = [long]0
    while ((Get-Date) -lt $deadline) { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle.ToInt64(); break }; Start-Sleep -Milliseconds 250 }
    Add-Result ($hwnd -ne 0) 'window-smoke' ('hwnd=' + $hwnd)
    if ($hwnd -eq 0) { throw 'no window' }

    $nav = Invoke-Click $hwnd 'BtnPageApi'
    Start-Sleep -Milliseconds 1200
    $hostEl = Find-ById $hwnd 'PresetsHost'
    $pageOk = ($nav -and $null -ne $hostEl -and -not $hostEl.Current.IsOffscreen)
    Add-Result $pageOk 'api-page-visible' ('nav=' + $nav)
    Save-Shot $hwnd 'api-presets-initial'

    # ---- add ----
    $addBtn = Invoke-Click $hwnd 'BtnPresetAdd'
    Start-Sleep -Milliseconds 700
    $ok1 = Set-Value $hwnd 'PresetName' 'Probe Preset'
    $ok2 = Set-Value $hwnd 'PresetKind' 'probe'
    $ok3 = Set-Value $hwnd 'PresetBaseUrl' 'https://127.0.0.1/v1'
    $ok4 = Set-Value $hwnd 'PresetApiKey' 'sk-probe'
    $ok5 = Set-Value $hwnd 'PresetModel' 'probe-model'
    $prim = Find-ById $hwnd 'PrimaryButton'
    $saved = $false
    if ($null -ne $prim) { $ip = $null; if ($prim.TryGetCurrentPattern($IPC::Pattern, [ref]$ip)) { $ip.Invoke(); $saved = $true } }
    $ms = Wait-Rows $hwnd 1 5000
    $rows = @(Get-Rows $hwnd)
    $stillOpen = $null -ne (Find-ById $hwnd 'PresetName')
    $fileDump = ''
    if (Test-Path $presetFile) { $fileDump = (Get-Content $presetFile -Raw -Encoding UTF8) }
    Write-Output ('  diag: saved=' + $saved + ' waitMs=' + $ms + ' rowsCount=' + $rows.Count + ' dialogStillOpen=' + $stillOpen + ' file=' + $fileDump)
    $addOk = ($addBtn -and $ok1 -and $ok2 -and $saved -and $ms -ge 0 -and $rows.Count -ge 1 -and $rows[0].Text.Contains('Probe Preset'))
    Add-Result $addOk 'api-add-row' ('fill=' + $ok1 + $ok2 + $ok3 + $ok4 + $ok5 + ' rows=' + $rows.Count)
    Save-Shot $hwnd 'api-presets-added'

    # ---- sync preview: opens, cancel = zero write ----
    $preBefore = ''; if (Test-Path $presetFile) { $preBefore = Get-Content $presetFile -Raw -Encoding UTF8 }
    $syncBtn = Invoke-Click $hwnd 'BtnPresetSync'
    Start-Sleep -Milliseconds 800
    $primS = Find-ById $hwnd 'PrimaryButton'
    $pv = ($syncBtn -and $null -ne $primS)
    Add-Result $pv 'sync-preview-opens' ('open=' + $pv)
    Save-Shot $hwnd 'api-presets-sync-preview'
    $closeB = Find-ById $hwnd 'CloseButton'
    $cancelled = $false
    if ($null -ne $closeB) { $ci = $null; if ($closeB.TryGetCurrentPattern($IPC::Pattern, [ref]$ci)) { $ci.Invoke(); $cancelled = $true } }
    Start-Sleep -Milliseconds 900
    $preAfter = ''; if (Test-Path $presetFile) { $preAfter = Get-Content $presetFile -Raw -Encoding UTF8 }
    $rowsX = @(Get-Rows $hwnd)
    $zeroOk = ($cancelled -and $preAfter -eq $preBefore -and $rowsX.Count -ge 1)
    Add-Result $zeroOk 'sync-cancel-zero-write' ('cancel=' + $cancelled + ' fileSame=' + ($preAfter -eq $preBefore) + ' rows=' + $rowsX.Count)

    # ---- sync confirm: real write into RTEST-S temp HOME settings.yaml ----
    $settings = Join-Path $homeS 'settings.yaml'
    $beforeSettings = Get-Content $settings -Raw -Encoding UTF8
    [void](Invoke-Click $hwnd 'BtnPresetSync')
    Start-Sleep -Milliseconds 800
    $primW = Find-ById $hwnd 'PrimaryButton'
    $wrote = $false
    if ($null -ne $primW) { $wi = $null; if ($primW.TryGetCurrentPattern($IPC::Pattern, [ref]$wi)) { $wi.Invoke(); $wrote = $true } }
    Start-Sleep -Milliseconds 1500
    # close the result Info dialog if present
    $infClose = Find-ById $hwnd 'CloseButton'
    if ($null -ne $infClose) { $ii = $null; if ($infClose.TryGetCurrentPattern($IPC::Pattern, [ref]$ii)) { $ii.Invoke() } }
    Start-Sleep -Milliseconds 600
    $afterSettings = ''
    if (Test-Path $settings) { $afterSettings = Get-Content $settings -Raw -Encoding UTF8 }
    $expectKey = 'probe-probe-preset:'
    $writeOk = ($wrote -and $afterSettings.Contains($expectKey) -and $afterSettings.Contains('other:') -and $beforeSettings.Contains('version: 1'))
    Add-Result $writeOk 'sync-confirm-real-write' ('wrote=' + $wrote + ' hasKey=' + $afterSettings.Contains($expectKey) + ' keptOther=' + $afterSettings.Contains('other:'))
    Save-Shot $hwnd 'api-presets-sync-written'

    # ---- edit (rename) ----
    $sel = Select-RowContains $hwnd 'Probe Preset'
    Start-Sleep -Milliseconds 500
    $edBtn = Invoke-Click $hwnd 'BtnPresetEdit'
    Start-Sleep -Milliseconds 700
    [void](Set-Value $hwnd 'PresetName' 'Probe Renamed')
    $prim2 = Find-ById $hwnd 'PrimaryButton'
    $saved2 = $false
    if ($null -ne $prim2) { $ip2 = $null; if ($prim2.TryGetCurrentPattern($IPC::Pattern, [ref]$ip2)) { $ip2.Invoke(); $saved2 = $true } }
    Start-Sleep -Milliseconds 1200
    $rows2 = @(Get-Rows $hwnd)
    $editOk = ($sel -and $edBtn -and $saved2 -and $rows2.Count -ge 1 -and $rows2[0].Text.Contains('Probe Renamed'))
    Add-Result $editOk 'api-edit-renamed' ('rows=' + $rows2.Count)
    Save-Shot $hwnd 'api-presets-edited'

    # ---- delete ----
    $delBtn = Invoke-Click $hwnd 'BtnPresetDelete'
    Start-Sleep -Milliseconds 700
    $prim3 = Find-ById $hwnd 'PrimaryButton'
    $delOk2 = $false
    if ($null -ne $prim3) { $ip3 = $null; if ($prim3.TryGetCurrentPattern($IPC::Pattern, [ref]$ip3)) { $ip3.Invoke(); $delOk2 = $true } }
    Start-Sleep -Milliseconds 1200
    $rows3 = @(Get-Rows $hwnd)
    $delOk = ($delBtn -and $delOk2 -and $rows3.Count -eq 0)
    Add-Result $delOk 'api-delete-removed' ('rows=' + $rows3.Count)
    Save-Shot $hwnd 'api-presets-deleted'

    $p.Refresh(); [void]$p.CloseMainWindow(); [void]$p.WaitForExit(12000)
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    $script:currentProc = $null
}
catch {
    Write-Output ('CAUGHT line ' + $_.InvocationInfo.ScriptLineNumber + ': ' + $_.Exception.Message)
    $global:apiFailed = $true
}
finally {
    if ($script:currentProc -and -not $script:currentProc.HasExited) { Stop-Process -Id $script:currentProc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    if (Test-Path $bak) { Copy-Item $bak $presetFile -Force; Remove-Item $bak -Force -ErrorAction SilentlyContinue; Write-Output 'api-presets restored' }
    if (Test-Path $regBak) { Copy-Item $regBak $reg -Force; Remove-Item $regBak -Force -ErrorAction SilentlyContinue; Write-Output 'registry restored' } else { Remove-Item $reg -Force -ErrorAction SilentlyContinue }
    Remove-Item -Recurse -Force $homeS -ErrorAction SilentlyContinue
    Get-ChildItem (Join-Path $state 'archives') -Filter 'RTEST-*' -ErrorAction SilentlyContinue | Remove-Item -Force
}
$bad = @($results | Where-Object { -not $_.Ok }).Count + $(if ($global:apiFailed) { 1 } else { 0 })
Write-Output ('SUMMARY total=' + $results.Count + ' fail=' + $bad)
if ($bad) { $results | Where-Object { -not $_.Ok } | ForEach-Object { Write-Output ('FAILED: ' + $_.Id + ' :: ' + $_.Detail) }; exit 1 }
exit 0
