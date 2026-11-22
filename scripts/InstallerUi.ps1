param([int]$ProcessId, [ValidateSet('Exercise','Exit')][string]$Action, [string]$OutputDirectory, [switch]$ValidateOnly)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing,System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class InstallerInput {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int height,bool repaint);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
}
"@
function Find-Name($root,$name) {
 $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name)
 return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Invoke-Control($control) {
 if (!$control) { throw 'Expected UI control missing' }
 $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
Add-Type -Path "$PSScriptRoot/InstallerMappings.cs"
if($ValidateOnly){Write-Output 'PASS UIA/WinForms/native metadata compile; no process/UI/input operations';return}
if([InstallerInput]::SetThreadDpiAwarenessContext([IntPtr]::new(-4)) -eq [IntPtr]::Zero){throw 'Process-local thread DPI context unavailable'}
$p=Get-Process -Id $ProcessId
if ($p.Path -ne "$env:ProgramFiles\EveryStage\Terminal\EveryStage.Terminal.exe") { throw 'Unexpected Terminal path' }
$start=$p.StartTime.ToUniversalTime().ToString("o")
$root=[System.Windows.Automation.AutomationElement]::RootElement
if ($Action -eq 'Exit') {
 # Windows notification area is outside the application's UIA subtree. Select only its exact product tooltip.
 $icon=$null
 for($i=0;$i -lt 20 -and !$icon;$i++) {
  $items=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
  foreach($item in $items) { if($item.Current.Name -like 'EveryStage 终端机 -*' -and !$item.Current.IsOffscreen) { $icon=$item; break } }
  if(!$icon) { Start-Sleep -Milliseconds 250 }
 }
 if(!$icon) { throw 'Owned Terminal notification icon unavailable; normal exit not proven' }
 $r=$icon.Current.BoundingRectangle
 [InstallerInput]::SetCursorPos([int]($r.X+$r.Width/2),[int]($r.Y+$r.Height/2))|Out-Null
 [InstallerInput]::mouse_event(8,0,0,0,[UIntPtr]::Zero)
 [InstallerInput]::mouse_event(16,0,0,0,[UIntPtr]::Zero)
 Start-Sleep -Milliseconds 300
 $exitCondition=[System.Windows.Automation.AndCondition]::new(
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'退出'),
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$ProcessId))
 $exitItem=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$exitCondition)
 if(!$exitItem -or $exitItem.Current.IsOffscreen -or !$exitItem.Current.IsEnabled){throw 'Owned visible enabled tray Exit item missing'}
 $p.Refresh()
 if($p.StartTime.ToUniversalTime().ToString('o') -ne $start){throw 'PID identity changed'}
 Invoke-Control $exitItem
 if(!$p.WaitForExit(30000)) { throw 'Tray Exit did not terminate held PID; installer must not proceed' }
 if($p.ExitCode -ne 0){throw "Normal tray Exit returned $($p.ExitCode)"}
 @{normal_tray_exit=$true;pid=$ProcessId;exit=$p.ExitCode}|ConvertTo-Json|Set-Content -Encoding utf8 "$OutputDirectory/normal-exit.json"
 return
}
$window=$null
for($i=0;$i -lt 60 -and !$window;$i++) {
 $p.Refresh()
 if($p.HasExited){throw 'Terminal exited before main window'}
 if($p.MainWindowHandle -ne 0) {
  $candidate=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
  if(Find-Name $candidate '歌单 ▴') {$window=$candidate}
 }
 if(!$window){Start-Sleep -Milliseconds 500}
}
if(!$window){throw 'Ordinary installed main window not ready'}
if($window.Current.ProcessId -ne $ProcessId){throw 'UIA window PID mismatch'}
$area=[System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
if($area.Width -lt 980 -or $area.Height -lt 640){throw 'Interactive desktop too small for real application MinimumSize'}
$width=[Math]::Min(1200,$area.Width-20)
$height=[Math]::Min(800,$area.Height-20)
[InstallerInput]::MoveWindow($p.MainWindowHandle,$area.X+10,$area.Y+10,$width,$height,$true)|Out-Null
[InstallerInput]::SetForegroundWindow($p.MainWindowHandle)|Out-Null
Start-Sleep -Milliseconds 300
@{working_area=@{x=$area.X;y=$area.Y;width=$area.Width;height=$area.Height};initial=@{width=$width;height=$height};pid=$ProcessId;start_utc=$start}|ConvertTo-Json -Depth 6|Set-Content -Encoding utf8 "$OutputDirectory/ordinary-window.json"
function Shot($name) {
 $r=$window.Current.BoundingRectangle
 if([InstallerInput]::GetForegroundWindow() -ne $p.MainWindowHandle -or $window.Current.IsOffscreen){throw 'Owned window is not foreground and on-screen'}
 $screen=[System.Windows.Forms.SystemInformation]::VirtualScreen
 if($r.Left -lt $screen.Left -or $r.Top -lt $screen.Top -or $r.Right -gt $screen.Right -or $r.Bottom -gt $screen.Bottom){throw 'Screenshot would include unavailable screen area'}
 if($r.Width -lt 100 -or $r.Height -lt 100){throw 'Invalid owned window geometry'}
 $image=[System.Drawing.Bitmap]::new([int]$r.Width,[int]$r.Height)
 $g=[System.Drawing.Graphics]::FromImage($image)
 try {$g.CopyFromScreen([int]$r.X,[int]$r.Y,0,0,$image.Size);$image.Save("$OutputDirectory/$name.png")}finally{$g.Dispose();$image.Dispose()}
}
Shot 'installed-closed'
Invoke-Control (Find-Name $window '歌单 ▴')
Start-Sleep -Milliseconds 40
Shot 'installed-opening'
# This is a sampled ordinary UI action, not proof of in-flight reversal timing.
Invoke-Control (Find-Name $window '歌单 ▾')
Start-Sleep -Milliseconds 600
Shot 'installed-reversed'
Invoke-Control (Find-Name $window '歌单 ▴')
Start-Sleep -Milliseconds 600
Shot 'installed-open'
$secondWidth=[Math]::Max(960,$width-100)
$secondHeight=[Math]::Max(600,$height-80)
if($secondWidth -eq $width -and $secondHeight -eq $height){throw 'No distinct in-bounds resize possible'}
[InstallerInput]::MoveWindow($p.MainWindowHandle,$area.X+10,$area.Y+10,$secondWidth,$secondHeight,$true)|Out-Null
Start-Sleep -Milliseconds 300
Shot 'installed-resized'
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Milliseconds 600
if(!(Find-Name $window '歌单 ▴')){throw 'Ordinary Escape did not close drawer'}
Shot 'installed-escape'
$modules=@([InstallerMappings]::Read($ProcessId)|ForEach-Object {@{name=[IO.Path]::GetFileName($_);path=$_;sha256=(Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant()}})
foreach($name in @('FluidMotion.L1.dll','fluidmotion.dll')) {
 if(@($modules|Where-Object name -ceq $name).Count -ne 1){throw "Actual mapped component missing: $name"}
}
$modules|ConvertTo-Json -Depth 6|Set-Content -Encoding utf8 "$OutputDirectory/loaded-modules.json"
