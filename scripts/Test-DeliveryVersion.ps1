$ErrorActionPreference='Stop'
. "$PSScriptRoot/Delivery-Version.ps1"
$root=Join-Path ([IO.Path]::GetTempPath()) ('EveryStage-version-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root|Out-Null
$checks=0
try {
 $props=Join-Path $root 'Directory.Build.props'
 '<Project><PropertyGroup><EveryStageVersion>0.9.3</EveryStageVersion></PropertyGroup></Project>'|Set-Content -LiteralPath $props -Encoding UTF8
 $v=Get-DeliveryVersion $props
 if($v.app_version -cne '0.9.3' -or $v.assembly_file_version -cne '0.9.3.0'){throw 'Actual props reader failed'}
 $checks++
 $receipt=@{app_version=$v.app_version;assembly_file_version=$v.assembly_file_version;version_props_sha256=$v.version_props_sha256;installer='EveryStage-Setup-0.9.3-win-x64.exe';source=@{checkout_commit=('a'*40)}}
 $null=Assert-DeliveryVersion $receipt $props;$checks++
 foreach($key in @('app_version','assembly_file_version','version_props_sha256','installer')) {
  $bad=$receipt.Clone();$bad[$key]='wrong';$rejected=$false
  try{$null=Assert-DeliveryVersion $bad $props}catch{$rejected=$true}
  if(!$rejected){throw "Mismatch accepted: $key"};$checks++
 }
 foreach($value in @('', '0.9', '0.9.3-preview', '01.2.3', '0.9.3</EveryStageVersion><EveryStageVersion>0.9.3')) {
  "<Project><PropertyGroup><EveryStageVersion>$value</EveryStageVersion></PropertyGroup></Project>"|Set-Content -LiteralPath $props -Encoding UTF8
  $rejected=$false;try{$null=Get-DeliveryVersion $props}catch{$rejected=$true}
  if(!$rejected){throw "Invalid props accepted: $value"};$checks++
 }
 Remove-Item -LiteralPath $props
 $rejected=$false;try{$null=Get-DeliveryVersion $props}catch{$rejected=$true}
 if(!$rejected){throw 'Missing props accepted'};$checks++
 Write-Output "PASS $checks actual shared-reader version guards; no installer/application execution"
} finally {
 # Only the exact owned temporary directory created above is removed.
 $resolved=[IO.Path]::GetFullPath($root)
 $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(!$resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -cnotmatch '^EveryStage-version-[0-9a-f]{32}$'){throw 'Owned temp cleanup path guard failed'}
 Remove-Item -LiteralPath $resolved -Recurse
}
