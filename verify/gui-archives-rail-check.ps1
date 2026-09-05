# ============================================================================
#  gui-archives-rail-check.ps1 - archives page left rail walk
#  (sub-task "archives rail with retired entries")
#  Fixture: RTEST-A (port 3185, stays listed = active archive) and RTEST-R
#  (port 3186, removed between two app launches so its archive turns retired
#  via the archive mirror). Walks: totals row pinned first / active rows /
#  retired row with badge / default selection = totals / rows selectable
#  (totals + retired + active). External user instances are listed read-only
#  and never clicked. ASCII-only source: PS 5.1 reads BOM-less UTF-8 as GBK;
#  Chinese words are composed via [char]0xXXXX code sets.
#  Usage: powershell -ExecutionPolicy Bypass -File verify\gui-archives-rail-check.ps1 -Exe <exe> -Out <dir>
# ============================================================================
param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [Parameter(Mandatory=$true)][string]$Out,
    [int]$StartupSec = 25
)
$ErrorActionPreference = 'Stop'
$S_TOTALS  = [string]([char]0x603B) + [char]0x8BA1                        # zongji (totals)
$S_RETIRED = [string]([char]0x5DF2) + [char]0x9000 + [char]0x5F79         # retired badge
$S_ACTIVE  = [string]([char]0x6D3B) + [char]0x8DC3                        # huoyue (active)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Win32A {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lp, int nMax);
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
[void][Win32A]::SetProcessDPIAware()
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
$SIP = [System.Windows.Automation.SelectionItemPattern]
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
function Save-Shot([long]$hwnd, [string]$name) {
    $r = New-Object Win32A+RECT
    [void][Win32A]::GetWindowRect([IntPtr]$hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $sz = New-Object System.Drawing.Size($w, $h)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $sz)
    $bmp.Save((Join-Path $Out ($name + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
function Get-RailRows([long]$hwnd) {
    # rows = title|sub|badge texts joined; ListItem.Name is a class-name string in
    # WinUI, so all row text is read from the Text children (see session facts).
    $rows = @()
    $lv = Find-ById $hwnd 'ArchivesRailList'
    if ($null -eq $lv) { return $rows }
    foreach ($i in $lv.FindAll($TS::Descendants, $COND_LI)) {
        $texts = @()
        foreach ($tx in $i.FindAll($TS::Descendants, $COND_TX)) { $texts += $tx.Current.Name }
        $sel = $false
        $pat = $null
        if ($i.TryGetCurrentPattern($SIP::Pattern, [ref]$pat)) { $sel = $pat.Current.IsSelected }
        $rows += [pscustomobject]@{ Text = ($texts -join '|'); Selected = $sel; Item = $i }
    }
    return $rows
}
function Select-RowBy([long]$hwnd, [string]$needle) {
    $lv = Find-ById $hwnd 'ArchivesRailList'
    if ($null -eq $lv) { return $false }
    foreach ($i in $lv.FindAll($TS::Descendants, $COND_LI)) {
        $texts = @()
        foreach ($tx in $i.FindAll($TS::Descendants, $COND_TX)) { $texts += $tx.Current.Name }
        if (($texts -join '|').Contains($needle)) {
            $pat = $null
            if ($i.TryGetCurrentPattern($SIP::Pattern, [ref]$pat)) { $pat.Select(); return $true }
        }
    }
    return $false
}
function Wait-Rows([long]$hwnd, [int]$minRows, [int]$timeoutMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $rows = Get-RailRows $hwnd
        if ($rows.Count -ge $minRows) { return $sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 400
    }
    return -1
}
function Start-App([string]$exe, [int]$startupSec) {
    $p = Start-Process -FilePath $exe -PassThru
    $script:currentProc = $p
    $deadline = (Get-Date).AddSeconds($startupSec); $hwnd = [long]0
    while ((Get-Date) -lt $deadline) { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle.ToInt64(); break }; Start-Sleep -Milliseconds 250 }
    return @{ Proc = $p; Hwnd = $hwnd }
}
function Stop-App($app) {
    if ($null -ne $app) {
        $p = $app.Proc
        $p.Refresh(); [void]$p.CloseMainWindow(); [void]$p.WaitForExit(12000)
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
    $script:currentProc = $null
    Start-Sleep -Milliseconds 900
}
function Find-PopupHwnds([int]$procId) {
    # ContentDialog in WinUI shows as a separate top-level 'Popup'-class window
    $script:popups = @()
    $cb = [Win32A+EnumWindowsProc]{
        param($h, $l)
        $opid = 0
        [void][Win32A]::GetWindowThreadProcessId($h, [ref]$opid)
        if ($opid -eq $procId) {
            $sb = New-Object System.Text.StringBuilder 128
            [void][Win32A]::GetClassName($h, $sb, 128)
            if ($sb.ToString().Contains('Popup')) { $script:popups += $h.ToInt64() }
        }
        return $true
    }
    [void][Win32A]::EnumWindows($cb, [IntPtr]::Zero)
    return @($script:popups)
}
$state = Join-Path $env:LOCALAPPDATA 'DshController'
$reg = Join-Path $state 'instances.json'
$backup = Join-Path $env:TEMP ('dsh-archrail-backup-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
$homeA = Join-Path $env:TEMP 'dsh-archrail-home-a'
$homeR = Join-Path $env:TEMP 'dsh-archrail-home-r'
$aliasFile = Join-Path $state 'aliases.json'
$aliasBak = Join-Path $env:TEMP ('dsh-archrail-alias-bak-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
$S_ALIAS_A = [string]([char]0x751F) + [char]0x4EA7 + [char]0x4E00 + [char]0x53F7   # shengchan-1hao
$S_ALIAS_R = [string]([char]0x9000) + [char]0x5F79 + [char]0x673A                  # tuiyiji
function Write-Registry([object[]]$extras) {
    $doc = $null
    try { $doc = Get-Content $reg -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $doc = $null }
    if ($null -eq $doc) { $doc = [pscustomobject]@{ version = 2; instances = @() } }
    $existing = @(); if ($null -ne $doc.instances) { $existing = @($doc.instances | Where-Object { $_.id -notlike 'RTEST-*' }) }
    $doc | Add-Member -NotePropertyName instances -NotePropertyValue (@($existing + $extras)) -Force
    [IO.File]::WriteAllText($reg, ($doc | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))
}
function Mk-Inst([string]$id, [int]$port, [string]$homeDir) {
    return [pscustomobject]@{ id=$id; name=$id; port=$port; host='127.0.0.1'; runtime='windows'
        home=$homeDir; workspace=$env:TEMP; trustedHosts=@(); autoOpenBrowser=$false; stopOnExit=$true
        createdAt='2026-08-28T00:00:00Z'; lastStartedAt='2026-09-02T01:00:00Z'; wslDistro=''; wslHome=''; harnessVersion='' }
}

# ---- preflight: free test ports + clean stale fixtures ----
Get-NetTCPConnection -State Listen -LocalPort 3185 -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
Get-NetTCPConnection -State Listen -LocalPort 3186 -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
Remove-Item -Recurse -Force $homeA -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $homeR -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $homeA | Out-Null
New-Item -ItemType Directory -Force -Path $homeR | Out-Null

try {
    if (Test-Path $reg) { Copy-Item $reg $backup -Force } else { Set-Content -Path $backup -Value '{}' }
    if (Test-Path $aliasFile) { Copy-Item $aliasFile $aliasBak -Force } else { Set-Content -Path $aliasBak -Value '{}' }

    # ---- phase A: both test instances listed -> archives mirrored (both active) ----
    Write-Registry @((Mk-Inst 'RTEST-A' 3185 $homeA), (Mk-Inst 'RTEST-R' 3186 $homeR))
    $app1 = Start-App $Exe $StartupSec
    Add-Result ($app1.Hwnd -ne 0) 'phaseA-window' ('hwnd=' + $app1.Hwnd)
    if ($app1.Hwnd -eq 0) { throw 'no window phase A' }
    Start-Sleep -Seconds 9          # archive mirror writes RTEST-A / RTEST-R archives
    Stop-App $app1

    # ---- phase B: RTEST-R removed -> its archive turns retired on mirror ----
    # seed aliases so the rename effect is visible at startup (persisted rename state)
    $aliasDoc = @{ 'RTEST-A' = $S_ALIAS_A; 'RTEST-R' = $S_ALIAS_R } | ConvertTo-Json
    [IO.File]::WriteAllText($aliasFile, $aliasDoc, (New-Object System.Text.UTF8Encoding($false)))
    Write-Registry @((Mk-Inst 'RTEST-A' 3185 $homeA))
    $app = Start-App $Exe $StartupSec
    Add-Result ($app.Hwnd -ne 0) 'phaseB-window' ('hwnd=' + $app.Hwnd)
    if ($app.Hwnd -eq 0) { throw 'no window phase B' }
    Start-Sleep -Seconds 9          # mirror marks RTEST-R retired (file kept)
    $hwnd = $app.Hwnd

    # ---- enter archives page ----
    $navOk = Invoke-Click $hwnd 'BtnPageArch'
    Start-Sleep -Milliseconds 1200
    Add-Result $navOk 'enter-archives-page' ('BtnPageArch clicked=' + $navOk)
    $rowMs = Wait-Rows $hwnd 3 8000
    Add-Result ($rowMs -ge 0) 'rail-rows-present' ('rows>=3 in ' + $rowMs + 'ms')
    $rows = Get-RailRows $hwnd
    Write-Output ('  rail dump: ' + (($rows | ForEach-Object { $_.Text }) -join '  ##  '))

    # ---- assertions: three row kinds + default selection ----
    $first = $null; if ($rows.Count -ge 1) { $first = $rows[0] }
    Add-Result ($null -ne $first -and $first.Text.Contains($S_TOTALS)) 'totals-row-first' ('first=' + $(if ($null -ne $first) { $first.Text } else { 'ABSENT' }))
    $act = @($rows | Where-Object { $_.Text.Contains('windows:3185') })
    Add-Result ($act.Count -ge 1) 'active-row-present' ('matches=' + $act.Count)
    $ret = @($rows | Where-Object { $_.Text.Contains('windows:3186') })
    $retOk = ($ret.Count -ge 1) -and ($ret[0].Text.Contains($S_RETIRED))
    Add-Result $retOk 'retired-row-badged' ('text=' + $(if ($ret.Count -ge 1) { $ret[0].Text } else { 'ABSENT' }))
    $defOk = ($null -ne $first) -and ($first.Selected)
    Add-Result $defOk 'default-totals-selected' ('selected=' + $(if ($null -ne $first) { $first.Selected } else { 'n/a' }))
    Save-Shot $hwnd 'archives-rail-initial'

    # ---- selectable: retired row ----
    $selR = Select-RowBy $hwnd 'windows:3186'
    Start-Sleep -Milliseconds 700
    $rows2 = @(Get-RailRows $hwnd)
    $retSel = @($rows2 | Where-Object { $_.Text.Contains('windows:3186') })
    $retSelOk = ($selR -and $retSel.Count -ge 1 -and $retSel[0].Selected)
    Add-Result $retSelOk 'retired-row-selectable' ('select=' + $selR + ' isSel=' + $(if ($retSel.Count -ge 1) { $retSel[0].Selected } else { 'n/a' }))
    Save-Shot $hwnd 'archives-rail-retired-selected'

    # ---- selectable: active row, then totals again ----
    $selA = Select-RowBy $hwnd 'windows:3185'
    Start-Sleep -Milliseconds 700
    $rows3 = @(Get-RailRows $hwnd)
    $actSel = @($rows3 | Where-Object { $_.Text.Contains('windows:3185') })
    Add-Result ($selA -and $actSel.Count -ge 1 -and $actSel[0].Selected) 'active-row-selectable' ('select=' + $selA + ' isSel=' + $(if ($actSel.Count -ge 1) { $actSel[0].Selected } else { 'n/a' }))
    $selT = Select-RowBy $hwnd $S_TOTALS
    Start-Sleep -Milliseconds 700
    $rows4 = @(Get-RailRows $hwnd)
    $totSel = $null; if ($rows4.Count -ge 1) { $totSel = $rows4[0] }
    Add-Result ($selT -and $null -ne $totSel -and $totSel.Selected) 'totals-row-reselectable' ('select=' + $selT + ' isSel=' + $(if ($null -ne $totSel) { $totSel.Selected } else { 'n/a' }))

    # ---- meta panel: row click shows archive metadata (main area swap) ----
    [void](Select-RowBy $hwnd 'windows:3185')
    $sw = [Diagnostics.Stopwatch]::StartNew(); $mTitle = $null
    while ($sw.ElapsedMilliseconds -lt 5000) {
        $mTitle = Find-ById $hwnd 'MetaTitle'
        if ($null -ne $mTitle -and -not $mTitle.Current.IsOffscreen) { break }
        Start-Sleep -Milliseconds 300
    }
    $mState = Find-ById $hwnd 'MetaState'
    $mTitleName = ''; if ($null -ne $mTitle) { $mTitleName = $mTitle.Current.Name }
    $mStateName = ''; if ($null -ne $mState) { $mStateName = $mState.Current.Name }
    $m1 = ($null -ne $mTitle -and -not $mTitle.Current.IsOffscreen -and $mTitleName.Length -gt 0 -and $mStateName.Contains($S_ACTIVE))
    Add-Result $m1 'meta-active-row' ('title=' + $mTitleName + ' state=' + $mStateName)
    Save-Shot $hwnd 'archives-meta-active'

    [void](Select-RowBy $hwnd 'windows:3186')
    Start-Sleep -Milliseconds 900
    $mState2 = Find-ById $hwnd 'MetaState'
    $st2 = ''; if ($null -ne $mState2) { $st2 = $mState2.Current.Name }
    $m2 = ($st2.Contains($S_RETIRED))
    Add-Result $m2 'meta-retired-row' ('state=' + $st2)
    Save-Shot $hwnd 'archives-meta-retired'

    [void](Select-RowBy $hwnd $S_TOTALS)
    Start-Sleep -Milliseconds 900
    $mTitle3 = Find-ById $hwnd 'MetaTitle'
    $m3 = ($null -eq $mTitle3 -or $mTitle3.Current.IsOffscreen)
    Add-Result $m3 'meta-totals-falls-back' ('metaTitle offscreen=' + $(if ($null -ne $mTitle3) { $mTitle3.Current.IsOffscreen } else { 'absent-el' }))

    # ---- panel coexistence: active row -> both meta + usage visible ----
    [void](Select-RowBy $hwnd 'windows:3185')
    Start-Sleep -Milliseconds 1000
    $uHost = Find-ById $hwnd 'UsageHost'
    $mTitle = Find-ById $hwnd 'MetaTitle'
    $uOk = ($null -ne $uHost -and -not $uHost.Current.IsOffscreen)
    $mOk = ($null -ne $mTitle -and -not $mTitle.Current.IsOffscreen)
    Add-Result ($uOk -and $mOk) 'panels-both-active' ('usage=' + $uOk + ' meta=' + $mOk)

    # ---- panel coexistence: totals -> usage visible, meta collapsed ----
    [void](Select-RowBy $hwnd $S_TOTALS)
    Start-Sleep -Milliseconds 1000
    $uHost2 = Find-ById $hwnd 'UsageHost'
    $mTitle2 = Find-ById $hwnd 'MetaTitle'
    $u2Ok = ($null -ne $uHost2 -and -not $uHost2.Current.IsOffscreen)
    $m2Ok = ($null -eq $mTitle2 -or $mTitle2.Current.IsOffscreen)
    Add-Result ($u2Ok -and $m2Ok) 'panels-totals-usage-only' ('usage=' + $u2Ok + ' meta-offscreen=' + $m2Ok)

    # ---- rename: seeded alias shown everywhere (persisted rename state) ----
    $rows5 = @(Get-RailRows $hwnd)
    $allText = ($rows5 | ForEach-Object { $_.Text }) -join '  ##  '
    $aliasA = $allText.Contains($S_ALIAS_A)
    $aliasR = $allText.Contains($S_ALIAS_R)
    Add-Result ($aliasA -and $aliasR) 'rename-aliases-shown' ('aliasA=' + $aliasA + ' aliasR=' + $aliasR)
    Save-Shot $hwnd 'archives-rename-seeded'

    # ---- rename dialog opens from meta panel ----
    [void](Select-RowBy $hwnd 'windows:3185')
    Start-Sleep -Milliseconds 900
    $btnR = Invoke-Click $hwnd 'BtnRename'
    # dialog detection: in-app tree first (WinUI may host popup in same hwnd), else popup window
    $dlgRoot = $null; $detMs = 0
    $sw3 = [Diagnostics.Stopwatch]::StartNew()
    while ($sw3.ElapsedMilliseconds -lt 5000) {
        $inp = Find-ById $hwnd 'RenameInput'
        if ($null -ne $inp) { $dlgRoot = $AE::FromHandle([IntPtr]$hwnd); break }
        $pp = @(Find-PopupHwnds $app.Proc.Id)
        if ($pp.Count -ge 1) {
            $r2 = $AE::FromHandle([IntPtr]$pp[0])
            if ($null -ne $r2 -and $null -ne $r2.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'RenameInput')))) { $dlgRoot = $r2; break }
        }
        Start-Sleep -Milliseconds 300
    }
    $detMs = $sw3.ElapsedMilliseconds
    Add-Result ($btnR -and $null -ne $dlgRoot) 'rename-dialog-open' ('detect=' + $detMs + 'ms')
    Save-Shot $hwnd 'archives-rename-dialog'
    if ($null -ne $dlgRoot) {
        # type a new alias via ValuePattern then confirm via PrimaryButton
        $ed = $dlgRoot.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'RenameInput')))
        $typed = $false
        if ($null -ne $ed) {
            $vp = $null
            if ($ed.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) {
                $vp.SetValue('renamed-A'); $typed = $true
            }
        }
        Start-Sleep -Milliseconds 300
        $btnPrimary = $dlgRoot.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'PrimaryButton')))
        if ($null -eq $btnPrimary) {
            $condBtn = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
            foreach ($b in $dlgRoot.FindAll($TS::Descendants, $condBtn)) {
                $bn = $b.Current.Name
                if ($bn.Contains([string]([char]0x4FDD) + [char]0x5B58)) { $btnPrimary = $b; break }
            }
        }
        $confirmed = $false
        if ($null -ne $btnPrimary) {
            $ip = $null
            if ($btnPrimary.TryGetCurrentPattern($IPC::Pattern, [ref]$ip)) { $ip.Invoke(); $confirmed = $true }
        }
        Add-Result ($typed -and $confirmed) 'rename-dialog-commit' ('typed=' + $typed + ' confirmed=' + $confirmed)
        Start-Sleep -Milliseconds 1500
        $rows6 = @(Get-RailRows $hwnd)
        $all6 = ($rows6 | ForEach-Object { $_.Text }) -join '  ##  '
        Add-Result $all6.Contains('renamed-A') 'rename-live-committed' ('rail contains renamed-A=' + $all6.Contains('renamed-A'))
        Save-Shot $hwnd 'archives-renamed-live'
    } else {
        Add-Result $false 'rename-dialog-commit' ('no dialog to commit')
        Add-Result $false 'rename-live-committed' ('no dialog to commit')
    }

    Stop-App $app
}
catch {
    Write-Output ('CAUGHT line ' + $_.InvocationInfo.ScriptLineNumber + ': ' + $_.Exception.Message)
    $global:archFailed = $true
}
finally {
    if ($script:currentProc -and -not $script:currentProc.HasExited) { Stop-Process -Id $script:currentProc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    if (Test-Path $backup) { Copy-Item $backup $reg -Force; Remove-Item $backup -Force -ErrorAction SilentlyContinue; Write-Output 'registry restored' }
    if (Test-Path $aliasBak) { Copy-Item $aliasBak $aliasFile -Force; Remove-Item $aliasBak -Force -ErrorAction SilentlyContinue; Write-Output 'aliases restored' } else { Remove-Item $aliasFile -Force -ErrorAction SilentlyContinue }
    Get-ChildItem (Join-Path $state 'archives') -Filter 'RTEST-*' -ErrorAction SilentlyContinue | Remove-Item -Force
    Remove-Item -Recurse -Force $homeA -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $homeR -ErrorAction SilentlyContinue
    Get-NetTCPConnection -State Listen -LocalPort 3185 -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    Get-NetTCPConnection -State Listen -LocalPort 3186 -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    $crash = Get-ChildItem (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'DshController\error-reports') -Filter '*-crash_*.md' -ErrorAction SilentlyContinue |
             Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-8) }
    if ($crash) { $crash | ForEach-Object { Write-Output ('CRASH-REPORT: ' + $_.Name) }; $global:archFailed = $true }
}
$bad = @($results | Where-Object { -not $_.Ok }).Count + $(if ($global:archFailed) { 1 } else { 0 })
Write-Output ('SUMMARY total=' + $results.Count + ' fail=' + $bad)
if ($bad) { $results | Where-Object { -not $_.Ok } | ForEach-Object { Write-Output ('FAILED: ' + $_.Id + ' :: ' + $_.Detail) }; exit 1 }
exit 0
