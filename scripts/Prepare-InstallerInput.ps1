$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/Delivery-Version.ps1"
$version=Get-DeliveryVersion "$repo/Directory.Build.props"
$out=Join-Path $repo 'artifacts/installer-input'
if(Test-Path $out){throw 'Installer input directory must be fresh'}
New-Item -ItemType Directory $out|Out-Null
$state=Get-Content "$repo/artifacts/fluidmotion/component-state.json" -Raw|ConvertFrom-Json
$setup=Get-Item "$repo/artifacts/installer/EveryStage-Setup-$($version.app_version)-win-x64.exe"
$package=Get-Item (Join-Path $repo $state.package_relative)
if(!$package.Exists){throw 'Exact consumed package missing'}
if((Get-FileHash $package.FullName).Hash.ToLowerInvariant() -ne $state.package_sha256){throw 'Consumed package drift'}
$frameworkResult=Join-Path $repo $state.framework_manifest_relative
if((Get-FileHash $frameworkResult).Hash.ToLowerInvariant() -ne $state.framework_manifest_sha256){throw 'Framework provenance drift'}
$framework=Get-Content $frameworkResult -Raw|ConvertFrom-Json
if($framework.source_commit -ne '44401d3f002b427d828500331a3e9b4ada5881cd' -or $framework.source_dirty -or $framework.status -notlike 'PASS*'){throw 'Framework source provenance mismatch'}

Copy-Item -LiteralPath @($setup.FullName,$package.FullName,"$repo/artifacts/fluidmotion/component-state.json","$repo/Directory.Build.props") -Destination $out
$provenance=Join-Path $out 'provenance'
New-Item -ItemType Directory $provenance|Out-Null
Copy-Item -LiteralPath (Join-Path $repo $state.framework_manifest_relative) -Destination "$provenance/framework-result.json"
Copy-Item "$repo/artifacts/fluidmotion/consumption-*.json" $provenance
$notices=Join-Path $out 'notices'
New-Item -ItemType Directory $notices|Out-Null
$inventory=@()
Add-Type -AssemblyName System.IO.Compression.FileSystem
$libraries=@{}
foreach($component in @('Terminal','Caster')) {
 $assets=Get-Content "$repo/src/$component/EveryStage.$component/obj/project.assets.json" -Raw|ConvertFrom-Json -AsHashtable
 foreach($item in $assets.libraries.GetEnumerator()){$libraries[$item.Key]=$item.Value}
}
$runtimeFrameworks=@()
foreach($component in @('Terminal','Caster')) {
 $config=Get-Content "$repo/artifacts/installer/publish/$component/EveryStage.$component.runtimeconfig.json" -Raw|ConvertFrom-Json
 Copy-Item "$repo/artifacts/installer/publish/$component/EveryStage.$component.runtimeconfig.json" "$out/$component.runtimeconfig.json"
 foreach($framework in $config.runtimeOptions.includedFrameworks) {
  $runtimeId=switch($framework.name){'Microsoft.NETCore.App' {'Microsoft.NETCore.App.Runtime.win-x64'} 'Microsoft.WindowsDesktop.App' {'Microsoft.WindowsDesktop.App.Runtime.win-x64'} default {throw "Unknown included runtime framework $($framework.name)"}}
  $path="$($runtimeId.ToLowerInvariant())/$($framework.version)"
  $present=Test-Path (Join-Path $env:NUGET_PACKAGES $path)
  $runtimeFrameworks+=@{component=$component;name=$framework.name;version=$framework.version;package=$runtimeId;package_cache_present=$present}
  if($present){$libraries["$runtimeId/$($framework.version)"]=@{type='package';path=$path}}
 }
}
$runtimeFrameworks|ConvertTo-Json -Depth 8|Set-Content -Encoding utf8 "$out/runtime-frameworks.json"
foreach($entry in $libraries.GetEnumerator()) {
 if($entry.Value.type -ne 'package'){continue}
 $id,$version=$entry.Key -split '/',2
 $folder=Join-Path $env:NUGET_PACKAGES $entry.Value.path
 $nupkg=Get-ChildItem $folder -Filter '*.nupkg'|Select-Object -First 1
 if(!$nupkg){throw "Restored package unavailable: $($entry.Key)"}
 $archive=[IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
 try {
  $nuspec=$archive.Entries|Where-Object FullName -like '*.nuspec'|Select-Object -First 1
  $reader=[IO.StreamReader]::new($nuspec.Open())
  try{[xml]$xml=$reader.ReadToEnd()}finally{$reader.Dispose()}
  $available=@()
  foreach($entryFile in $archive.Entries) {
   if($entryFile.Name -match '(?i)(license|notice|copying)' -and $entryFile.Length -gt 0) {
    $destination=Join-Path $notices "$id-$version-$($available.Count)-$($entryFile.Name)"
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entryFile,$destination)
    $available+=@{member=$entryFile.FullName;file=[IO.Path]::GetFileName($destination);sha256=(Get-FileHash $destination).Hash.ToLowerInvariant()}
   }
  }
  $inventory+=@{id=$id;version=$version;nupkg_sha256=(Get-FileHash $nupkg.FullName).Hash.ToLowerInvariant();license=$xml.package.metadata.license.InnerText;license_type=$xml.package.metadata.license.type;license_url=[string]$xml.package.metadata.licenseUrl;available_notices=$available;limitation='Package metadata and available files only, not exhaustive embedded-dependency legal proof'}
 }finally{$archive.Dispose()}
}
$runtimeNotices=@()
foreach($component in @('Terminal','Caster')) {
 $root=Join-Path $repo "artifacts/installer/publish/$component"
 foreach($file in Get-ChildItem $root -Recurse -File|Where-Object Name -match '(?i)(license|notice|copying)') {
  $name="$component-$($runtimeNotices.Count)-$($file.Name)"
  Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $notices $name)
  $runtimeNotices+=@{component=$component;published_path=[IO.Path]::GetRelativePath($root,$file.FullName);notice=$name;sha256=(Get-FileHash $file.FullName).Hash.ToLowerInvariant()}
 }
 Copy-Item "$root/EveryStage.$component.deps.json" "$out/$component.deps.json"
}
$runtimeNotices|ConvertTo-Json -Depth 12|Set-Content -Encoding utf8 "$out/shipped-runtime-notices.json"
$inventory|ConvertTo-Json -Depth 20|Set-Content -Encoding utf8 "$out/dependency-inventory.json"
$files=@(Get-ChildItem $out -File -Recurse|ForEach-Object {@{path=[IO.Path]::GetRelativePath($out,$_.FullName).Replace('\','/');sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant();bytes=$_.Length}})
$published=@{}
foreach($component in @('Terminal','Caster')) {
 $base=Join-Path $repo "artifacts/installer/publish/$component"
 $published[$component]=@(Get-ChildItem $base -File -Recurse|ForEach-Object {@{path=[IO.Path]::GetRelativePath($base,$_.FullName).Replace('\','/');sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}})
}
$receipt=@{app_version=$version.app_version;assembly_file_version=$version.assembly_file_version;version_props_sha256=$version.version_props_sha256;published=$published;source=@{checkout_commit=(& git rev-parse HEAD);event_commit=$env:GITHUB_SHA;pull_request_head=$env:PR_HEAD_SHA};installer=$setup.Name;package=$package.Name;package_members=$state.package_members;files=$files;status='build input only; not installation acceptance'}
$receipt|ConvertTo-Json -Depth 24|Set-Content -Encoding utf8 "$out/build-input.json"
