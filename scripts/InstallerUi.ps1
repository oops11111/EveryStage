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
function Get-ExistingFileSha256([string]$path) {
 $stream=[IO.File]::OpenRead($path)
 $algorithm=[Security.Cryptography.SHA256]::Create()
 try {return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}
 finally {$stream.Dispose();$algorithm.Dispose()}
}
function Test-NotificationOverflowMetadata($metadata,[bool]$notificationAncestry) {
 return $notificationAncestry -and [string]::Equals($metadata.name,'Show Hidden Icons',[StringComparison]::OrdinalIgnoreCase) -and $metadata.class_name -ceq 'SystemTray.NormalButton' -and $metadata.automation_id -ceq 'SystemTrayIcon' -and $metadata.control_type -ceq 'ControlType.Button' -and $metadata.enabled -and !$metadata.offscreen
}
function Test-NotificationIconMetadata($metadata,[bool]$notificationAncestry) {
 return $notificationAncestry -and $metadata.name -cmatch '^(?:EveryStage 终端机 )?EveryStage 终端机 - (待机中|扩展屏输出中) / 开关: (开|关)$' -and $metadata.class_name -ceq 'SystemTray.NormalButton' -and $metadata.automation_id -ceq 'NotifyItemIcon' -and $metadata.control_type -ceq 'ControlType.Button' -and $metadata.enabled -and !$metadata.offscreen
}
function In-NotificationArea($ancestors) {
 if(!$ancestors.Count){return $false}
 $shellPid=$ancestors[0].pid
 $sameProcess=@()
 foreach($ancestor in $ancestors) {
  if($ancestor.pid -ne $shellPid){break}
  $sameProcess+=,$ancestor
 }
 $overflow=@($sameProcess|Where-Object {$_.class_name -ceq 'TopLevelWindowForOverflowXamlIsland' -and $_.name -ceq 'System tray overflow window.' -and $_.native_hwnd -ne 0 -and $_.enabled -and !$_.offscreen})
 if($overflow.Count -eq 1) {
  $rootBounds=$overflow[0].bounds;$itemBounds=$ancestors[0].bounds
  return $rootBounds.width -gt 0 -and $rootBounds.height -gt 0 -and $itemBounds.width -gt 0 -and $itemBounds.height -gt 0 -and $itemBounds.left -ge $rootBounds.left -and $itemBounds.top -ge $rootBounds.top -and ($itemBounds.left+$itemBounds.width) -le ($rootBounds.left+$rootBounds.width) -and ($itemBounds.top+$itemBounds.height) -le ($rootBounds.top+$rootBounds.height)
 }
 $shellRoots=@($sameProcess|Where-Object {$_.class_name -in @('Shell_TrayWnd','Shell_SecondaryTrayWnd','TrayNotifyWnd') -and $_.native_hwnd -ne 0})
 $areas=@($sameProcess|Where-Object {$_.class_name -in @('TrayNotifyWnd','SysPager','SystemTray.NormalIconView','SystemTray.OverflowIconView') -or $_.automation_id -match '^(SystemTray|NotificationArea)' -or $_.name -in @('Notification area','User Promoted Notification Area','System tray','System Tray')})
 return $shellRoots.Count -gt 0 -and $areas.Count -gt 0
}
function Find-Name($root,$name) {
 $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name)
 return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Invoke-Control($control) {
 if (!$control) { throw 'Expected UI control missing' }
 $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
Add-Type -Path "$PSScriptRoot/InstallerMappings.cs"
Add-Type -Path "$PSScriptRoot/InstallerDialogMetadata.cs"
if($ValidateOnly){Write-Output 'PASS UIA/WinForms/native metadata compile; no process/UI/input operations';return}
if([InstallerInput]::SetThreadDpiAwarenessContext([IntPtr]::new(-4)) -eq [IntPtr]::Zero){throw 'Process-local thread DPI context unavailable'}
$p=Get-Process -Id $ProcessId
if ($p.Path -ne "$env:ProgramFiles\EveryStage\Terminal\EveryStage.Terminal.exe") { throw 'Unexpected Terminal path' }
$start=$p.StartTime.ToUniversalTime().ToString("o")
if($ExpectedStartUtc -and $start -ne $ExpectedStartUtc){throw 'Held process starttime mismatch'}
function Element-Metadata($element) {
 $r=$element.Current.BoundingRectangle
 return @{automation_id=$element.Current.AutomationId;class_name=$element.Current.ClassName;native_hwnd=$element.Current.NativeWindowHandle;name=$element.Current.Name;pid=$element.Current.ProcessId;control_type=$element.Current.ControlType.ProgrammaticName;enabled=$element.Current.IsEnabled;offscreen=$element.Current.IsOffscreen;bounds=@{left=$r.Left;top=$r.Top;width=$r.Width;height=$r.Height}}
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
  $descendants=$dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
  $record.uia_descendants=@($descendants|ForEach-Object {Element-Metadata $_})
  $expected="欢迎使用 EveryStage。文件库还是空的、也还没有活动，可以按三步开始：`n`n1. 在「文件」页点「导入」，把图片、视频、文档等素材加入文件库；`n2. 在「流程」页新建活动，把文件加入活动并排好播放顺序；`n3. 选好扩展屏后即可播放，也可以等待投屏机连接投屏。"
  $matched=$false
  foreach($element in $descendants){if($element.Current.Name.Replace("`r`n","`n") -ceq $expected){$matched=$true}}
  $nativeChildren=@()
  if(!$matched) {
   $nativeChildren=@([InstallerDialogMetadata]::Read($ProcessId,[long]$dialog.Current.NativeWindowHandle))
   $record.native_children=$nativeChildren
   foreach($child in $nativeChildren){if($child.ClassName -ceq 'Static' -and $null -ne $child.Text -and $child.Text.Replace("`r`n","`n") -ceq $expected){$matched=$true}}
   $record.body_source='owned native Static WM_GETTEXT after UIA omitted exact body'
  } else {$record.body_source='owned UIA descendant name'}
  $record.expected_welcome_text=$matched
  if(!$matched){throw 'Expected full source welcome text not verified through UIA or owned native Static metadata'}
  if(!$nativeChildren.Count){$nativeChildren=@([InstallerDialogMetadata]::Read($ProcessId,[long]$dialog.Current.NativeWindowHandle));$record.native_children=$nativeChildren}
  $nativeOk=@($nativeChildren|Where-Object {$_.ClassName -ceq 'Button' -and $_.Visible -and $_.Enabled -and $_.Text -in @('OK','确定')})
  if($nativeOk.Count -ne 1){throw 'First-run guide does not expose exactly one eligible owned native OK button'}
  $nativeButton=$nativeOk[0]
  $button=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]::new($nativeButton.Hwnd))
  if($button.Current.ProcessId -ne $ProcessId -or !$button.Current.IsEnabled -or $button.Current.IsOffscreen){throw 'Owned native OK UIA element readiness mismatch'}
  $record.button=Element-Metadata $button
  $record.native_button=$nativeButton
  $p.Refresh()
  if($p.StartTime.ToUniversalTime().ToString('o') -ne $start){throw 'Held process identity changed before ordinary OK'}
  [InstallerDialogMetadata]::ValidateNormalButton($ProcessId,[long]$dialog.Current.NativeWindowHandle,$nativeButton.Hwnd,$expected)
  $invokePattern=$null
  $record.uia_invoke_available=$button.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$invokePattern)
  if($record.uia_invoke_available) {
   $invokePattern.Invoke()
   $record.button_source='normal UIA Invoke on verified owned native OK'
  } else {
   [InstallerInput]::SetForegroundWindow([IntPtr]::new($dialog.Current.NativeWindowHandle))|Out-Null
   if([InstallerInput]::GetForegroundWindow() -ne [IntPtr]::new($dialog.Current.NativeWindowHandle)){throw 'Owned welcome guide is not active for normal button click'}
   [InstallerDialogMetadata]::ClickNormalButton($ProcessId,[long]$dialog.Current.NativeWindowHandle,$nativeButton.Hwnd,$expected)
   $record.button_source='standard bounded BM_CLICK on verified active owned native OK; UIA Invoke unavailable'
  }
  for($attempt=0;$attempt -lt 30;$attempt++) {
   if(!$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)){break}
   Start-Sleep -Milliseconds 200
  }
  if($root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)){throw 'First-run guide remained after ordinary OK invocation'}
  $p.Refresh()
  if($p.HasExited){throw 'Terminal exited while dismissing ordinary onboarding'}
  $record.action='normal OK invoked; exact owned guide dismissed; held Terminal remains alive'
 } catch {$record.error=$_.Exception.ToString();throw} finally {
  $record|ConvertTo-Json -Depth 10|Set-Content -Encoding utf8 "$OutputDirectory/onboarding-$Action.json"
 }
}
try {
Dismiss-OwnedFirstRunGuide ($Action -eq 'Exercise')
if ($Action -eq 'Exit') {
 # Match only the exact dynamic NotifyIcon tooltip from TrayIconController, never a running-app taskbar button.
 function Notification-Ancestry($element) {
  $ancestors=@();$current=$element
  for($depth=0;$depth -lt 16 -and $current;$depth++) {
   $ancestors+=Element-Metadata $current
   $current=[System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($current)
  }
  return $ancestors
 }
 $taskbars=$root.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty,'Shell_TrayWnd'))
 $taskbarMetadata=@()
 foreach($taskbar in $taskbars) {
  $children=$taskbar.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
  $entries=@();$count=0
  foreach($child in $children){if($count++ -ge 512){break};$entries+=Element-Metadata $child}
  $taskbarMetadata+=@{root=Element-Metadata $taskbar;descendants=$entries;truncated=$children.Count -gt 512}
 }
 $taskbarMetadata|ConvertTo-Json -Depth 12|Set-Content -Encoding utf8 "$OutputDirectory/taskbar-notification-metadata-before-input.json"
 $observations=@();$icon=$null;$overflowOpened=$false
 for($attempt=0;$attempt -lt 30 -and !$icon;$attempt++) {
  $items=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
  $eligibleIcons=@();$eligibleOverflow=@()
  foreach($item in $items) {
   if($item.Current.Name -like 'EveryStage 终端机*' -or $item.Current.AutomationId -match '^(SystemTray|NotificationArea)' -or $item.Current.Name -eq 'Show hidden icons') {
    $ancestry=@(Notification-Ancestry $item)
    $inArea=In-NotificationArea $ancestry
    $observations+=@{attempt=$attempt;element=Element-Metadata $item;notification_ancestry=$inArea;ancestors=$ancestry}
    if(Test-NotificationIconMetadata (Element-Metadata $item) $inArea){$eligibleIcons+=,$item}
    if(Test-NotificationOverflowMetadata (Element-Metadata $item) $inArea){$eligibleOverflow+=,$item}
   }
  }
  # Persist notification-area observations before any interaction, including an absent-icon outcome.
  $observations|ConvertTo-Json -Depth 16|Set-Content -Encoding utf8 "$OutputDirectory/notification-area-observations.json"
  if($eligibleIcons.Count -gt 1 -or $eligibleOverflow.Count -gt 1){throw 'Ambiguous eligible notification icons or overflow controls; no input permitted'}
  if($eligibleIcons.Count -eq 1){$icon=$eligibleIcons[0]}
  if(!$icon -and $eligibleOverflow.Count -eq 1 -and !$overflowOpened) {
   $shellProcess=Get-Process -Id $eligibleOverflow[0].Current.ProcessId
   if($shellProcess.Path -ne "$env:WINDIR\explorer.exe"){throw 'Notification overflow is not owned by the expected Explorer shell'}
   Invoke-Control $eligibleOverflow[0]
   $overflowOpened=$true
  }
  if(!$icon){Start-Sleep -Milliseconds 200}
 }
 if(!$icon){throw 'Exact source NotifyIcon tooltip in notification-area ancestry unavailable; no taskbar click permitted'}
 $shellProcess=Get-Process -Id $icon.Current.ProcessId
 if($shellProcess.Path -ne "$env:WINDIR\explorer.exe"){throw 'Notification icon is not owned by the expected Explorer shell'}
 $iconRecord=Element-Metadata $icon
 $iconRecord.notification_ancestors=@(Notification-Ancestry $icon)
 $iconRecord|ConvertTo-Json -Depth 12|Set-Content -Encoding utf8 "$OutputDirectory/tray-icon.json"
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
$modules=@([InstallerMappings]::Read($ProcessId)|ForEach-Object {@{name=[IO.Path]::GetFileName($_);path=$_;sha256=(Get-ExistingFileSha256 $_)}})
foreach($name in @('FluidMotion.L1.dll','fluidmotion.dll')) {
 if(@($modules|Where-Object name -ceq $name).Count -ne 1){throw "Actual mapped component missing: $name"}
}
$modules|ConvertTo-Json -Depth 6|Set-Content -Encoding utf8 "$OutputDirectory/loaded-modules.json"

} catch {
 Save-Diagnostics $_.Exception.Message
 throw
}
