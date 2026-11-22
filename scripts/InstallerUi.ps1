param([int]$ProcessId, [ValidateSet('Exercise','Exit')][string]$Action, [string]$OutputDirectory, [switch]$ValidateOnly, [string]$ExpectedStartUtc)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing,System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class InstallerInput {
 [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h,int command);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,System.Text.StringBuilder title,int capacity);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
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
if($ExpectedStartUtc -and $start -ne $ExpectedStartUtc){throw 'Held process starttime mismatch'}
function Element-Metadata($element) {
 $r=$element.Current.BoundingRectangle
 return @{name=$element.Current.Name;pid=$element.Current.ProcessId;control_type=$element.Current.ControlType.ProgrammaticName;enabled=$element.Current.IsEnabled;offscreen=$element.Current.IsOffscreen;bounds=@{left=$r.Left;top=$r.Top;width=$r.Width;height=$r.Height}}
}
function Save-Diagnostics([string]$label) {
 try {
  $foreground=[InstallerInput]::GetForegroundWindow()
  [uint32]$foregroundPid=0
  [InstallerInput]::GetWindowThreadProcessId($foreground,[ref]$foregroundPid)|Out-Null
  $title=[Text.StringBuilder]::new(512)
  [InstallerInput]::GetWindowText($foreground,$title,$title.Capacity)|Out-Null
  $p.Refresh()
  $screen=[Windows.Forms.SystemInformation]::VirtualScreen
  $record=@{label=$label;acceptance=$false;pid=$ProcessId;start_utc=$start;exited=$p.HasExited;foreground_hwnd=$foreground.ToInt64();foreground_pid=$foregroundPid;foreground_title=$title.ToString();desktop=@{x=$screen.X;y=$screen.Y;width=$screen.Width;height=$screen.Height}}
  if(!$p.HasExited -and $p.MainWindowHandle -ne 0) {
   $own=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
   $record.owned=Element-Metadata $own
   $record.owned_hwnd=$p.MainWindowHandle.ToInt64()
   $r=$own.Current.BoundingRectangle
   if($own.Current.ProcessId -eq $ProcessId -and $r.Width -gt 0 -and $r.Height -gt 0 -and $r.Width -le 4096 -and $r.Height -le 4096) {
    $image=[Drawing.Bitmap]::new([int]$r.Width,[int]$r.Height);$graphics=[Drawing.Graphics]::FromImage($image)
    $dc=$graphics.GetHdc()
    try {$record.printwindow=[InstallerInput]::PrintWindow($p.MainWindowHandle,$dc,2)}finally{$graphics.ReleaseHdc($dc);$graphics.Dispose()}
    try {$image.Save("$OutputDirectory/diagnostic-$Action-owned-window.png")}finally{$image.Dispose()}
   }
  }
  $record|ConvertTo-Json -Depth 10|Set-Content -Encoding utf8 "$OutputDirectory/diagnostic-$Action.json"
 } catch { $_.Exception.ToString()|Set-Content -Encoding utf8 "$OutputDirectory/diagnostic-$Action-error.txt" }
}
$root=[System.Windows.Automation.AutomationElement]::RootElement
function Dismiss-OwnedFirstRunGuide([bool]$required) {
 $condition=[System.Windows.Automation.AndCondition]::new(
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$ProcessId),
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'开始使用'),
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Window))
 $dialog=$null
 for($attempt=0;$attempt -lt 30 -and !$dialog;$attempt++) {
  $dialog=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
  if(!$dialog){Start-Sleep -Milliseconds 200}
 }
 if(!$dialog) {
  if($required){throw 'Expected fresh empty-app first-run guide was not observed'}
  return
 }
 $record=@{title='开始使用';pid=$ProcessId;dialog=Element-Metadata $dialog;action='not invoked'}
 try {
  if($dialog.Current.ProcessId -ne $ProcessId -or $dialog.Current.IsOffscreen -or !$dialog.Current.IsEnabled){throw 'First-run guide identity/readiness mismatch'}
  $textCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)
  $texts=$dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,$textCondition)
  $record.observed_static_text=@($texts|ForEach-Object {$_.Current.Name})
  $expected="欢迎使用 EveryStage。文件库还是空的、也还没有活动，可以按三步开始：`n`n1. 在「文件」页点「导入」，把图片、视频、文档等素材加入文件库；`n2. 在「流程」页新建活动，把文件加入活动并排好播放顺序；`n3. 选好扩展屏后即可播放，也可以等待投屏机连接投屏。"
  $matched=$false
  foreach($text in $texts){if($text.Current.Name.Replace("`r`n","`n") -ceq $expected){$matched=$true}}
  $record.expected_welcome_text=$matched
  if(!$matched){throw 'Owned first-run dialog text differs from pinned source'}
  $buttons=$dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button))
  $record.all_buttons=@($buttons|ForEach-Object {Element-Metadata $_})
  # Native dialog title-bar chrome may expose Close; only the matched content OK is eligible.
  $okButtons=@($buttons|Where-Object {$_.Current.Name -in @('OK','确定') -and $_.Current.IsEnabled -and !$_.Current.IsOffscreen})
  if($okButtons.Count -ne 1){throw 'First-run guide does not have the single expected enabled visible OK button'}
  $record.button=Element-Metadata $okButtons[0]
  Invoke-Control $okButtons[0]
  for($attempt=0;$attempt -lt 30;$attempt++) {
   if(!$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)){break}
   Start-Sleep -Milliseconds 200
  }
  if($root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)){throw 'First-run guide remained after ordinary OK invocation'}
  $record.action='normal OK invoked; exact owned guide dismissed'
 } catch {$record.error=$_.Exception.ToString();throw} finally {
  $record|ConvertTo-Json -Depth 10|Set-Content -Encoding utf8 "$OutputDirectory/onboarding-$Action.json"
 }
}
try {
Dismiss-OwnedFirstRunGuide ($Action -eq 'Exercise')
if ($Action -eq 'Exit') {
 # Windows notification area is outside the application's UIA subtree. Select only its exact product tooltip.
 $icon=$null
 for($i=0;$i -lt 20 -and !$icon;$i++) {
  $items=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
  foreach($item in $items) { if($item.Current.Name -like 'EveryStage 终端机 -*' -and $item.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $item.Current.IsEnabled -and !$item.Current.IsOffscreen) { $icon=$item; break } }
  if(!$icon) { Start-Sleep -Milliseconds 250 }
 }
 if(!$icon) { throw 'Owned Terminal notification icon unavailable; normal exit not proven' }
 $iconRecord=Element-Metadata $icon
 $iconRecord|ConvertTo-Json -Depth 6|Set-Content -Encoding utf8 "$OutputDirectory/tray-icon.json"
 $r=$icon.Current.BoundingRectangle
 $desktop=[Windows.Forms.SystemInformation]::VirtualScreen
 if($r.Width -le 0 -or $r.Height -le 0 -or $r.Left -lt $desktop.Left -or $r.Top -lt $desktop.Top -or $r.Right -gt $desktop.Right -or $r.Bottom -gt $desktop.Bottom){throw 'Notification button coordinates out of screen'}
 [InstallerInput]::SetCursorPos([int]($r.X+$r.Width/2),[int]($r.Y+$r.Height/2))|Out-Null
 [InstallerInput]::mouse_event(8,0,0,0,[UIntPtr]::Zero)
 [InstallerInput]::mouse_event(16,0,0,0,[UIntPtr]::Zero)
 $exitCondition=[System.Windows.Automation.AndCondition]::new(
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'退出'),
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$ProcessId))
 $exitItem=$null
 $popupObservations=@()
 for($attempt=0;$attempt -lt 30 -and !$exitItem;$attempt++) {
  $candidate=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$exitCondition)
  if($candidate) {
   $popupObservations+=Element-Metadata $candidate
   if(!$candidate.Current.IsOffscreen -and $candidate.Current.IsEnabled -and $candidate.Current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem){$exitItem=$candidate}
  }
  if(!$exitItem){Start-Sleep -Milliseconds 200}
 }
 $popupObservations|ConvertTo-Json -Depth 8|Set-Content -Encoding utf8 "$OutputDirectory/tray-exit-observations.json"
 if(!$exitItem){throw 'Owned visible enabled tray Exit item missing after bounded poll'}
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
  if($candidate.Current.IsEnabled -and (Find-Name $candidate '歌单 ▴')) {$window=$candidate}
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
[InstallerInput]::ShowWindowAsync($p.MainWindowHandle,9)|Out-Null
for($attempt=0;$attempt -lt 5;$attempt++) {
 [InstallerInput]::SetForegroundWindow($p.MainWindowHandle)|Out-Null
 try {$window.SetFocus()}catch {Write-Output "Owned UIA focus request: $($_.Exception.Message)"}
 if([InstallerInput]::GetForegroundWindow() -eq $p.MainWindowHandle){break}
 Start-Sleep -Milliseconds 200
}
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

} catch {
 Save-Diagnostics $_.Exception.Message
 throw
}
