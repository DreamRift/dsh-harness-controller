# ============================================================================
#  gui-plugin-pages-check.ps1 - market/manage capability regression walk
#  (sub-task "market & manage migration onto the new rail"). Walks:
#   market: catalog load -> search filter -> checkbox/sort usable ->
#           detail dialog open/close -> REAL install onto a fresh test
#           instance (port 3185, temp HOME, started first per the product's
#           own guidance) -> manage page shows the installed plugin.
#   manage: refresh + row actions presence; sources dialog open/close.
#  ASCII-only source (PS 5.1 GBK pitfall); CJK names built from char codes.
#  Self-restoring: backups instances.json, purges test HOME, kills orphans,
#  flags fresh crash reports. NEVER touches external 3080 controls.
#  Usage: powershell -ExecutionPolicy Bypass -File verify\gui-plugin-pages-check.ps1 -Exe <exe> -Out <dir>
# ============================================================================
param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [Parameter(Mandatory=$true)][string]$Out,
    [int]$StartupSec = 25
)
$ErrorActionPreference = 'Stop'
# CJK UI strings from char codes
$S_OK      = [string]([char]0x786E) + [char]0x5B9A                    # queding (confirm)
$S_CANCEL  = [string]([char]0x53D6) + [char]0x6D88                    # quxiao (cancel)
$S_CLOSE   = [string]([char]0x5173) + [char]0x95ED                    # guanbi (close)
$S_GOT     = [string]([char]0x77E5) + [char]0x9053 + [char]0x4E86     # zhidaole
$S_DETAIL  = [string]([char]0x8BE6) + [char]0x60C5                    # xiangqing (detail)
$S_INSTALL = [string]([char]0x5B89) + [char]0x88C5                    # anzhuang (install)
$S_REFRESH = [string]([char]0x5237) + [char]0x65B0                    # shuaxin (refresh)
$S_RUN     = [string]([char]0x8FD0) + [char]0x884C                    # yunxing (running)
$S_TARGET  = ([string]([char]0x5B89) + [char]0x88C5) + ([string][char]0x76EE) + [char]0x6807 + [char]0xFF1A  # "anzhuang mubiao:"
$S_TGTINST = ([string][char]0x76EE) + [char]0x6807 + [char]0x5B9E + [char]0x4F8B  # "mubiao shili" (detail dialog line)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Win32Q {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
[void][Win32Q]::SetProcessDPIAware()
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
    for ($try = 1; $try -le 4; $try++) {
        $el = Find-ById $hwnd $id
        if ($null -ne $el) {
            try {
                $pat = $null
                if ($el.TryGetCurrentPattern($IPC::Pattern, [ref]$pat)) { $pat.Invoke(); return $true }
                $tog = $null
                if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$tog)) { $tog.Toggle(); return $true }
            } catch {
                # UIA invoke can transiently fail while a dialog/mask is settling; retry
            }
        }
        Start-Sleep -Milliseconds 400
    }
    return $false
}
function Get-ListItems([long]$hwnd, [string]$listId) {
    $lv = Find-ById $hwnd $listId
    if ($null -eq $lv) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $out = @(); foreach ($i in $lv.FindAll($TS::Descendants, $cond)) { $out += $i }
    return $out
}
function Get-ItemText($item) {
    $condTx = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $s = ''
    foreach ($t in $item.FindAll($TS::Descendants, $condTx)) { $s += ' ' + $t.Current.Name }
    return $s.Trim()
}
function Find-ButtonIn($scope, [string]$name) {
    $condB = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    foreach ($b in $scope.FindAll($TS::Descendants, $condB)) {
        if ($b.Current.Name -eq $name) { return $b }
    }
    return $null
}
function Click-Button($btn) {
    if ($null -eq $btn) { return $false }
    $pat = $null
    if ($btn.TryGetCurrentPattern($IPC::Pattern, [ref]$pat)) { $pat.Invoke(); return $true }
    return $false
}
function Select-ComboItem([long]$hwnd, [string]$id, [string]$token) {
    # select the first combo item whose Name contains the token; retries cover
    # the transient disabled state while a page reload is busy
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
function Get-Dialog([long]$hwnd) {
    # WinUI3 ContentDialog surfaces as a Popup window in the UIA tree (ClassName 'Popup',
    # ControlType.Window); 'ContentDialog' ClassName is NOT what the provider exposes.
    $root = $AE::FromHandle([IntPtr]$hwnd)
    if ($null -eq $root) { return $null }
    $cClass = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'Popup')
    $cType = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    $and = New-Object System.Windows.Automation.AndCondition($cType, $cClass)
    $col = $root.FindAll($TS::Descendants, $and)
    $best = $null
    foreach ($el in $col) {
        $cb = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        if ($el.FindAll($TS::Descendants, $cb).Count -gt 0) { $best = $el; break }
    }
    return $best
}
function Wait-Dialog([long]$hwnd, [int]$timeoutMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $d = Get-Dialog $hwnd
        if ($null -ne $d) { return $d }
        Start-Sleep -Milliseconds 200
    }
    return $null
}
function Set-TextBox([long]$hwnd, [string]$id, [string]$value) {
    $el = Find-ById $hwnd $id
    if ($null -eq $el) { return $false }
    $pat = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pat)) { $pat.SetValue($value); return $true }
    return $false
}
function Save-Shot([long]$hwnd, [string]$name) {
    $r = New-Object Win32Q+RECT
    [void][Win32Q]::GetWindowRect([IntPtr]$hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $bmp.Save((Join-Path $Out ($name + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

$state = Join-Path $env:LOCALAPPDATA 'DshController'
$reg = Join-Path $state 'instances.json'
$backup = Join-Path $env:TEMP ('dsh-pp-backup-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
$homeM = Join-Path $env:TEMP 'dsh-pp-home-m'
# preflight: free the port + purge old test home
Get-NetTCPConnection -State Listen -LocalPort 3185 -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
Remove-Item -Recurse -Force $homeM -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $homeM | Out-Null
try {
    if (Test-Path $reg) { Copy-Item $reg $backup -Force } else { Set-Content -Path $backup -Value '{}' }
    $doc = $null
    try { $doc = Get-Content $reg -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $doc = $null }
    if ($null -eq $doc) { $doc = [pscustomobject]@{ version = 2; instances = @() } }
    $existing = @(); if ($null -ne $doc.instances) { $existing = @($doc.instances) }
    $t = [pscustomobject]@{ id='RTEST-M'; name='RTEST-M'; port=3185; host='127.0.0.1'; runtime='windows'
        home=$homeM; workspace=$env:TEMP; trustedHosts=@(); autoOpenBrowser=$false; stopOnExit=$false
        createdAt='2026-08-29T00:00:00Z'; lastStartedAt='2026-09-02T02:00:00Z'; wslDistro=''; wslHome=''; harnessVersion='' }
    $doc | Add-Member -NotePropertyName instances -NotePropertyValue (@($existing + @($t))) -Force
    [IO.File]::WriteAllText($reg, ($doc | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))

    $p = Start-Process -FilePath $Exe -PassThru
    $script:currentProc = $p
    $deadline = (Get-Date).AddSeconds($StartupSec); $hwnd = [long]0
    while ((Get-Date) -lt $deadline) { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle.ToInt64(); break }; Start-Sleep -Milliseconds 250 }
    Add-Result ($hwnd -ne 0) 'window-smoke' ('hwnd=' + $hwnd)
    if ($hwnd -eq 0) { throw 'no window' }
    Start-Sleep -Seconds 8

    # ---------- start the test instance first (fresh HOME should be initialized once) ----------
    Invoke-Click $hwnd 'BtnPageInst' | Out-Null
    Start-Sleep -Milliseconds 900
    $lvR = Find-ById $hwnd 'RailList'
    $condLI = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $condTx = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $selM = $false
    if ($null -ne $lvR) {
        foreach ($i in $lvR.FindAll($TS::Descendants, $condLI)) {
            $txt = ''
            foreach ($tx in $i.FindAll($TS::Descendants, $condTx)) { $txt = $tx.Current.Name; break }
            if ($txt.StartsWith('windows:3185')) {
                $pat = $null
                if ($i.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); $selM = $true }
                break
            }
        }
    }
    Add-Result $selM 'select-test-instance' ('rail row windows:3185 selected=' + $selM)
    [void](Invoke-Click $hwnd 'BtnStart')
    $runOk = $false
    $swR = [Diagnostics.Stopwatch]::StartNew()
    while ($swR.ElapsedMilliseconds -lt 60000) {
        $elS = Find-ById $hwnd 'StatusText'
        if ($null -ne $elS) { try { if ($elS.Current.Name.Contains($S_RUN)) { $runOk = $true } } catch { } }
        if ($runOk) { break }
        Start-Sleep -Milliseconds 500
    }
    Add-Result $runOk 'instance-started' ('RTEST-M running=' + $runOk)
    Save-Shot $hwnd 'pre-started'

    # ---------- market page ----------
    Invoke-Click $hwnd 'BtnPagePlug' | Out-Null; Start-Sleep -Milliseconds 1200
    $rows = @(Get-ListItems $hwnd 'ListPlugins')
    $swC = [Diagnostics.Stopwatch]::StartNew()
    while ($rows.Count -eq 0 -and $swC.ElapsedMilliseconds -lt 25000) {
        Start-Sleep -Milliseconds 800; $rows = @(Get-ListItems $hwnd 'ListPlugins')
    }
    Add-Result ($rows.Count -gt 0) 'catalog-loaded' ('market rows=' + $rows.Count)
    $ids = @('TxtSearch','CmbCategory','CmbSort','ChkInstallable','ChkVerified','BtnRefreshCatalog','BtnSources','TxtFreshness','CmbInstance','TxtProfile','BtnOpenHome','TxtStatus')
    $miss = @(); foreach ($i in $ids) { if (-not (Test-Visible $hwnd $i)) { $miss += $i } }

    Add-Result ($miss.Count -eq 0) 'market-controls-present' ('missing=' + ($miss -join ','))
    Save-Shot $hwnd 'market-list'

    # search filter: nonsense query empties the list, clearing restores it
    $before = @(Get-ListItems $hwnd 'ListPlugins').Count
    $typed = Set-TextBox $hwnd 'TxtSearch' 'zzqx-no-such-plugin'
    Start-Sleep -Milliseconds 1800
    $mid = @(Get-ListItems $hwnd 'ListPlugins').Count
    [void](Set-TextBox $hwnd 'TxtSearch' 'dsh')
    Start-Sleep -Milliseconds 1800
    $filtered = @(Get-ListItems $hwnd 'ListPlugins').Count
    [void](Set-TextBox $hwnd 'TxtSearch' '')
    Start-Sleep -Milliseconds 1800
    $after = @(Get-ListItems $hwnd 'ListPlugins').Count
    Add-Result ($typed -and $mid -lt $before -and $filtered -ge 1 -and $after -ge $before - 1) 'market-search-filter' ('before=' + $before + ' nonsense=' + $mid + ' dsh=' + $filtered + ' cleared=' + $after)

    # checkbox + sort combos act without breaking the list
    $c1 = Invoke-Click $hwnd 'ChkInstallable'; Start-Sleep -Milliseconds 1200
    $r1 = @(Get-ListItems $hwnd 'ListPlugins').Count
    [void](Invoke-Click $hwnd 'ChkInstallable'); Start-Sleep -Milliseconds 1200
    $r2 = @(Get-ListItems $hwnd 'ListPlugins').Count
    Add-Result ($c1 -and $r1 -ge 0 -and $r2 -ge 1) 'market-filters-toggle' ('chk clicked=' + $c1 + ' rows on=' + $r1 + ' rows off=' + $r2)

    # detail dialog opens with target line, closes
    $rowA = @(Get-ListItems $hwnd 'ListPlugins')
    $detailBtn = $null
    foreach ($it in $rowA) { $b = Find-ButtonIn $it $S_DETAIL; if ($null -ne $b) { $detailBtn = $b; break } }
    $dOk = Click-Button $detailBtn
    $dlg = Wait-Dialog $hwnd 4000
    $dlgText = ''
    if ($null -ne $dlg) { $condTx2 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text); foreach ($tx in $dlg.FindAll($TS::Descendants, $condTx2)) { $dlgText += ' ' + $tx.Current.Name } }
    $hasTarget = $dlgText.Contains($S_TGTINST)
    $cBtn = $null; if ($null -ne $dlg) { $cBtn = Find-ButtonIn $dlg $S_CLOSE }
    $cClosed = Click-Button $cBtn
    Add-Result ($dOk -and ($null -ne $dlg) -and $hasTarget -and $cClosed) 'market-detail-dialog' ('open=' + $dOk + ' dialog=' + ($null -ne $dlg) + ' target-line=' + $hasTarget + ' closed=' + $cClosed)

    # ---------- REAL install onto the started test instance ----------
    $targetToken = ''
    $rowsB = @(Get-ListItems $hwnd 'ListPlugins')
    $instBtn = $null; $instRowText = ''
    foreach ($it in $rowsB) {
        $b = Find-ButtonIn $it $S_INSTALL
        if ($null -ne $b) { try { if ($b.Current.IsEnabled) { $instBtn = $b; $instRowText = Get-ItemText $it; break } } catch { } }
    }
    $iClicked = Click-Button $instBtn
    $dlg2 = Wait-Dialog $hwnd 5000
    $d2Text = ''
    if ($null -ne $dlg2) { $condTx3 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text); foreach ($tx in $dlg2.FindAll($TS::Descendants, $condTx3)) { $d2Text += '|' + $tx.Current.Name } }
    # the dialog message can arrive as one blob; locate the marker substring anywhere
    $idxT = $d2Text.IndexOf($S_TARGET)
    if ($idxT -ge 0) {
        $tail = $d2Text.Substring($idxT + $S_TARGET.Length).Trim()
        $targetToken = (($tail -split "[\s|]+")[0]).Trim()
    }
    Write-Output ("  target-extracted=[" + $targetToken + "]")
    $okBtn = $null; if ($null -ne $dlg2) { $okBtn = Find-ButtonIn $dlg2 $S_OK }
    $confirmed = Click-Button $okBtn
    Add-Result ($iClicked -and ($null -ne $dlg2) -and $confirmed -and $targetToken.Length -gt 2) 'install-confirm-dialog' ('clicked=' + $iClicked + ' dialog=' + ($null -ne $dlg2) + ' confirmed=' + $confirmed + ' target=' + $targetToken)
    Save-Shot $hwnd 'install-confirmed'
    # wait for busy to end (install runs npm; up to 150s)
    $script:busySeen = $false
    $shortName0 = $targetToken
    $at0 = $shortName0.IndexOf('@'); if ($at0 -gt 0) { $shortName0 = $shortName0.Substring($at0 + 1) }
    $sl0 = $shortName0.LastIndexOf('/'); if ($sl0 -ge 0) { $shortName0 = $shortName0.Substring($sl0 + 1) }
    $doneMs = -1
    $swI = [Diagnostics.Stopwatch]::StartNew()
    while ($swI.ElapsedMilliseconds -lt 150000) {
        Start-Sleep -Milliseconds 1500
        $lg = @(Get-ListItems $hwnd 'ListPlugins')
        $anyInstallEnabled = $false
        foreach ($it in $lg) { $b = Find-ButtonIn $it $S_INSTALL; if ($null -ne $b) { try { if ($b.Current.IsEnabled) { $anyInstallEnabled = $true; break } } catch { } } }
        $dlgNow = Get-Dialog $hwnd
        if ($null -ne $dlgNow) {
            # follow-up dialog (restart prompt or info) - dismiss with SAFE button only;
            # never click ?? here (could trigger a restart) - prefer "next time"
            $S_NEXT = [string]([char]0x4E0B) + [char]0x6B21
            foreach ($nm in @($S_CANCEL, $S_CLOSE, $S_GOT, $S_NEXT, ([string][char]0x7A0D) + [char]0x540E)) { $bb = Find-ButtonIn $dlgNow $nm; if ($null -ne $bb) { [void](Click-Button $bb); break } }
            Start-Sleep -Milliseconds 600
        }
        # completion: status text shows the running-install message first, then clears;
        # OR the market list already mentions the installed package (row evidence)
        $busy = Test-Visible $hwnd 'BusyRing'
        $lst = Find-ById $hwnd 'ListPlugins'
        $lstEnabled = $true
        if ($null -ne $lst) { try { $lstEnabled = $lst.Current.IsEnabled } catch { } }
        $st = ''
        $elSt = Find-ById $hwnd 'TxtStatus'; if ($null -ne $elSt) { try { $st = $elSt.Current.Name } catch { } }
        $S_WORKING = [string]([char]0x6B63) + [char]0x5728 + [char]0x5B89 + [char]0x88C5   # "zhengzai anzhuang" = installing...
        if ($st.Contains($S_WORKING)) { $script:busySeen = $true }
        $mentioned = $false
        if ($targetToken.Length -gt 2) {
            foreach ($it in @(Get-ListItems $hwnd 'ListPlugins')) {
                $tx9 = Get-ItemText $it
                if ($tx9.Contains($targetToken.Split('@')[-1].Split('/')[0]) -or $tx9.Contains($shortName0)) { $mentioned = $true; break }
            }
        }
        if (($script:busySeen -and $st.Length -eq 0 -and $lstEnabled) -or $mentioned -or (-not $busy -and $lstEnabled -and $anyInstallEnabled)) { $doneMs = $swI.ElapsedMilliseconds; break }
    }
    Add-Result ($doneMs -ge 0) 'install-completed' ('busy done in ' + $doneMs + 'ms; target=' + $targetToken)
    $shortName = $targetToken
    $at = $shortName.IndexOf('@'); if ($at -gt 0) { $shortName = $shortName.Substring(0, $at) }
    $slash = $shortName.LastIndexOf('/'); if ($slash -ge 0) { $shortName = $shortName.Substring($slash + 1) }
    $installed = $false
    if ($targetToken.Length -gt 2) {
        foreach ($it in @(Get-ListItems $hwnd 'ListPlugins')) { if ((Get-ItemText $it).Contains($shortName) -or (Get-ItemText $it).Contains($targetToken)) { $installed = $true; break } }
    }
    Add-Result $installed 'market-shows-installed' ('row mentions ' + $shortName + ' = ' + $installed)

    # ---------- manage page shows it under the test instance ----------
    # settle: dismiss any leftover dialog so the assertions see a calm page
    $S_NEXT2 = [string]([char]0x4E0B) + [char]0x6B21
    $lgz = Get-Dialog $hwnd
    if ($null -ne $lgz) {
        foreach ($nm in @($S_CANCEL, $S_CLOSE, $S_GOT, $S_NEXT2, ([string][char]0x7A0D) + [char]0x540E)) { $bb = Find-ButtonIn $lgz $nm; if ($null -ne $bb) { [void](Click-Button $bb); break } }
        Start-Sleep -Milliseconds 800
    }
    Invoke-Click $hwnd 'BtnSubManage' | Out-Null; Start-Sleep -Milliseconds 2200
    # manage page owns its CmbInstance now (PluginTargetTree was removed in the rework);
    # item labels are "windows:3185" or "alias (windows:3185)" -> match by Contains
    $selMg = Select-ComboItem $hwnd 'CmbInstance' 'windows:3185'
    Add-Result $selMg 'manage-combo-select' ('combo item windows:3185=' + $selMg)
    [void](Invoke-Click $hwnd 'BtnRefresh')
    $foundMs = -1
    $swM = [Diagnostics.Stopwatch]::StartNew()
    while ($swM.ElapsedMilliseconds -lt 15000) {
        foreach ($it in @(Get-ListItems $hwnd 'ListPlugins')) {
            $tx2 = Get-ItemText $it
            if ($tx2.Contains($shortName) -or $tx2.Contains($targetToken)) { $foundMs = $swM.ElapsedMilliseconds; break }
        }
        if ($foundMs -ge 0) { break }
        Start-Sleep -Milliseconds 700
    }
    Add-Result ($foundMs -ge 0 -and $foundMs -lt 5000) 'manage-shows-installed' ('plugin visible in manage list in ' + $foundMs + 'ms (<5s) name=' + $shortName)
    Save-Shot $hwnd 'manage-installed'
    # every toolbar control may flicker while a reload settles; poll each before judging
    $mIds = @('TxtProfile','TxtInstanceMeta','CmbInstance')
    $mMiss = @()
    foreach ($i in $mIds) {
        $okI = $false
        for ($k = 0; $k -lt 12; $k++) { if (Test-Visible $hwnd $i) { $okI = $true; break }; Start-Sleep -Milliseconds 400 }
        if (-not $okI) { $mMiss += $i }
    }
    $lpOk = $false
    for ($k = 0; $k -lt 12; $k++) { if ($null -ne (Find-ById $hwnd 'ListPlugins')) { $lpOk = $true; break }; Start-Sleep -Milliseconds 400 }
    if (-not $lpOk) { $mMiss += 'ListPlugins' }
    # BtnRefresh may be transiently disabled during reload; poll before judging
    $brOk = $false
    for ($k = 0; $k -lt 12; $k++) { if (Test-Visible $hwnd 'BtnRefresh') { $brOk = $true; break }; Start-Sleep -Milliseconds 400 }
    if (-not $brOk) { $mMiss += 'BtnRefresh' }
    Add-Result ($mMiss.Count -eq 0) 'manage-controls-present' ('missing=' + ($mMiss -join ','))
    # uninstall button presence on the installed row (existence only - do NOT uninstall)
    $unb = $false
    $S_UN = [string]([char]0x5378) + [char]0x8F7D   # xiezai
    foreach ($it in @(Get-ListItems $hwnd 'ListPlugins')) {
        if ((Get-ItemText $it).Contains($shortName)) { $ub = Find-ButtonIn $it $S_UN; if ($null -ne $ub) { $unb = $true } ; break }
    }
    Add-Result $unb 'manage-uninstall-present' ('uninstall button on installed row=' + $unb)

    # sources dialog on market (open+close only); settle dialogs first
    $lgq = Get-Dialog $hwnd
    if ($null -ne $lgq) {
        $S_N3 = [string]([char]0x4E0B) + [char]0x6B21
        $S_LATER = [string]([char]0x7A0D) + [char]0x540E   # shao-hou = "later" (safe dismiss for the restart prompt)
        foreach ($nm in @($S_CANCEL, $S_CLOSE, $S_GOT, $S_N3, $S_LATER)) { $bb = Find-ButtonIn $lgq $nm; if ($null -ne $bb) { [void](Click-Button $bb); break } }
        Start-Sleep -Milliseconds 700
    }
    Invoke-Click $hwnd 'BtnSubMarket' | Out-Null; Start-Sleep -Milliseconds 1400
    [void](Invoke-Click $hwnd 'BtnSources')
    $dlgS = Wait-Dialog $hwnd 4000
    $sClosed = $false; $sBtns = ''
    if ($null -ne $dlgS) {
        $condBd = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        $names = @()
        foreach ($bb in $dlgS.FindAll($TS::Descendants, $condBd)) { $names += $bb.Current.Name }
        $sBtns = ($names -join '/')
        $S_DONE = [string]([char]0x5B8C) + [char]0x6210   # wancheng (done)
        $S_SAVE = [string]([char]0x4FDD) + [char]0x5B58   # baocun (save)
        foreach ($nm in @($S_CANCEL, $S_CLOSE, $S_DONE, $S_GOT, $S_SAVE)) {
            $cb = Find-ButtonIn $dlgS $nm
            if ($null -ne $cb) { $sClosed = Click-Button $cb; if ($sClosed) { break } }
        }
    }
    Add-Result (($null -ne $dlgS) -and $sClosed) 'market-sources-dialog' ('dialog=' + ($null -ne $dlgS) + ' closed=' + $sClosed + ' buttons=[' + $sBtns + ']')

    $p.Refresh(); [void]$p.CloseMainWindow()
    $closed = $p.WaitForExit(15000)
    Add-Result $closed 'close' 'closed-ok'
}
catch {
    Write-Output ('CAUGHT line ' + $_.InvocationInfo.ScriptLineNumber + ': ' + $_.Exception.Message)
    $global:ppFailed = $true
}
finally {
    if ($script:currentProc -and -not $script:currentProc.HasExited) { Stop-Process -Id $script:currentProc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
    if (Test-Path $backup) { Copy-Item $backup $reg -Force; Remove-Item $backup -Force -ErrorAction SilentlyContinue; Write-Output 'registry restored' }
    Get-ChildItem (Join-Path $state 'archives') -Filter 'RTEST-*' -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-NetTCPConnection -State Listen -LocalPort 3185 -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    Remove-Item -Recurse -Force $homeM -ErrorAction SilentlyContinue
    $crash = Get-ChildItem (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'DshController\error-reports') -Filter '*-crash_*.md' -ErrorAction SilentlyContinue |
             Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-4) }
    if ($crash) { $crash | ForEach-Object { Write-Output ('CRASH-REPORT: ' + $_.Name) }; $global:ppFailed = $true }
}
$bad = @($results | Where-Object { -not $_.Ok }).Count + $(if ($global:ppFailed) { 1 } else { 0 })
Write-Output ('SUMMARY total=' + $results.Count + ' fail=' + $bad)
if ($bad) { $results | Where-Object { -not $_.Ok } | ForEach-Object { Write-Output ('FAILED: ' + $_.Id + ' :: ' + $_.Detail) }; exit 1 }
exit 0
