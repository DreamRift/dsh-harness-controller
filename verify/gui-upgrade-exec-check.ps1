# ============================================================================
#  gui-upgrade-exec-check.ps1 - upgrade EXECUTION end-to-end (sub-task: upgrade-exec)
#  Fixture RTEST-U (port 3185, temp HOME) installs @tt-a1i/archify-dsh at 0.0.1;
#  real market latest is fetched from npm and the walk does:
#    1) ignore closure: dialog -> ignore -> button gone -> ledger file persisted
#    2) ledger reset -> hint returns
#    3) REAL upgrade click: busy -> npm install @latest into fixture HOME ->
#       row version jumps to v<latest>; archive plugins facet carries latest too
#  ASCII-only source (PS 5.1 GBK pitfall); CJK button names from char codes.
#  Self-restoring (registry/archives/home/ledger/port). Usage:
#    powershell -ExecutionPolicy Bypass -File verify\gui-upgrade-exec-check.ps1 -Exe <exe> -Out <dir>
# ============================================================================
param([Parameter(Mandatory=$true)][string]$Exe,[Parameter(Mandatory=$true)][string]$Out,[int]$StartupSec=25)
$ErrorActionPreference = 'Stop'
$S_UP  = [string]([char]0x5347) + [char]0x7EA7                      # sheng-ji (upgrade)
$S_IGN = ([string]([char]0x5FFD) + [char]0x7565 + [char]0x672C) + ([string][char]0x6B21 + [char]0x5347 + [char]0x7EA7)  # hu-lue-ben-ci-sheng-ji
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Win32E {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
[void][Win32E]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$results = @()
$script:currentProc = $null
function Add-Result([bool]$ok,[string]$id,[string]$detail){ $mark='FAIL'; if($ok){$mark='PASS'}; $script:results += [pscustomobject]@{Ok=$ok;Id=$id;Detail=$detail}; Write-Output ($mark+' '+$id+' '+$detail) }
$AE=[System.Windows.Automation.AutomationElement]; $TS=[System.Windows.Automation.TreeScope]; $IPC=[System.Windows.Automation.InvokePattern]
function Find-ById([long]$hwnd,[string]$id){ $root=$AE::FromHandle([IntPtr]$hwnd); if($null -eq $root){return $null}; return $root.FindFirst($TS::Descendants,(New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty,$id))) }
function Invoke-ClickEl($el){ if($null -eq $el){return $false}; $pat=$null; if($el.TryGetCurrentPattern($IPC::Pattern,[ref]$pat)){ try{ $pat.Invoke(); return $true }catch{ return $false } }; return $false }
function Invoke-Click([long]$hwnd,[string]$id){ for($k=1;$k -le 4;$k++){ $el=Find-ById $hwnd $id; if($null -ne $el){ if(Invoke-ClickEl $el){return $true} }; Start-Sleep -Milliseconds 400 }; return $false }
function Get-Row([long]$hwnd,[string]$rowText){ $lv=Find-ById $hwnd 'ListPlugins'; if($null -eq $lv){return $null}; $cLI=New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem); $cTx=New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text); foreach($it in $lv.FindAll($TS::Descendants,$cLI)){ $txt=''; foreach($t in $it.FindAll($TS::Descendants,$cTx)){ $txt += ' '+$t.Current.Name }; if($txt.Contains($rowText)){return $it} }; return $null }
function Get-RowBtn($row,[string]$name){ if($null -eq $row){return $null}; $cB=New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button); foreach($b in $row.FindAll($TS::Descendants,$cB)){ if($b.Current.Name -eq $name){return $b} }; return $null }
function Get-Dialog([long]$hwnd){ $root=$AE::FromHandle([IntPtr]$hwnd); if($null -eq $root){return $null}; $cc=New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty,'Popup'); $ct=New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,[System.Windows.Automation.ControlType]::Window); $and=New-Object System.Windows.Automation.AndCondition($ct,$cc); $cb=New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button); foreach($el in $root.FindAll($TS::Descendants,$and)){ if($el.FindAll($TS::Descendants,$cb).Count -gt 0){return $el} }; return $null }
function Save-Shot([long]$hwnd,[string]$name){ $r=New-Object Win32E+RECT; [void][Win32E]::GetWindowRect([IntPtr]$hwnd,[ref]$r); $w=$r.Right-$r.Left; $h=$r.Bottom-$r.Top; if($w -le 0 -or $h -le 0){return}; $bmp=New-Object System.Drawing.Bitmap($w,$h); $g=[System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.Left,$r.Top,0,0,(New-Object System.Drawing.Size($w,$h))); $bmp.Save((Join-Path $Out ($name+'.png')),[System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose() }
function Get-RowTexts($row){ $cTx=New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text); $out=@(); foreach($t in $row.FindAll($TS::Descendants,$cTx)){ $out += $t.Current.Name }; return $out }
function Wait-RowVersion([long]$hwnd,[string]$rowText,[string]$wanted,[int]$timeoutMs){ $sw=[Diagnostics.Stopwatch]::StartNew(); while($sw.ElapsedMilliseconds -lt $timeoutMs){ $row=Get-Row $hwnd $rowText; if($null -ne $row){ foreach($t in @(Get-RowTexts $row)){ if($t.Contains($wanted)){return $sw.ElapsedMilliseconds} } }; Start-Sleep -Milliseconds 800 }; return -1 }
$state = Join-Path $env:LOCALAPPDATA 'DshController'
$reg = Join-Path $state 'instances.json'
$ledger = Join-Path $state 'upgrade-ignores.json'
$backup = Join-Path $env:TEMP ('dsh-upx-'+[Guid]::NewGuid().ToString('N').Substring(0,8)+'.json')
$homeU = Join-Path $env:TEMP 'dsh-upx-home-u'
$pkg = '@tt-a1i/archify-dsh'
try {
    $meta = Invoke-RestMethod -Uri ('https://registry.npmjs.org/'+[Uri]::EscapeDataString($pkg)+'/latest') -TimeoutSec 25
    $latest = [string]$meta.version
    if (-not $latest -or $latest -notmatch '^[0-9]') { throw 'cannot resolve latest' }
    $fakeOld = '0.0.1'
    Write-Output ('  fixture: latest='+$latest+' old='+$fakeOld)
    if (Test-Path $homeU) { Remove-Item -Recurse -Force $homeU }
    New-Item -ItemType Directory -Force -Path (Join-Path $homeU 'profiles\web') | Out-Null
    $modDir = Join-Path $homeU ('profiles\web\node_modules\'+$pkg.Replace('/','\'))
    New-Item -ItemType Directory -Force -Path $modDir | Out-Null
    $depObj = @{ name='profile' }; $depObj.dependencies = @{ $pkg = '*' }
    [IO.File]::WriteAllText((Join-Path $homeU 'profiles\web\package.json'),($depObj|ConvertTo-Json -Depth 4),(New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $modDir 'package.json'),(@{name=$pkg;version=$fakeOld}|ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
    if (Test-Path $reg) { Copy-Item $reg $backup -Force } else { Set-Content -Path $backup -Value '{}' }
    $doc = $null; try { $doc = Get-Content $reg -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $doc = $null }
    if ($null -eq $doc) { $doc = [pscustomobject]@{ version=2; instances=@() } }
    $existing = @(); if ($null -ne $doc.instances) { $existing = @($doc.instances) }
    $u = [pscustomobject]@{ id='RTEST-U'; name='RTEST-U'; port=3185; host='127.0.0.1'; runtime='windows'; home=$homeU; workspace=$env:TEMP; trustedHosts=@(); autoOpenBrowser=$false; stopOnExit=$true; createdAt='2026-08-30T00:00:00Z'; lastStartedAt='2026-09-02T03:00:00Z'; wslDistro=''; wslHome=''; harnessVersion='' }
    $doc | Add-Member -NotePropertyName instances -NotePropertyValue (@($existing + @($u))) -Force
    [IO.File]::WriteAllText($reg,($doc|ConvertTo-Json -Depth 12),(New-Object Text.UTF8Encoding($false)))
    Get-NetTCPConnection -State Listen -LocalPort 3185 -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    $p = Start-Process -FilePath $Exe -PassThru; $script:currentProc = $p
    $deadline = (Get-Date).AddSeconds($StartupSec); $hwnd = [long]0
    while ((Get-Date) -lt $deadline) { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle.ToInt64(); break }; Start-Sleep -Milliseconds 250 }
    Add-Result ($hwnd -ne 0) 'window-smoke' ('hwnd='+$hwnd)
    if ($hwnd -eq 0) { throw 'no window' }
    Start-Sleep -Seconds 8
    Invoke-Click $hwnd 'BtnPagePlug' | Out-Null; Start-Sleep -Milliseconds 900
    Invoke-Click $hwnd 'BtnSubManage' | Out-Null; Start-Sleep -Milliseconds 1800
    $lvT = Find-ById $hwnd 'PluginTargetTree'; $selU = $false
    if ($null -ne $lvT) {
        $cLI = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
        $cTx = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)
        foreach ($i in $lvT.FindAll($TS::Descendants,$cLI)) { $txt=''; foreach ($tx in $i.FindAll($TS::Descendants,$cTx)) { $txt = $tx.Current.Name; break }; if ($txt.StartsWith('windows:3185')) { $pat=$null; if ($i.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pat)) { $pat.Select(); $selU=$true }; break } }
    }
    Add-Result $selU 'select-fixture' ('tree row='+$selU)
    $row = $null; $hintMs = -1
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 45000) {
        $row = Get-Row $hwnd $pkg
        if ($null -ne $row -and $null -ne (Get-RowBtn $row $S_UP)) { $hintMs = $sw.ElapsedMilliseconds; break }
        Start-Sleep -Milliseconds 1500
    }
    Add-Result ($hintMs -ge 0) 'upgrade-button-appears' ($hintMs.ToString()+'ms')
    if ($hintMs -lt 0) { throw 'no hint' }
    Save-Shot $hwnd 'step1-hint'
    # ---- (1) ignore closure ----
    [void](Invoke-ClickEl (Get-RowBtn $row $S_UP))
    $dlg = $null; $swD = [Diagnostics.Stopwatch]::StartNew()
    while ($swD.ElapsedMilliseconds -lt 8000) { $dlg = Get-Dialog $hwnd; if ($null -ne $dlg) { break }; Start-Sleep -Milliseconds 300 }
    if ($null -eq $dlg) { throw 'dialog missing' }
    [void](Invoke-ClickEl (Get-RowBtn $dlg $S_IGN))
    Start-Sleep -Milliseconds 3500
    $row2 = Get-Row $hwnd $pkg; $gone = $true
    if ($null -ne $row2 -and $null -ne (Get-RowBtn $row2 $S_UP)) { $gone = $false }
    Add-Result $gone 'ignore-hides-button' ('gone='+$gone)
    $ledgerHas = $false
    if (Test-Path $ledger) { $lj = Get-Content $ledger -Raw -Encoding UTF8; $ledgerHas = ($lj.Contains($pkg) -and $lj.Contains($latest)) }
    Add-Result $ledgerHas 'ledger-persisted' ('file='+(Test-Path $ledger)+' has='+$ledgerHas)
    Save-Shot $hwnd 'step2-ignored'
    # ---- (2) restart app after deleting the ledger: fresh in-memory state, hint returns ----
    $p.Refresh(); [void]$p.CloseMainWindow(); [void]$p.WaitForExit(15000)
    Remove-Item $ledger -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    $p = Start-Process -FilePath $Exe -PassThru; $script:currentProc = $p
    $dl2 = (Get-Date).AddSeconds($StartupSec); $hwnd2 = [long]0
    while ((Get-Date) -lt $dl2) { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $hwnd2 = $p.MainWindowHandle.ToInt64(); break }; Start-Sleep -Milliseconds 250 }
    if ($hwnd2 -eq 0) { throw 'no second window' }
    $hwnd = $hwnd2
    Start-Sleep -Seconds 8
    Invoke-Click $hwnd 'BtnPagePlug' | Out-Null; Start-Sleep -Milliseconds 900
    Invoke-Click $hwnd 'BtnSubManage' | Out-Null; Start-Sleep -Milliseconds 1800
    $row3 = $null; $hint2 = -1; $sw2 = [Diagnostics.Stopwatch]::StartNew()
    while ($sw2.ElapsedMilliseconds -lt 45000) {
        $row3 = Get-Row $hwnd $pkg
        if ($null -ne $row3 -and $null -ne (Get-RowBtn $row3 $S_UP)) { $hint2 = $sw2.ElapsedMilliseconds; break }
        Start-Sleep -Milliseconds 1500
    }
    Add-Result ($hint2 -ge 0) 'upgrade-hint-returns-after-restart' ($hint2.ToString()+'ms')
    if ($hint2 -lt 0) { throw 'hint not back after restart' }
    # ---- (3) REAL upgrade ----
    [void](Invoke-ClickEl (Get-RowBtn $row3 $S_UP))
    $dlg2 = $null; $swD2 = [Diagnostics.Stopwatch]::StartNew()
    while ($swD2.ElapsedMilliseconds -lt 8000) { $dlg2 = Get-Dialog $hwnd; if ($null -ne $dlg2) { break }; Start-Sleep -Milliseconds 300 }
    if ($null -eq $dlg2) { throw 'dialog2 missing' }
    $pUp = Invoke-ClickEl (Get-RowBtn $dlg2 $S_UP)
    Add-Result $pUp 'upgrade-primary-clicked' ('clicked='+$pUp)
    Save-Shot $hwnd 'step3-before'
    $jumpMs = Wait-RowVersion $hwnd $pkg ('v'+$latest) 120000
    Add-Result ($jumpMs -ge 0) 'row-version-jumped' ('to v'+$latest+' in '+$jumpMs+'ms')
    Save-Shot $hwnd 'step3-upgraded'
    Start-Sleep -Seconds 3
    $arc = Join-Path $state 'archives\RTEST-U.json'; $arcHas = $false
    if (Test-Path $arc) { $aj = Get-Content $arc -Raw -Encoding UTF8; $arcHas = $aj.Contains($latest) }
    Add-Result $arcHas 'archive-facet-refreshed' ('archive has v'+$latest+'='+$arcHas)
    Save-Shot $hwnd 'step4-archive'
    $p.Refresh(); [void]$p.CloseMainWindow(); $closed = $p.WaitForExit(15000)
    Add-Result $closed 'close' 'closed-ok'
}
catch { Write-Output ('CAUGHT line '+$_.InvocationInfo.ScriptLineNumber+': '+$_.Exception.Message); $global:upxFailed = $true }
finally {
    if ($script:currentProc -and -not $script:currentProc.HasExited) { Stop-Process -Id $script:currentProc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    if (Test-Path $backup) { Copy-Item $backup $reg -Force; Remove-Item $backup -Force -ErrorAction SilentlyContinue; Write-Output 'registry restored' }
    Get-ChildItem (Join-Path $state 'archives') -Filter 'RTEST-*' -ErrorAction SilentlyContinue | Remove-Item -Force
    Remove-Item $ledger -Force -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $homeU -ErrorAction SilentlyContinue
    Get-NetTCPConnection -State Listen -LocalPort 3185 -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    $crash = Get-ChildItem (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'DshController\error-reports') -Filter '*-crash_*.md' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-6) }
    if ($crash) { $crash | ForEach-Object { Write-Output ('CRASH-REPORT: '+$_.Name) }; $global:upxFailed = $true }
}
$bad = @($results | Where-Object { -not $_.Ok }).Count + $(if ($global:upxFailed) { 1 } else { 0 })
Write-Output ('SUMMARY total='+$results.Count+' fail='+$bad)
if ($bad) { $results | Where-Object { -not $_.Ok } | ForEach-Object { Write-Output ('FAILED: '+$_.Id+' :: '+$_.Detail) }; exit 1 }
exit 0
