param([string]$InputDirectory,[string]$EvidenceDirectory,[string]$OutputPath)
$ErrorActionPreference='Stop'
$result=Get-Content "$EvidenceDirectory/result.json" -Raw|ConvertFrom-Json
if($result.status -notlike 'PASS pipeline installer acceptance*'){throw 'Smoke acceptance did not pass'}
$input=Get-Content "$InputDirectory/build-input.json" -Raw|ConvertFrom-Json
foreach($file in $input.files){if((Get-FileHash (Join-Path $InputDirectory $file.path)).Hash.ToLowerInvariant() -ne $file.sha256){throw 'Delivery bytes drift'}}
$setup=Join-Path $InputDirectory $input.installer
$manifest=@{
 status='pipeline-tested; primary review and postmerge acceptance pending'
 source=$input.source
 files=$input.files
 installer_authenticode=(Get-AuthenticodeSignature $setup).Status.ToString()
 smoke_result_sha256=(Get-FileHash "$EvidenceDirectory/result.json").Hash.ToLowerInvariant()
 package_state='embedded candidate metadata records build-time status; external acceptance binds unchanged bytes'
 scope='internal unsigned delivery only; no public publish'
 notices='Available package notices and metadata inventory provided; absent embedded upstream notices remain disclosed'
}
$manifest|ConvertTo-Json -Depth 20|Set-Content -Encoding utf8 $OutputPath
