param([string]$InputDirectory,[string]$EvidenceDirectory,[string]$OutputPath)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/Delivery-Version.ps1"
$result=Get-Content "$EvidenceDirectory/result.json" -Raw|ConvertFrom-Json
if($result.status -notlike 'PASS pipeline installer acceptance*'){throw 'Smoke acceptance did not pass'}
$input=Get-Content "$InputDirectory/build-input.json" -Raw|ConvertFrom-Json
foreach($file in $input.files){if((Get-FileHash (Join-Path $InputDirectory $file.path)).Hash.ToLowerInvariant() -ne $file.sha256){throw 'Delivery bytes drift'}}
$version=Assert-DeliveryVersion $input "$InputDirectory/Directory.Build.props"
$setup=Join-Path $InputDirectory $input.installer
$manifest=@{
 status='pipeline-tested; primary review and postmerge acceptance pending'
 app_version=$version.app_version
 assembly_file_version=$version.assembly_file_version
 version_props_sha256=$version.version_props_sha256
 source=$input.source
 files=$input.files
 installer_authenticode=(Get-AuthenticodeSignature $setup).Status.ToString()
 smoke_result_sha256=(Get-FileHash "$EvidenceDirectory/result.json").Hash.ToLowerInvariant()
 package_state='embedded candidate metadata records build-time status; external acceptance binds unchanged bytes'
 scope='internal unsigned delivery only; no public publish'
 notices='Available package notices and metadata inventory provided; absent embedded upstream notices remain disclosed'
}
$manifest|ConvertTo-Json -Depth 20|Set-Content -Encoding utf8 $OutputPath
