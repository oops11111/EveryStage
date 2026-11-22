param([Parameter(Mandatory=$true)][string]$LiteralPath)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$native=(Get-Item -LiteralPath $LiteralPath).FullName.Replace('/','\')
$shell=New-Object -ComObject WScript.Shell
try {
 $link=$shell.CreateShortcut($native)
 @{apartment=[Threading.Thread]::CurrentThread.ApartmentState.ToString();shell=$PSVersionTable.PSVersion.ToString();path=$native;target=$link.TargetPath;arguments=$link.Arguments;working_directory=$link.WorkingDirectory;icon=$link.IconLocation}|ConvertTo-Json -Compress
} finally {[Runtime.InteropServices.Marshal]::ReleaseComObject($shell)|Out-Null}
