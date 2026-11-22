$ErrorActionPreference='Stop'
$scriptPath=Join-Path $PSScriptRoot 'Verify-PresentationDelivery.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($scriptPath,[ref]$tokens,[ref]$errors)
if($errors){throw 'Verifier parse failed'}
# Load only the actual pure file guard/hash functions, never the top-level acceptance driver.
foreach($name in @('Require','Sha','Safe','Read','Verify-Tree')) {
 $node=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -ceq $name)
 if($node.Count -ne 1){throw 'Expected actual verifier function missing'}
 Invoke-Expression $node[0].Extent.Text
}
$root=Join-Path $PSScriptRoot ('../artifacts/delivery-verifier-'+[Guid]::NewGuid().ToString('N'))
$root=[IO.Path]::GetFullPath($root)
New-Item -ItemType Directory $root|Out-Null
$checks=0
try {
 New-Item -ItemType Directory "$root/nested"|Out-Null
 [IO.File]::WriteAllText("$root/nested/item.txt",'actual bytes')
 [IO.File]::WriteAllText("$root/nested/modules.json",'[{"name":"FluidMotion.L1.dll"},{"name":"fluidmotion.dll"}]')
 $modules=@(Read 'nested/modules.json')
 if($modules.Count -ne 2 -or $modules[0].name -cne 'FluidMotion.L1.dll' -or $modules[1].name -cne 'fluidmotion.dll'){throw 'Actual Read wrapped the top-level JSON array'}
 $checks++
 [IO.File]::WriteAllText("$root/nested/object.json", '{"status":"object preserved","nested":[1,2]}')
 $object=Read 'nested/object.json'
 if($object.status -cne 'object preserved' -or $object.nested.Count -ne 2 -or $object.nested[0] -ne 1 -or $object.nested[1] -ne 2){throw 'Actual Read changed a JSON object'}
 $checks++
 Remove-Item -LiteralPath "$root/nested/modules.json","$root/nested/object.json"
 $hash=Sha "$root/nested/item.txt"
 [IO.File]::WriteAllText("$root/nested/SHA256SUMS","$hash  item.txt`n")
 $nestedHash=Sha "$root/nested/SHA256SUMS"
 [IO.File]::WriteAllText("$root/SHA256SUMS","$nestedHash  nested/SHA256SUMS`n$hash  nested/item.txt`n")
 Verify-Tree '.';$checks++
 foreach($relative in @('../escape', 'nested/../../escape', 'C:/escape', 'nested/item.txt:stream')) {
  $rejected=$false;try{$null=Safe $relative}catch{$rejected=$true}
  if(!$rejected){throw "Unsafe path accepted: $relative"};$checks++
 }
 [IO.File]::WriteAllText("$root/nested/extra.txt",'unlisted')
 $rejected=$false;try{Verify-Tree '.'}catch{$rejected=$true}
 if(!$rejected){throw 'Extra file accepted'};$checks++
 Remove-Item -LiteralPath "$root/nested/extra.txt"
 [IO.File]::WriteAllText("$root/nested/hidden.txt",'hidden extra')
 [IO.File]::SetAttributes("$root/nested/hidden.txt",[IO.FileAttributes]::Hidden)
 $rejected=$false;try{Verify-Tree '.'}catch{$rejected=$true}
 if(!$rejected){throw 'Hidden extra file accepted'};$checks++
 Remove-Item -LiteralPath "$root/nested/hidden.txt" -Force
 [IO.File]::WriteAllText("$root/nested/item.txt",'drift')
 $rejected=$false;try{Verify-Tree '.'}catch{$rejected=$true}
 if(!$rejected){throw 'Byte drift accepted'};$checks++
 Write-Output "PASS $checks actual verifier file/tree/path guards; no product execution"
} finally {
 $allowed=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts')).TrimEnd('\')+'\'
 if(!$root.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($root) -cnotmatch '^delivery-verifier-[0-9a-f]{32}$'){throw 'Cleanup prefix refused'}
 Remove-Item -LiteralPath $root -Recurse
}
