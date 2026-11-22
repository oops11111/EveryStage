$ErrorActionPreference='Stop'
$source=Join-Path $PSScriptRoot 'InstallerUi.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
if($errors){throw 'Actual selector source did not parse'}
foreach($name in @('Test-NotificationOverflowMetadata','Test-NotificationIconMetadata')) {
 $function=$ast.Find({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
 if(!$function){throw "Actual selector missing: $name"}
 . ([ScriptBlock]::Create($function.Extent.Text))
}
$fixture=Get-Content (Join-Path $PSScriptRoot '../docs/fluidmotion/P3_SELECTOR_FIXTURE.json') -Raw -Encoding UTF8|ConvertFrom-Json
if(!(Test-NotificationOverflowMetadata $fixture.overflow $fixture.notification_ancestry)){throw 'Real observed Show Hidden Icons rejected'}
if(Test-NotificationOverflowMetadata $fixture.overflow $false){throw 'Overflow outside notification ancestry accepted'}
if(Test-NotificationOverflowMetadata $fixture.wrong_taskbar $true){throw 'Wrong taskbar button accepted as overflow'}
if(Test-NotificationIconMetadata $fixture.wrong_taskbar $true){throw 'Wrong taskbar button accepted as notification icon'}
$variant=$fixture.overflow|ConvertTo-Json -Depth 8|ConvertFrom-Json
$variant.name='Show hidden icons'
if(!(Test-NotificationOverflowMetadata $variant $true)){throw 'Allowed case variant rejected'}
'PASS 5 actual-selector replay checks; no UI query or input'
