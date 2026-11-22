param([string]$Archive='artifacts/baseline.zip',[string]$MirrorWrapper='artifacts/baseline-mirror.zip',[string]$Receipt='artifacts/baseline-source.json')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$source=Get-Content $Receipt -Raw|ConvertFrom-Json -AsHashtable
if($source.route -cnotin @('original-artifact','retained-actions-artifact')){throw 'Unknown baseline retrieval route'}
if($source.route -eq 'retained-actions-artifact') {
 if(Test-Path $Archive){throw 'Unexpected existing baseline destination'}
 $wrapper=[IO.Compression.ZipFile]::OpenRead((Resolve-Path $MirrorWrapper))
 try {
  if($wrapper.Entries.Count -ne 1 -or $wrapper.Entries[0].FullName -cne 'baseline.zip'){throw 'Mirror wrapper must contain only exact baseline.zip member'}
  if($wrapper.Entries[0].Length -ne 62309585){throw 'Retained original ZIP length mismatch'}
  $source.mirror_wrapper_sha256=(Get-FileHash $MirrorWrapper).Hash.ToLowerInvariant()
  [IO.Compression.ZipFileExtensions]::ExtractToFile($wrapper.Entries[0],[IO.Path]::GetFullPath($Archive))
 } finally {$wrapper.Dispose()}
}
if((Get-FileHash $Archive).Hash.ToLowerInvariant() -ne '76614f31aa34372c15f0de26d69f818f89c3b5814967b24a0aa980b4c7eaad94'){throw 'Original baseline ZIP hash mismatch'}
$zip=[IO.Compression.ZipFile]::OpenRead((Resolve-Path $Archive))
try {
 if($zip.Entries.Count -ne 1 -or $zip.Entries[0].FullName -cne 'EveryStage-Setup-0.9.1-win-x64.exe'){throw 'Original ZIP installer member mismatch'}
 $stream=$zip.Entries[0].Open();$algorithm=[Security.Cryptography.SHA256]::Create()
 try {$hash=[BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$stream.Dispose();$algorithm.Dispose()}
 if($hash -ne 'c6219e786d34affb1b69099ed3e6b0d6af3e2e86c131643e41ea66d6a30560d9'){throw 'Original baseline installer hash mismatch'}
} finally {$zip.Dispose()}
$source.original_zip_sha256='76614f31aa34372c15f0de26d69f818f89c3b5814967b24a0aa980b4c7eaad94'
$source.original_installer_sha256=$hash
$source.original_source_run=44977550323
$source.original_source_commit='7a40a8d38d6d1d91786e17d5265037b0d4bd7ddf'
$source.validation='PASS exact immutable original ZIP and installer; no execution'
$source|ConvertTo-Json -Depth 16|Set-Content -Encoding utf8 $Receipt
