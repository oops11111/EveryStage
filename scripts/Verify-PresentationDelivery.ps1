param([string]$DeliveryDirectory=$PSScriptRoot,[switch]$AllowPending)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($DeliveryDirectory).TrimEnd('\')
function Require($condition,[string]$message) {if(!$condition){throw $message}}
function Sha([string]$path) {
 $stream=[IO.File]::OpenRead($path);$algorithm=[Security.Cryptography.SHA256]::Create()
 try{return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}
 finally{$stream.Dispose();$algorithm.Dispose()}
}
function Safe([string]$relative) {
 Require (![string]::IsNullOrWhiteSpace($relative) -and !$relative.Contains(':') -and ![IO.Path]::IsPathRooted($relative) -and $relative.Split([char[]]'/\') -notcontains '..') 'Unsafe delivery path'
 $full=[IO.Path]::GetFullPath((Join-Path $root $relative))
 Require ($full.Equals($root,[StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) 'Delivery prefix escape'
 for($item=Get-Item -LiteralPath $full;$item;) {
  Require (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Reparse path refused'
  if($item -is [IO.FileInfo]){$item=$item.Directory}else{$item=$item.Parent}
 }
 return $full
}
function Read([string]$relative) {Get-Content -LiteralPath (Safe $relative) -Raw -Encoding UTF8|ConvertFrom-Json}
function Verify-Tree([string]$directory) {
 $base=Safe $directory
 $sums=Join-Path $base 'SHA256SUMS'
 if(!(Test-Path -LiteralPath $sums)){return}
 $expected=@{}
 foreach($line in Get-Content -LiteralPath $sums -Encoding UTF8) {
  Require ($line -cmatch '^([0-9a-f]{64})  (.+)$') 'Invalid checksum row'
  $digest=$Matches[1];$relative=$Matches[2]
  Require (!$expected.ContainsKey($relative)) 'Duplicate checksum path'
  $prefix=if($directory -eq '.') {''}else{$directory.TrimEnd('/')+'/'}
  $path=Safe ($prefix+$relative)
  Require ((Sha $path) -ceq $digest) "SHA mismatch: $relative"
  $expected[$relative]=$digest
 }
 $actual=@(Get-ChildItem -LiteralPath $base -Recurse -File -Force|Where-Object FullName -ne $sums|ForEach-Object {$_.FullName.Substring($base.Length+1).Replace('\','/')})
 Require ($actual.Count -eq $expected.Count -and @($actual|Where-Object {!$expected.ContainsKey($_)}).Count -eq 0) 'Unlisted or missing file'
 foreach($nested in Get-ChildItem -LiteralPath $base -Recurse -File -Force -Filter SHA256SUMS) {
  if($nested.FullName -ne $sums){Verify-Tree ($nested.Directory.FullName.Substring($root.Length+1).Replace('\','/'))}
 }
}
# Validate every directory before recursive enumeration (never follow a junction).
$null=Safe '.'
$pending=[Collections.Generic.Queue[string]]::new();$pending.Enqueue($root)
while($pending.Count) {
 foreach($entry in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
  Require (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Delivery tree contains reparse point'
  if($entry.PSIsContainer){$pending.Enqueue($entry.FullName)}
 }
}
Require (Test-Path -LiteralPath "$root/SHA256SUMS") 'Root checksum manifest missing'
Verify-Tree '.'
$m=Read 'manifest.json'
Require ($m.app_version -ceq '0.9.3' -and $m.application_commit -cmatch '^[0-9a-f]{40}$' -and $m.component_source_commit -ceq '44401d3f002b427d828500331a3e9b4ada5881cd' -and $m.run -gt 0) 'Source/version binding mismatch'
Require ((Sha (Safe $m.installer.path)) -ceq $m.installer.sha256 -and (Sha (Safe $m.package.path)) -ceq $m.package.sha256) 'Product binding mismatch'
Require ($m.installer.authenticode -ceq 'NotSigned') 'Unexpected recorded signing status'
$build=Read 'evidence/input/build-input.json';$state=Read 'evidence/input/component-state.json'
Require ($build.source.checkout_commit -ceq $m.application_commit -and $build.source.event_commit -ceq $m.application_commit -and $build.app_version -ceq $m.app_version -and $build.assembly_file_version -ceq ($m.app_version+'.0')) 'Build/version source mismatch'
[xml]$props=Get-Content -LiteralPath (Safe 'evidence/input/Directory.Build.props') -Raw -Encoding UTF8
Require ($props.Project.PropertyGroup.EveryStageVersion -ceq $m.app_version -and (Sha (Safe 'evidence/input/Directory.Build.props')) -ceq $build.version_props_sha256) 'Immutable props mismatch'
Require ($state.source_commit -ceq $m.component_source_commit -and $state.package_sha256 -ceq $m.package.sha256) 'Component provenance mismatch'
$mapping=@{}
foreach($item in $m.input_mapping){Require (!$mapping.ContainsKey($item.input_path)) 'Duplicate input mapping';$mapping[$item.input_path]=$item}
foreach($map in $m.input_mapping) {Require ((Sha (Safe $map.delivery_path)) -ceq $map.sha256) 'Mapped input byte mismatch'}
foreach($item in $build.files) {
 Require ($mapping.ContainsKey($item.path)) 'Missing input mapping'
 $map=$mapping[$item.path];$path=Safe $map.delivery_path
 Require ((Sha $path) -ceq $item.sha256 -and $map.sha256 -ceq $item.sha256 -and (Get-Item -LiteralPath $path).Length -eq $item.bytes) 'Input bytes drift'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package=[IO.Compression.ZipFile]::OpenRead((Safe $m.package.path))
try {
 Require (@($build.package_members.path|Sort-Object -Unique).Count -eq $build.package_members.Count) 'Duplicate declared package member'
 Require ($package.Entries.Count -eq $build.package_members.Count) 'Package member set mismatch'
 foreach($member in $build.package_members) {
  $matches=@($package.Entries|Where-Object FullName -ceq $member.path);Require ($matches.Count -eq 1) 'Package member missing/duplicate'
  $stream=$matches[0].Open();$algorithm=[Security.Cryptography.SHA256]::Create()
  try{$hash=[BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$stream.Dispose();$algorithm.Dispose()}
  Require ($hash -ceq $member.sha256) 'Package member byte mismatch'
 }
} finally {$package.Dispose()}
$exitProof=Read 'evidence/smoke/normal-exit.json';$launch=Read 'evidence/smoke/shortcut-launch.json'
Require ($null -ne $exitProof.exit -and $exitProof.exit -eq 0 -and $exitProof.retained_handle -and $exitProof.normal_tray_exit -and $exitProof.pid -eq $launch.pid -and $exitProof.start_utc -ceq $launch.process_started_utc) 'Normal held process exit proof mismatch'
$modules=@(Read 'evidence/smoke/loaded-modules.json')
Require ($modules.Count -eq 2 -and @($modules.name|Sort-Object -Unique).Count -eq 2) 'Loaded module set mismatch'
foreach($name in @('FluidMotion.L1.dll','fluidmotion.dll')) {
 $module=@($modules|Where-Object name -ceq $name);$member=@($build.package_members|Where-Object {$_.path.EndsWith('/'+$name,[StringComparison]::Ordinal)})
 Require ($module.Count -eq 1 -and $member.Count -eq 1 -and $module[0].sha256 -ceq $member[0].sha256 -and $module[0].path.Replace('\','/') -ceq ('C:/Program Files/EveryStage/Terminal/'+$name)) 'Actual mapped module binding mismatch'
}
$calls=@(Get-Content -LiteralPath (Safe 'evidence/smoke/installer-calls.jsonl') -Encoding UTF8|Where-Object {$_}|ForEach-Object {$_|ConvertFrom-Json})
Require ($calls.Count -eq 9 -and @($calls|Where-Object {$null -eq $_.exit -or $_.exit -ne 0}).Count -eq 0) 'Installer call proof mismatch'
$smoke=Read 'evidence/smoke/result.json'
Require ($smoke.status -like 'PASS pipeline installer acceptance*' -and (($smoke.cases.selection|Sort-Object) -join ',') -ceq 'Both,Caster,Terminal') 'Installation selection acceptance mismatch'
Require ((Sha (Safe 'baseline/baseline.zip')) -ceq '76614f31aa34372c15f0de26d69f818f89c3b5814967b24a0aa980b4c7eaad94') 'Baseline ZIP mismatch'
$baseline=[IO.Compression.ZipFile]::OpenRead((Safe 'baseline/baseline.zip'))
try {
 Require ($baseline.Entries.Count -eq 1 -and $baseline.Entries[0].FullName -ceq 'EveryStage-Setup-0.9.1-win-x64.exe') 'Baseline member identity mismatch'
 $stream=$baseline.Entries[0].Open();$algorithm=[Security.Cryptography.SHA256]::Create()
 try{$hash=[BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$stream.Dispose();$algorithm.Dispose()}
 Require ($hash -ceq 'c6219e786d34affb1b69099ed3e6b0d6af3e2e86c131643e41ea66d6a30560d9') 'Baseline inner EXE mismatch'
} finally {$baseline.Dispose()}
foreach($archive in $m.archives){Require ((Sha (Safe $archive.path)) -ceq $archive.sha256) 'Original artifact archive mismatch'}
Require (@($m.archives).Count -ge 3) 'Input/smoke/control archives required'
$control=Read 'evidence/control/result.json' 
Require ($control.status -like 'PASS isolated actual-control harness*' -and $control.drawer_regression_checks -eq 47 -and $control.presenter_checks -eq 50 -and $control.Created -eq 51 -and $control.Released -eq 51 -and $control.checks -eq 213 -and $control.original_motion_checks -eq 97 -and $control.slide_transition_checks -eq 116 -and $control.Created -eq $control.Released -and $control.Created -gt 0) 'Presentation control regression mismatch'
foreach($image in $control.screenshots) {Require ((Sha (Safe ('evidence/control/'+$image.name+'.png'))) -ceq $image.sha256) 'Control screenshot mismatch'}
$primary=Read 'evidence/POST_PRIMARY_ACCEPTANCE.json'
Require ((Sha (Safe 'evidence/POST_PRIMARY_ACCEPTANCE.json')) -ceq $m.primary_post_receipt_sha256) 'Primary post receipt mismatch'
foreach($image in $primary.images) {Require ((Sha (Safe ('evidence/smoke/'+$image.file))) -ceq $image.sha256) 'Ordinary UI image mismatch'}
Write-Output 'PASS content: full file sets/input/package/mapped modules/held normal exit/install cases/213 control evidence; no application execution'
if(!(Test-Path -LiteralPath "$root/ROOT_ACCEPTANCE.json")) {
 Write-Output 'FINAL ACCEPTANCE PENDING';if(!$AllowPending){exit 2};return
}
$final=Read 'ROOT_ACCEPTANCE.json'
Require ($final.status -ceq 'accepted internal delivery' -and $final.application_commit -ceq $m.application_commit -and $final.component_source_commit -ceq $m.component_source_commit -and $final.application_post_run -eq $m.run -and $final.application_post_conclusion -ceq 'success' -and $final.primary_review -ceq 'accepted' -and $final.manifest_sha256 -ceq (Sha (Safe 'manifest.json')) -and $final.installer_sha256 -ceq $m.installer.sha256 -and $final.package_sha256 -ceq $m.package.sha256) 'Final external acceptance incomplete'
Write-Output 'PASS FINAL internal unsigned delivery; immutable tested products unchanged'
