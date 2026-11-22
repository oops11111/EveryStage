function Get-DeliveryVersion([string]$PropsPath) {
 $document=[xml](Get-Content -LiteralPath $PropsPath -Raw -Encoding UTF8)
 $values=@($document.SelectNodes('/Project/PropertyGroup/EveryStageVersion'))
 if($values.Count -ne 1 -or $values[0].InnerText -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'){throw 'One numeric EveryStageVersion is required'}
 $version=$values[0].InnerText
 return @{app_version=$version;assembly_file_version="$version.0";version_props_sha256=(Get-FileHash -LiteralPath $PropsPath -Algorithm SHA256).Hash.ToLowerInvariant()}
}
function Assert-DeliveryVersion($Receipt,[string]$PropsPath) {
 $actual=Get-DeliveryVersion $PropsPath
 foreach($key in @('app_version','assembly_file_version','version_props_sha256')) {
  if($Receipt.$key -cne $actual[$key]){throw "Immutable input version mismatch: $key"}
 }
 if($Receipt.installer -cne "EveryStage-Setup-$($actual.app_version)-win-x64.exe"){throw 'Versioned installer filename mismatch'}
 if($Receipt.source.checkout_commit -cnotmatch '^[0-9a-f]{40}$'){throw 'Input checkout source is required'}
 return $actual
}
