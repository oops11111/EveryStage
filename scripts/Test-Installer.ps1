param([string]$InputDirectory,[string]$BaselineArchive,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted'){throw 'Installation acceptance is restricted to disposable GitHub-hosted runners'}
if(Test-Path $OutputDirectory){throw 'Evidence directory must be fresh'}
New-Item -ItemType Directory $OutputDirectory|Out-Null
$install="$env:ProgramFiles\EveryStage"
$data="$env:ProgramData\EveryStage"
$key='HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\EveryStage'
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class InstallerPaths {
 [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
 public static extern uint GetLongPathName(string input, StringBuilder output, uint capacity);
}
"@
function Canonical-ExistingPath([string]$path) {
 if([string]::IsNullOrWhiteSpace($path)){throw 'Shortcut target is empty'}
 $native=[IO.Path]::GetFullPath($path).Replace('/','\')
 if(!(Test-Path -LiteralPath $native -PathType Leaf)){throw "Shortcut target does not exist: $native"}
 $buffer=[Text.StringBuilder]::new(32768)
 $count=[InstallerPaths]::GetLongPathName($native,$buffer,[uint32]$buffer.Capacity)
 if($count -eq 0 -or $count -ge $buffer.Capacity){throw 'Existing target long-path resolution failed'}
 return $buffer.ToString()
}

Add-Type -Path "$PSScriptRoot/InstallerShellLink.cs"
$script:ShortcutSequence=0
function Capture-Shortcut([string]$shortcut,[string]$phase) {
 $script:ShortcutSequence++
 $folder=Join-Path $OutputDirectory ("shortcut-evidence/{0:D2}-{1}" -f $script:ShortcutSequence,$phase)
 New-Item -ItemType Directory -Path $folder -Force|Out-Null
 $native=(Get-Item -LiteralPath $shortcut).FullName.Replace('/','\')
 $bytes=[IO.File]::ReadAllBytes($native)
 $copy=Join-Path $folder ([IO.Path]::GetFileName($native))
 [IO.File]::WriteAllBytes($copy,$bytes)
 $hash=(Get-FileHash -LiteralPath $native).Hash.ToLowerInvariant()
 if((Get-FileHash -LiteralPath $copy).Hash.ToLowerInvariant() -ne $hash){throw 'Shortcut evidence copy mismatch'}
 $record=@{phase=$phase;path=$native;copy=$copy;bytes=$bytes.Length;sha256=$hash}
 try{$record.native_raw_path=[InstallerShellLink]::ReadRawPath($native)}catch{$record.native_error=$_.Exception.ToString()}
 if($bytes.Length -ge 28){$record.header_size=[BitConverter]::ToUInt32($bytes,0);$record.link_flags=[BitConverter]::ToUInt32($bytes,20);$record.file_attributes=[BitConverter]::ToUInt32($bytes,24)}
 $shell=New-Object -ComObject WScript.Shell
 try {
  $link=$shell.CreateShortcut($native)
  $record.pwsh7=@{apartment=[Threading.Thread]::CurrentThread.ApartmentState.ToString();shell=$PSVersionTable.PSVersion.ToString();target=$link.TargetPath;arguments=$link.Arguments;working_directory=$link.WorkingDirectory;icon=$link.IconLocation}
 } finally {[Runtime.InteropServices.Marshal]::ReleaseComObject($shell)|Out-Null}
 try {
  $independent=& powershell.exe -NoProfile -STA -File "$PSScriptRoot/Read-InstallerShortcut.ps1" -LiteralPath $native
  $record.winps5_exit=$LASTEXITCODE
  $record.winps5=($independent -join "`n")|ConvertFrom-Json
 } catch {$record.winps5_error=$_.Exception.ToString()}
 $record|ConvertTo-Json -Depth 10|Set-Content -Encoding utf8 (Join-Path $folder 'metadata.json')
 return $record
}
function Capture-ProductShortcuts([string]$phase) {
 foreach($root in @("$env:ProgramData/Microsoft/Windows/Start Menu/Programs/EveryStage","$env:PUBLIC/Desktop")) {
  foreach($name in @('EveryStage 终端机.lnk','EveryStage 投屏机.lnk')) {
   $file=Join-Path $root $name
   if(Test-Path -LiteralPath $file){Capture-Shortcut $file $phase|Out-Null}
  }
 }
}
function Run-UiHelper([string]$action,[Diagnostics.Process]$held) {
 $prefix=Join-Path $OutputDirectory ("ui-{0}-{1}" -f $action,[guid]::NewGuid().ToString('N'))
 $helper=(Resolve-Path "$PSScriptRoot/InstallerUi.ps1").Path
 $output=(Resolve-Path $OutputDirectory).Path
 $expectedStart=$held.StartTime.ToUniversalTime().ToString('o')
 $arguments=@('-NoProfile','-STA','-File',"`"$helper`"",'-ProcessId',$held.Id,'-Action',$action,'-OutputDirectory',"`"$output`"",'-ExpectedStartUtc',$expectedStart)
 $entry=@{action=$action;owned_pid=$held.Id;owned_start_utc=$expectedStart;helper_sha256=(Get-FileHash $helper).Hash.ToLowerInvariant();stdout="$prefix.stdout.txt";stderr="$prefix.stderr.txt"}
 $worker=Start-Process powershell.exe -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $entry.stdout -RedirectStandardError $entry.stderr
 $entry.helper_pid=$worker.Id
 try {
  if(!$worker.WaitForExit(120000)){throw 'UI helper timeout; no retry/force cleanup'}
  $entry.exit=$worker.ExitCode
  if($worker.ExitCode -ne 0){
   foreach($log in @($entry.stdout,$entry.stderr)){if(Test-Path $log){Write-Output "UI helper $action captured log: $log";Get-Content -LiteralPath $log -Raw}}
   throw "Installed UI $action helper failed: exit $($worker.ExitCode)"
  }
 } finally {$entry|ConvertTo-Json -Compress|Add-Content -Encoding utf8 "$OutputDirectory/ui-helper-calls.jsonl"}
}
function No-Processes {
 $processes=@(Get-Process -ErrorAction Stop|Where-Object ProcessName -in @('EveryStage.Terminal','EveryStage.Caster'))
 if($processes.Count){throw 'EveryStage process still running; refusing installer broad-kill path'}
}
function Run-Setup($file,$arguments) {
 No-Processes
 $entry=@{file=$file;sha256=(Get-FileHash $file).Hash.ToLowerInvariant();arguments=$arguments;started_utc=[DateTime]::UtcNow.ToString('o')}
 $process=Start-Process -FilePath $file -ArgumentList $arguments -WindowStyle Hidden -PassThru
 $entry.pid=$process.Id
 try {
  if(!$process.WaitForExit(120000)){throw 'Installer timeout; no retry'}
  $entry.exit=$process.ExitCode
  if($process.ExitCode -ne 0){throw "Installer exit $($process.ExitCode)"}
 } finally {
  $entry|ConvertTo-Json -Compress|Add-Content -Encoding utf8 "$OutputDirectory/installer-calls.jsonl"
 }
}
function Product-State {
 $run=Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
 return @{setup=Test-Path 'HKLM:\Software\EveryStage\Setup';uninstall=Test-Path $key;runTerminal=$run.PSObject.Properties.Name -contains 'EveryStageTerminal';runCaster=$run.PSObject.Properties.Name -contains 'EveryStageCaster';firewall=@(Get-NetFirewallRule -DisplayName 'EveryStage Terminal','EveryStage Caster' -ErrorAction SilentlyContinue|Select-Object -ExpandProperty Name);shortcuts=Test-Path "$env:ProgramData/Microsoft/Windows/Start Menu/Programs/EveryStage";install=Test-Path $install}
}
function Assert-ProductAbsent {
 $state=Product-State
 if((Test-Path "$env:PUBLIC/Desktop/EveryStage 终端机.lnk") -or (Test-Path "$env:PUBLIC/Desktop/EveryStage 投屏机.lnk")){throw 'Desktop shortcuts remain'}
 if($state.setup -or $state.uninstall -or $state.runTerminal -or $state.runCaster -or $state.firewall.Count -or $state.shortcuts -or $state.install){throw 'Product installation state is not absent'}
}
function Verify-Installation($selection) {
 $run=Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
 $setupState=Get-ItemProperty 'HKLM:\Software\EveryStage\Setup'
 foreach($component in @('Terminal','Caster')) {
  $selected=$selection -eq 'Both' -or $selection -eq $component
  $path=Join-Path $install $component
  if((Test-Path $path) -ne $selected){throw "Unexpected component directory $component"}
  $link=if($component -eq 'Terminal'){'EveryStage 终端机.lnk'}else{'EveryStage 投屏机.lnk'}
  foreach($shortcut in @("$env:ProgramData/Microsoft/Windows/Start Menu/Programs/EveryStage/$link","$env:PUBLIC/Desktop/$link")) {
   if((Test-Path $shortcut) -ne $selected){throw 'Component shortcut mismatch'}
   if($selected) {
    $captured=Capture-Shortcut $shortcut 'upgraded-or-selected'
    $shortcutRecord=@{link=(Get-Item -LiteralPath $shortcut).FullName;expected="$path\EveryStage.$component.exe"}
    try {
     # Canonicalize existing paths and retain raw values; the empty-target cause is not yet established.
     $nativeLink=$shortcutRecord.link.Replace('/','\')
     $shortcutRecord.wsh_raw_target=$captured.pwsh7.target
     $shortcutRecord.raw_target=[InstallerShellLink]::ReadRawPath($nativeLink)
     $shortcutRecord.resolved_expected=Canonical-ExistingPath $shortcutRecord.expected
     $shortcutRecord.resolved_target=Canonical-ExistingPath $shortcutRecord.raw_target
     if(![string]::Equals($shortcutRecord.resolved_target,$shortcutRecord.resolved_expected,[StringComparison]::OrdinalIgnoreCase)){throw 'Shortcut target mismatch'}
     $shortcutRecord.status='PASS exact existing canonical target'
    } catch {
     $shortcutRecord.error=$_.Exception.ToString()
     throw
    } finally {
     $shortcutRecord|ConvertTo-Json -Compress|Add-Content -Encoding utf8 "$OutputDirectory/shortcut-targets.jsonl"
    }
   }
  }
  $rules=@(Get-NetFirewallRule -DisplayName "EveryStage $component" -ErrorAction SilentlyContinue)
  if(($rules.Count -gt 0) -ne $selected){throw 'Component firewall presence mismatch'}
  if(($run.PSObject.Properties.Name -contains "EveryStage$component") -ne $selected){throw 'Component Run presence mismatch'}
  if($selected) {
   if($setupState.$component -ne 1){throw 'Component Setup registration mismatch'}
   $expected=@($receipt.published.$component)
   $actual=@(Get-ChildItem $path -Recurse -File)
   if($actual.Count -ne $expected.Count){throw "Stale/missing installed files $component"}
   foreach($file in $expected){if((Get-FileHash (Join-Path $path $file.path)).Hash.ToLowerInvariant() -ne $file.sha256){throw "Installed file drift $component/$($file.path)"}}
   if($run."EveryStage$component" -ne "`"$path\EveryStage.$component.exe`""){throw 'Autostart path mismatch'}
   foreach($rule in $rules) {
    $app=$rule|Get-NetFirewallApplicationFilter
    $port=$rule|Get-NetFirewallPortFilter
    $address=$rule|Get-NetFirewallAddressFilter
    if($app.Program -ne "$path\EveryStage.$component.exe" -or $port.Protocol -ne 'UDP' -or (@($port.LocalPort) -join ',') -ne '47990-47992' -or (@($address.RemoteAddress) -join ',') -ne 'LocalSubnet' -or $rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Allow' -or $rule.Enabled -ne 'True'){throw 'Firewall rule content mismatch'}
   }
  } elseif($setupState.PSObject.Properties.Name -contains $component){throw 'Unselected Setup component registered'}
 }
 $short=$receipt.source.checkout_commit.Substring(0,7)
 if((Get-ItemProperty $key).DisplayVersion -ne "0.9.2 ($short)"){throw 'Uninstall version registration mismatch'}
 $acl=Get-Acl $data
 $users=@($acl.Access|Where-Object {$_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-32-545' -and $_.AccessControlType -eq 'Allow'})
 if(!@($users|Where-Object {($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -eq [Security.AccessControl.FileSystemRights]::Modify -and ($_.InheritanceFlags -band 3) -eq 3}).Count){throw 'Users inherited Modify grant missing'}
 $secure=Get-Acl "$data/secure"
 if(!$secure.AreAccessRulesProtected){throw 'Secure ACL inheritance not protected'}
 foreach($rule in $secure.Access) {
  $allowed=[Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [Security.AccessControl.FileSystemRights]::WriteData -bor [Security.AccessControl.FileSystemRights]::AppendData -bor [Security.AccessControl.FileSystemRights]::Synchronize
  if($rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-32-545' -and (($rule.FileSystemRights -band (-bnot $allowed)) -ne 0 -or $rule.InheritanceFlags -ne 'None')){throw 'Secure Users rights exceed directory-only RX,WD,AD'}
 }
 @($acl,$secure)|Format-List|Out-String|Add-Content "$OutputDirectory/installed-acl.txt"
}
function Uninstall($deleteData) {
 No-Processes
 $args='/S'
 if($deleteData){$args+=' /DELETEDATA=1'}
 Run-Setup "$install\Uninstall.exe" $args
 # NSIS may hand off to a temporary copy. Do not equate initial process exit with completion.
 for($i=0;$i -lt 120;$i++) {
  if(!(Test-Path $key) -and !(Test-Path "$install\Terminal") -and !(Test-Path "$install\Caster")){break}
  Start-Sleep -Milliseconds 500
 }
 if((Test-Path $key) -or (Test-Path "$install\Terminal") -or (Test-Path "$install\Caster")){throw 'Uninstall artifacts remain'}
 No-Processes
 for($i=0;$i -lt 60;$i++) {
  $remaining=@(Get-CimInstance Win32_Process|Where-Object {$_.Name -like '*~ns*.tmp' -or $_.Name -in @('Uninstall.exe','Un.exe')})
  @{observed_utc=[DateTime]::UtcNow.ToString("o");processes=@($remaining|Select-Object ProcessId,ParentProcessId,ExecutablePath,CreationDate,Name)}|ConvertTo-Json -Depth 6 -Compress|Add-Content -Encoding utf8 "$OutputDirectory/uninstaller-process-observations.jsonl"
  if(!$remaining.Count){break}
  Start-Sleep -Milliseconds 500
 }
 if($remaining.Count){throw 'Uninstaller temporary process still live'}
 Assert-ProductAbsent
 foreach($component in @('Terminal','Caster')) {
  if(Get-NetFirewallRule -DisplayName "EveryStage $component" -ErrorAction SilentlyContinue){throw 'Firewall rule remained after uninstall'}
  if((Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run').PSObject.Properties.Name -contains "EveryStage$component"){throw 'Autorun remained after uninstall'}
 }
}
$result=@{status='STOP';ordinary_app='NOT TESTED';cases=@()}
try {
 No-Processes
 Assert-ProductAbsent
 if(Test-Path $data){throw 'Runner has preexisting EveryStage data'}
 if((Get-FileHash $BaselineArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne '76614f31aa34372c15f0de26d69f818f89c3b5814967b24a0aa980b4c7eaad94'){throw 'Baseline archive hash mismatch'}
 Expand-Archive $BaselineArchive "$OutputDirectory/baseline"
 $baseline=Get-Item "$OutputDirectory/baseline/EveryStage-Setup-0.9.1-win-x64.exe"
 if((Get-FileHash $baseline -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'c6219e786d34affb1b69099ed3e6b0d6af3e2e86c131643e41ea66d6a30560d9'){throw 'Baseline executable hash mismatch'}
 $receipt=Get-Content "$InputDirectory/build-input.json" -Raw|ConvertFrom-Json
 foreach($file in $receipt.files){if((Get-FileHash (Join-Path $InputDirectory $file.path)).Hash.ToLowerInvariant() -ne $file.sha256){throw "Input drift $($file.path)"}}
 $setup=Join-Path $InputDirectory $receipt.installer
 Run-Setup $baseline.FullName '/S'
 if(!(Test-Path "$install/Terminal") -or (Test-Path "$install/Caster")){throw 'Baseline default Terminal selection mismatch'}
 Capture-ProductShortcuts 'baseline-before-upgrade'
 New-Item -ItemType Directory -Force "$data/logs"|Out-Null
 Set-Content "$install/Terminal/p3-stale-file.txt" 'owned stale upgrade file'
 Set-Content "$data/p3-sentinel.txt" 'owned upgrade sentinel'
 Set-Content "$data/logs/p3-sentinel.log" 'owned retained log'
 $sentinelHash=(Get-FileHash "$data/p3-sentinel.txt").Hash
 $logSentinelHash=(Get-FileHash "$data/logs/p3-sentinel.log").Hash
 Run-Setup $setup '/S'
 Capture-ProductShortcuts 'upgraded-before-assertions'
 Verify-Installation 'Terminal'
 if(Test-Path "$install/Terminal/p3-stale-file.txt"){throw 'Upgrade retained stale program file'}
 if((Get-FileHash "$data/p3-sentinel.txt").Hash -ne $sentinelHash){throw 'Upgrade changed sentinel bytes'}
 if((Get-Item "$install/Terminal/EveryStage.Terminal.exe").VersionInfo.FileVersion -ne '0.9.2.0'){throw 'Installed version mismatch'}
 No-Processes
 $launchLink=(Get-Item -LiteralPath "$env:ProgramData/Microsoft/Windows/Start Menu/Programs/EveryStage/EveryStage 终端机.lnk").FullName
 $launchTarget=[InstallerShellLink]::ReadRawPath($launchLink)
 $expectedExe=Canonical-ExistingPath "$install/Terminal/EveryStage.Terminal.exe"
 if((Canonical-ExistingPath $launchTarget) -ne $expectedExe){throw 'Shortcut launch target drift'}
 $launchStart=[DateTime]::UtcNow
 $launchRecord=@{shortcut=$launchLink;shortcut_sha256=(Get-FileHash $launchLink).Hash.ToLowerInvariant();native_target=$launchTarget;started_utc=$launchStart.ToString('o')}
 try {
  $terminal=Start-Process -FilePath $launchLink -PassThru
  if(!$terminal) {
   # ShellExecute may not return a process handle. Only this fresh exact target is eligible.
   for($i=0;$i -lt 30;$i++) {
    $matches=@(Get-Process -Name 'EveryStage.Terminal' -ErrorAction SilentlyContinue|Where-Object {$_.Path -eq $expectedExe -and $_.StartTime.ToUniversalTime() -ge $launchStart})
    if($matches.Count -gt 1){throw 'Ambiguous newly launched Terminal processes'}
    if($matches.Count -eq 1){$terminal=$matches[0];break}
    Start-Sleep -Milliseconds 100
   }
  }
  if(!$terminal -or $terminal.Path -ne $expectedExe -or $terminal.StartTime.ToUniversalTime() -lt $launchStart){throw 'Shortcut did not launch a verified new exact Terminal PID'}
  $launchRecord.pid=$terminal.Id
  $launchRecord.process_started_utc=$terminal.StartTime.ToUniversalTime().ToString('o')
  $launchRecord.verified_path=$terminal.Path
 } catch {$launchRecord.error=$_.Exception.ToString();throw} finally {
  $launchRecord|ConvertTo-Json -Depth 8|Set-Content -Encoding utf8 "$OutputDirectory/shortcut-launch.json"
 }
 try {
  Run-UiHelper 'Exercise' $terminal
  $result.ordinary_app='PASS ordinary installed Terminal drawer'
 } catch {
  $result.primary_error=$_.Exception.ToString()
 } finally {
  try {
   if(!$terminal.HasExited){Run-UiHelper 'Exit' $terminal}
   if($terminal.HasExited -and $terminal.ExitCode -ne 0){throw 'Owned Terminal exited nonzero'}
  } catch {$result.exit_error=$_.Exception.ToString()}
 }
 if($result.primary_error){throw $result.primary_error}
 if($result.exit_error){throw $result.exit_error}
 No-Processes
 $expected=@($receipt.package_members|Where-Object path -like '*fluidmotion.dll')
 $loaded=@(Get-Content "$OutputDirectory/loaded-modules.json" -Raw|ConvertFrom-Json)
 foreach($name in @('FluidMotion.L1.dll','fluidmotion.dll')) {
  $item=@($loaded|Where-Object name -ceq $name)
  $member=@($receipt.package_members|Where-Object {$_.path.EndsWith('/'+$name,[StringComparison]::Ordinal)})
  if($item.Count -ne 1 -or $member.Count -ne 1 -or $item[0].path -ne "$install\Terminal\$name" -or $item[0].sha256 -ne $member[0].sha256){throw "Actual mapped module mismatch $name"}
 }
 $native=Get-FileHash "$install/Terminal/fluidmotion.dll" -Algorithm SHA256
 if($native.Hash.ToLowerInvariant() -ne $expected[0].sha256){throw 'Installed native package mismatch'}
 Uninstall $false
 if((Get-FileHash "$data/p3-sentinel.txt").Hash -ne $sentinelHash){throw 'Default uninstall changed sentinel bytes'}
 foreach($selection in @('Terminal','Caster','Both')) {
  Run-Setup $setup "/S /COMPONENTS=$selection"
  $hasTerminal=Test-Path "$install/Terminal/EveryStage.Terminal.exe"
  $hasCaster=Test-Path "$install/Caster/EveryStage.Caster.exe"
  if($hasTerminal -ne ($selection -ne 'Caster') -or $hasCaster -ne ($selection -ne 'Terminal')){throw "Component selection mismatch $selection"}
  Verify-Installation $selection
  $result.cases+=@{selection=$selection;terminal=$hasTerminal;caster=$hasCaster}
  Uninstall ($selection -eq 'Both')
 }
 if((Test-Path "$data/p3-sentinel.txt") -or (Get-FileHash "$data/logs/p3-sentinel.log").Hash -ne $logSentinelHash){throw 'Delete-data/log retention byte mismatch'}
 No-Processes
 $invalid=Start-Process $setup -ArgumentList '/S /COMPONENTS=Invalid' -WindowStyle Hidden -PassThru
 $invalid.WaitForExit()
 if($invalid.ExitCode -ne 2){throw 'Invalid component exit was not 2'}
 Assert-ProductAbsent
 $result.status='PASS pipeline installer acceptance; primary acceptance pending'
} catch {$result.error=$_.Exception.ToString();throw} finally {
 $result|ConvertTo-Json -Depth 12|Set-Content -Encoding utf8 "$OutputDirectory/result.json"
}
