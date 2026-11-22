$ErrorActionPreference='Stop'
$source=Join-Path $PSScriptRoot 'InstallerUi.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
if($errors){throw 'Actual selector source did not parse'}
foreach($name in @('Test-NotificationOverflowMetadata','Test-NotificationIconMetadata','In-NotificationArea')) {
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
if(!(In-NotificationArea $fixture.overflow_ancestors)){throw 'Actual taskbar notification ancestry rejected'}
$actualArea=In-NotificationArea $fixture.notification_icon_ancestors
if(!$actualArea -or !(Test-NotificationIconMetadata $fixture.notification_icon $actualArea)){throw 'Real observed prefixed overflow icon rejected'}
$original=$fixture.notification_icon|ConvertTo-Json -Depth 8|ConvertFrom-Json
$original.name='EveryStage 终端机 - 待机中 / 开关: 开'
if(!(Test-NotificationIconMetadata $original $actualArea)){throw 'Unprefixed exact application tooltip rejected'}
$unknown=$fixture.notification_icon|ConvertTo-Json -Depth 8|ConvertFrom-Json
$unknown.name='Unknown '+$unknown.name
if(Test-NotificationIconMetadata $unknown $actualArea){throw 'Unknown prefix accepted'}
$double=$fixture.notification_icon|ConvertTo-Json -Depth 8|ConvertFrom-Json
$double.name='EveryStage 终端机 '+$double.name
if(Test-NotificationIconMetadata $double $actualArea){throw 'More than one shell product prefix accepted'}
$wrong=$fixture.notification_icon_ancestors|ConvertTo-Json -Depth 12|ConvertFrom-Json
$wrong[3].class_name='UnknownWindow'
if(In-NotificationArea $wrong){throw 'Wrong overflow ancestry accepted'}
$foreign=$fixture.notification_icon_ancestors|ConvertTo-Json -Depth 12|ConvertFrom-Json
$foreign[3].pid=$foreign[0].pid+1
if(In-NotificationArea $foreign){throw 'Foreign process overflow root accepted'}
'PASS 12 actual-selector/ancestry replay checks; no UI query or input'
