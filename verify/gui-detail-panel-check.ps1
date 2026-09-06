# ============================================================================
#  gui-detail-panel-check.ps1 - merged instances page capability walk
#  (sub-task "detail operations main area"; checklist = old InstancePanel capability list)
#  Contexts: external windows:3080 (READ-ONLY: never start/stop/restart/delete),
#  wsl:3081 (presence + wsl-only fields), injected RTEST-T on port 3185 with temp
#  HOME (full lifecycle incl. dialog-cancel proofs). ASCII-only source: PS 5.1
#  reads BOM-less UTF-8 as GBK; UI state words are compared via char-code sets.
#  Usage: powershell -ExecutionPolicy Bypass -File verify\gui-detail-panel-check.ps1 -Exe <exe> -Out <dir>
# ============================================================================
param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [Parameter(Mandatory=$true)][string]$Out,
    [int]$StartupSec = 25
)
$ErrorActionPreference = 'Stop'
$S_CANCEL = [string]([char]0x53D6) + [char]0x6D88              # cancel
$S_RUN    = [string]([char]0x8FD0) + [char]0x884C              # running
$S_READY  = [string]([char]0x5C31) + [char]0x7EEA              # ready
$S_STOP   = [string]([char]0x505C) + [char]0x6B62              # stopped
$S_NOTRUN = [string]([char]0x672A) + [char]0x8FD0 + [char]0x884C  # not-running
$S_ENVSEL = [string]([char]0x73AF) + [char]0x5883                # environment (env-choice dialog)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Win32D {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
[void][Win32D]::SetProcessDPIAware()
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
function Get-Enabled([long]$hwnd, [string]$id) {
    $el = Find-ById $hwnd $id
    if ($null -eq $el) { return 'absent' }
    try { if ($el.Current.IsOffscreen) { return 'offscreen' } } catch { }
    try { return [string]$el.Current.IsEnabled } catch { return '?' }
}
function Invoke-Click([long]$hwnd, [string]$id) {
    # click fallback chain: InvokePattern -> TogglePattern -> ExpandCollapsePattern (Expander)
    for ($try = 1; $try -le 3; $try++) {
        $el = Find-ById $hwnd $id
        if ($null -ne $el) {
            $pat = $null
            if ($el.TryGetCurrentPattern($IPC::Pattern, [ref]$pat)) { $pat.Invoke(); return $true }
            $tog = $null
            if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$tog)) { $tog.Toggle(); return $true }
            $xc = $null
            if ($el.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$xc)) { $xc.Expand(); return $true }
        }
        Start-Sleep -Milliseconds 300
    }
    return $false
}
function Select-RailRow([long]$hwnd, [string]$nameContains) {
    # revamp: rail row template was rebuilt (dot + name/port lines + state text), so match
    # on the aggregate row text instead of the first Text child only
    $lv = Find-ById $hwnd 'RailList'
    if ($null -eq $lv) { return $false }
    $condLI = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $condTx = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    foreach ($i in $lv.FindAll($TS::Descendants, $condLI)) {
        $rowText = ''
        foreach ($tx in $i.FindAll($TS::Descendants, $condTx)) { $rowText += ' ' + $tx.Current.Name }
        if ($rowText.Contains($nameContains)) {
            $pat = $null
            if ($i.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); return $true }
        }
    }
    return $false
}
function Test-InTree([long]$hwnd, [string]$id) {
    # Expander content is realized into the UIA tree only while expanded; items may sit
    # below the fold (IsOffscreen=true) which is normal scroll layout, not absence.
    try { return ($null -ne (Find-ById $hwnd $id)) } catch { return $false }
}
function Get-Title([long]$hwnd) {
    $el = Find-ById $hwnd 'TxtPageTitle'
    if ($null -eq $el) { return '' }
    return $el.Current.Name
}
function Save-Shot([long]$hwnd, [string]$name) {
    $r = New-Object Win32D+RECT
    [void][Win32D]::GetWindowRect([IntPtr]$hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $sz = New-Object System.Drawing.Size($w, $h)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $sz)
    $bmp.Save((Join-Path $Out ($name + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
function Wait-State([long]$hwnd, [string[]]$wantAny, [int]$timeoutMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $el = Find-ById $hwnd 'StatusText'
        if ($null -ne $el) {
            try {
                if (-not $el.Current.IsOffscreen) {
                    $n = $el.Current.Name
                    foreach ($w in $wantAny) { if ($n.Contains($w)) { return $sw.ElapsedMilliseconds } }
                }
            } catch { }
        }
        Start-Sleep -Milliseconds 300
    }
    return -1
}
# revamp: the per-instance toolbar (CmbInstance/BtnNew/BtnScan) and the VersionText badge
# are gone; new/scan moved to the rail top (BtnRailNew/BtnRailScan, always visible)
$ALL_IDS = @('BtnRailNew','BtnRailScan','BtnClone','BtnDelete','BtnStart','BtnRestart','BtnStop','BtnOpen',
             'BtnCopyUrl','UrlLink','ExpInstanceSettings','StatusText','EnvBadgeText','HomeText','PidText')

$state = Join-Path $env:LOCALAPPDATA 'DshController'
$reg = Join-Path $state 'instances.json'
$backup = Join-Path $env:TEMP ('dsh-detail-backup-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
$homeT = Join-Path $env:TEMP 'dsh-detail-home-t'
# preflight: free 3185 + purge stale test HOME so a force-killed earlier session's
# orphaned dsh child (or its HOME lock) can never masquerade as a product failure
Get-NetTCPConnection -State Listen -LocalPort 3185 -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
Remove-Item -Recurse -Force $homeT -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $homeT | Out-Null
try {
    if (Test-Path $reg) { Copy-Item $reg $backup -Force } else { Set-Content -Path $backup -Value '{}' }
    $doc = $null
    try { $doc = Get-Content $reg -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $doc = $null }
    if ($null -eq $doc) { $doc = [pscustomobject]@{ version = 2; instances = @() } }
    $existing = @(); if ($null -ne $doc.instances) { $existing = @($doc.instances) }
    $t = [pscustomobject]@{ id='RTEST-T'; name='RTEST-T'; port=3185; host='127.0.0.1'; runtime='windows'
        home=$homeT; workspace=$env:TEMP; trustedHosts=@(); autoOpenBrowser=$false; stopOnExit=$true
        createdAt='2026-08-28T00:00:00Z'; lastStartedAt='2026-09-02T01:00:00Z'; wslDistro=''; wslHome=''; harnessVersion='' }
    $doc | Add-Member -NotePropertyName instances -NotePropertyValue (@($existing + @($t))) -Force
    [IO.File]::WriteAllText($reg, ($doc | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))

    $p = Start-Process -FilePath $Exe -PassThru
    $script:currentProc = $p
    $deadline = (Get-Date).AddSeconds($StartupSec); $hwnd = [long]0
    while ((Get-Date) -lt $deadline) { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle.ToInt64(); break }; Start-Sleep -Milliseconds 250 }
    Add-Result ($hwnd -ne 0) 'window-smoke' ('hwnd=' + $hwnd)
    if ($hwnd -eq 0) { throw 'no window' }
    Start-Sleep -Seconds 8

    # ---- context A: external windows:3080 (read-only) ----
    [void](Select-RailRow $hwnd 'windows:3080')
    Start-Sleep -Milliseconds 900
    Add-Result ((Get-Title $hwnd) -match 'Windows') 'ext-selected' ('title=' + (Get-Title $hwnd))
    $absent = @(); foreach ($i in $ALL_IDS) { if (-not (Test-Visible $hwnd $i)) { $absent += $i } }
    Add-Result ($absent.Count -eq 0) 'ext-controls-present' ('missing=' + ($absent -join ','))
    $mExt = ''
    foreach ($i in @('BtnStart','BtnRestart','BtnStop','BtnOpen','BtnDelete')) { $mExt += $i + '=' + (Get-Enabled $hwnd $i) + ' ' }
    Write-Output ('  enabled-matrix-ext: ' + $mExt)
    Save-Shot $hwnd 'ctx-ext-selected'
    # revamp: scan is the rail-top global entry (BtnRailScan); when a not-running WSL
    # distro is detected the app first asks permission ("launch WSL to scan?") - close
    # that prompt if it appears (appearance is environment-dependent; the scan covers
    # all environments now, so skipping the WSL boot is fine for this probe)
    $scanOk = Invoke-Click $hwnd 'BtnRailScan'; Start-Sleep -Milliseconds 1500
    $scanPromptClosed = $false
    $swS = [Diagnostics.Stopwatch]::StartNew()
    while ($swS.ElapsedMilliseconds -lt 5000) {
        $cbS = Find-ById $hwnd 'CloseButton'
        if ($null -ne $cbS) {
            $patS = $null
            if ($cbS.TryGetCurrentPattern($IPC::Pattern, [ref]$patS)) { $patS.Invoke(); $scanPromptClosed = $true; break }
        }
        Start-Sleep -Milliseconds 300
    }
    $urlOk = Invoke-Click $hwnd 'BtnCopyUrl'; Start-Sleep -Milliseconds 500
    $expOk = Invoke-Click $hwnd 'ExpInstanceSettings'; Start-Sleep -Milliseconds 1200
    $expVisible = Test-InTree $hwnd 'TxtPort'
    Add-Result ($scanOk -and $urlOk -and $expOk -and $expVisible) 'ext-readonly-actions' ('scan=' + $scanOk + ' wslPromptClosed=' + $scanPromptClosed + ' copy=' + $urlOk + ' expand=' + $expOk + ' fields=' + $expVisible)
    # G1/G3 (subagent cold review): every settings-area interactive control must be present
    # when expanded (existence only; side-effectful ones are deliberately not clicked)
    $missing2 = @()
    # row containers (RowWinHome etc.) are layout panels - not in the UIA control view;
    # their children (TxtHome/BtnBrowseHome/...) prove the row is realized
    foreach ($i in @('TxtHost','TxtPort','BtnSuggestPort','TxtWorkspace','BtnBrowseWs','BtnOpenWs','TxtHome','BtnBrowseHome','BtnOpenHome','SwAutoOpen','SwStopOnExit','TxtTrustedHosts','BtnCancelInstance','BtnSaveInstance')) {
        if (-not (Test-InTree $hwnd $i)) { $missing2 += $i }
    }
    Add-Result ($missing2.Count -eq 0) 'ext-settings-full-coverage' ('missing=' + ($missing2 -join ','))
    [void](Invoke-Click $hwnd 'ExpInstanceSettings'); Start-Sleep -Milliseconds 400
    $elP = Find-ById $hwnd 'PidText'; $pidLine = ''; if ($elP) { $pidLine = $elP.Current.Name }
    Write-Output ('  ext pid line: ' + $pidLine)

    # ---- context B: wsl row (presence + wsl-only fields) ----
    [void](Select-RailRow $hwnd 'wsl:')
    Start-Sleep -Milliseconds 900
    Add-Result ((Get-Title $hwnd) -match 'WSL') 'wsl-selected' ('title=' + (Get-Title $hwnd))
    $expOk2 = Invoke-Click $hwnd 'ExpInstanceSettings'; Start-Sleep -Milliseconds 1500
    # RowWslDistro is a layout container (not in the UIA control view); assert its real
    # controls: the distro ComboBox and the list-refresh button.
    $distroCtl = (Test-InTree $hwnd 'CmbWslDistro') -and (Test-InTree $hwnd 'RowWslDistro' -eq $true -or $true)
    $distroCtl = Test-InTree $hwnd 'CmbWslDistro'
    $wslOnlyMissing = @()
    # wsl-only realized controls (home browsing lives in the windows-only row)
    foreach ($i in @('CmbWslDistro','BtnListDistros','TxtWslHome','CmbWslPolicy')) {
        if (-not (Test-InTree $hwnd $i)) { $wslOnlyMissing += $i }
    }
    $listD = $false
    if ($distroCtl) { $listD = Invoke-Click $hwnd 'BtnListDistros'; Start-Sleep -Milliseconds 3500 }
    [void](Invoke-Click $hwnd 'ExpInstanceSettings'); Start-Sleep -Milliseconds 400
    Add-Result ($expOk2 -and $distroCtl -and $wslOnlyMissing.Count -eq 0) 'wsl-distro-fields' ('expand=' + $expOk2 + ' CmbWslDistro=' + $distroCtl + ' wslOnlyMissing=' + ($wslOnlyMissing -join ',') + ' listDistrosClicked=' + $listD)
    Save-Shot $hwnd 'ctx-wsl-selected'

    # ---- context C: RTEST-T lifecycle on 3185 ----
    [void](Select-RailRow $hwnd 'windows:3185')
    Start-Sleep -Milliseconds 900
    Add-Result ((Get-Title $hwnd) -match 'Windows') 'test-selected' ('title=' + (Get-Title $hwnd))
    $mT0 = ''
    foreach ($i in @('BtnStart','BtnRestart','BtnStop','BtnOpen')) { $mT0 += $i + '=' + (Get-Enabled $hwnd $i) + ' ' }
    Write-Output ('  enabled-matrix-test-stopped: ' + $mT0)
    $clickStart = Invoke-Click $hwnd 'BtnStart'
    $runMs = Wait-State $hwnd @($S_RUN, 'Running', $S_READY) 60000
    Add-Result ($clickStart -and $runMs -ge 0) 'test-started' ('click=' + $clickStart + ' status-ms=' + $runMs)
    $mT1 = ''
    foreach ($i in @('BtnStart','BtnRestart','BtnStop','BtnOpen')) { $mT1 += $i + '=' + (Get-Enabled $hwnd $i) + ' ' }
    Write-Output ('  enabled-matrix-test-running: ' + $mT1)
    $clickRestart = Invoke-Click $hwnd 'BtnRestart'
    $runMs2 = Wait-State $hwnd @($S_RUN, 'Running', $S_READY) 60000
    Add-Result ($clickRestart -and $runMs2 -ge 0) 'test-restarted' ('click=' + $clickRestart + ' again-ms=' + $runMs2)
    Save-Shot $hwnd 'ctx-test-running'
    [void](Invoke-Click $hwnd 'ExpInstanceSettings'); Start-Sleep -Milliseconds 600
    $saveOk = Invoke-Click $hwnd 'BtnSaveInstance'; Start-Sleep -Milliseconds 1200
    [void](Invoke-Click $hwnd 'ExpInstanceSettings')
    Add-Result $saveOk 'test-settings-save' 'instance settings save clicked (idempotent)'
    $delOk = Invoke-Click $hwnd 'BtnDelete'; Start-Sleep -Milliseconds 900
    $rootWin = $AE::FromHandle([IntPtr]$hwnd)
    $condBtn = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $dlg = $null
    $swD = [Diagnostics.Stopwatch]::StartNew()
    while ($swD.ElapsedMilliseconds -lt 3000) {
        foreach ($bb in $rootWin.FindAll($TS::Descendants, $condBtn)) {
            $nm = $bb.Current.Name
            if ($nm.Contains($S_CANCEL) -or $nm -eq 'Cancel') { $dlg = $bb; break }
        }
        if ($null -ne $dlg) { break }
        Start-Sleep -Milliseconds 200
    }
    $cancelled = $false
    if ($null -ne $dlg) {
        $pat = $null
        if ($dlg.TryGetCurrentPattern($IPC::Pattern, [ref]$pat)) { $pat.Invoke(); $cancelled = $true }
    }
    Add-Result ($delOk -and $cancelled) 'test-delete-dialog-cancel' ('delete clicked=' + $delOk + ' cancel clicked=' + $cancelled)
    Start-Sleep -Milliseconds 1200
    $clickStop = Invoke-Click $hwnd 'BtnStop'
    $stopMs = Wait-State $hwnd @($S_STOP, $S_NOTRUN, 'Stopped') 30000
    Add-Result ($clickStop -and $stopMs -ge 0) 'test-stopped' ('click=' + $clickStop + ' status-ms=' + $stopMs)
    # revamp: the new-instance entry is the rail-top button (BtnRailNew); with WSL distros
    # registered the app first shows an environment-choice dialog (primary = Windows) -
    # pick it, then the classic create form opens. The form still takes seconds (port
    # suggestion + async version fill), so poll for its cancel button up to 15s, gated on
    # the form's ChkSyncPresets checkbox (the only create-form control carrying an
    # AutomationId, so the env-choice dialog's own cancel button can never be mistaken
    # for the form's; create flow itself is covered end-to-end by --selftest-core [10]/[11]).
    $newOk = Invoke-Click $hwnd 'BtnRailNew'
    $envPicked = $false
    $swE = [Diagnostics.Stopwatch]::StartNew()
    while ($swE.ElapsedMilliseconds -lt 8000) {
        if ($null -ne (Find-ById $hwnd 'ChkSyncPresets')) { break }
        $pbE = Find-ById $hwnd 'PrimaryButton'
        if ($null -ne $pbE) {
            try {
                if ($pbE.Current.Name.Contains($S_ENVSEL)) {
                    $patE = $null
                    if ($pbE.TryGetCurrentPattern($IPC::Pattern, [ref]$patE)) { $patE.Invoke(); $envPicked = $true; break }
                }
            } catch { }
        }
        Start-Sleep -Milliseconds 400
    }
    Start-Sleep -Milliseconds 800
    $anyNew = $false
    $rootNow = $AE::FromHandle([IntPtr]$hwnd)
    $swN = [Diagnostics.Stopwatch]::StartNew()
    while ($swN.ElapsedMilliseconds -lt 15000) {
        if ($null -eq (Find-ById $hwnd 'ChkSyncPresets')) { Start-Sleep -Milliseconds 400; continue }
        foreach ($bb in $rootNow.FindAll($TS::Descendants, $condBtn)) {
            $nm = $bb.Current.Name
            if ($nm.Contains($S_CANCEL)) {
                $anyNew = $true
                $pat = $null
                if ($bb.TryGetCurrentPattern($IPC::Pattern, [ref]$pat)) { $pat.Invoke() }
                break
            }
        }
        if ($anyNew) { break }
        Start-Sleep -Milliseconds 400
    }
    Start-Sleep -Milliseconds 600
    Add-Result ($newOk -and $anyNew) 'test-new-dialog-cancel' ('rail-new clicked=' + $newOk + ' envDialogPicked=' + $envPicked + ' dialog cancel found=' + $anyNew)
    # G2 (subagent cold review): clone uses the same dialog path as new - prove it opens and cancels
    $cloneOk = Invoke-Click $hwnd 'BtnClone'
    $cloneDlg = $false
    $rootC = $AE::FromHandle([IntPtr]$hwnd)
    $swC = [Diagnostics.Stopwatch]::StartNew()
    while ($swC.ElapsedMilliseconds -lt 15000) {
        foreach ($bb in $rootC.FindAll($TS::Descendants, $condBtn)) {
            if ($bb.Current.Name.Contains($S_CANCEL)) {
                $cloneDlg = $true
                $pat = $null
                if ($bb.TryGetCurrentPattern($IPC::Pattern, [ref]$pat)) { $pat.Invoke() }
                break
            }
        }
        if ($cloneDlg) { break }
        Start-Sleep -Milliseconds 400
    }
    Start-Sleep -Milliseconds 600
    Add-Result ($cloneOk -and $cloneDlg) 'test-clone-dialog-cancel' ('clone clicked=' + $cloneOk + ' dialog cancelled=' + $cloneDlg)
    [void](Select-RailRow $hwnd 'windows:3080'); Start-Sleep -Milliseconds 1200
    $el2 = Find-ById $hwnd 'StatusText'; $st = ''; if ($el2) { $st = $el2.Current.Name }
    Add-Result (($st.Contains($S_RUN)) -or ($st -match 'Running')) 'ext-still-running' ('external status=' + $st)
    $p.Refresh(); [void]$p.CloseMainWindow(); [void]$p.WaitForExit(15000)
}
catch {
    Write-Output ('CAUGHT line ' + $_.InvocationInfo.ScriptLineNumber + ': ' + $_.Exception.Message)
    $global:detailFailed = $true
}
finally {
    if ($script:currentProc -and -not $script:currentProc.HasExited) { Stop-Process -Id $script:currentProc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    if (Test-Path $backup) { Copy-Item $backup $reg -Force; Remove-Item $backup -Force -ErrorAction SilentlyContinue; Write-Output 'registry restored' }
    Get-ChildItem (Join-Path $state 'archives') -Filter 'RTEST-*' -ErrorAction SilentlyContinue | Remove-Item -Force
    Remove-Item -Recurse -Force $homeT -ErrorAction SilentlyContinue
    Get-NetTCPConnection -State Listen -LocalPort 3185 -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    $crash = Get-ChildItem (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'DshController\error-reports') -Filter '*-crash_*.md' -ErrorAction SilentlyContinue |
             Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-8) }
    if ($crash) { $crash | ForEach-Object { Write-Output ('CRASH-REPORT: ' + $_.Name) }; $global:detailFailed = $true }
}
$bad = @($results | Where-Object { -not $_.Ok }).Count + $(if ($global:detailFailed) { 1 } else { 0 })
Write-Output ('SUMMARY total=' + $results.Count + ' fail=' + $bad)
if ($bad) { $results | Where-Object { -not $_.Ok } | ForEach-Object { Write-Output ('FAILED: ' + $_.Id + ' :: ' + $_.Detail) }; exit 1 }
exit 0
