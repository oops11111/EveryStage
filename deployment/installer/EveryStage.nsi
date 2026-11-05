; EveryStage Windows 安装程序（NSIS 3, Unicode）。
; 构建：makensis -DPUBLISH_DIR=<publish 根目录，含 Terminal/ 与 Caster/> -DVERSION=<x.y.z> -DBUILD=<提交号> -DOUTFILE=<输出 exe> EveryStage.nsi
; 发布目录由 scripts/Build-Installer.sh（或 dotnet publish --self-contained）生成：自带 .NET 运行时，目标机无需另装 .NET 8。
;
; 与 deployment/Install-EveryStage.ps1 保持一致：安装到 %ProgramFiles%\EveryStage\Terminal|Caster，开机启动写
; HKLM Run 的 EveryStageTerminal / EveryStageCaster，卸载默认保留 %ProgramData%\EveryStage（可勾选删除配置与配对，日志始终保留）。
; 因此可以直接覆盖升级用脚本部署的机器。

Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!ifndef PUBLISH_DIR
  !error "Pass -DPUBLISH_DIR=<folder containing Terminal\ and Caster\>"
!endif
!ifndef VERSION
  !error "Pass -DVERSION=<x.y.z> (scripts/Build-Installer.* read it from Directory.Build.props)"
!endif
!ifndef BUILD
  !define BUILD "dev"
!endif
!ifndef OUTFILE
  !define OUTFILE "EveryStage-Setup-${VERSION}-win-x64.exe"
!endif

!define PRODUCT "EveryStage"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\EveryStage"
!define STATE_KEY "Software\EveryStage\Setup"
!define RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"
!define ICON "${PUBLISH_DIR}\Terminal\Assets\EveryStage.ico"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "Sections.nsh"
!include "WinVer.nsh"
!include "nsDialogs.nsh"
!include "FileFunc.nsh"

Name "${PRODUCT}"
Caption "${PRODUCT} ${VERSION} 安装"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\EveryStage"
RequestExecutionLevel admin
BrandingText "${PRODUCT} ${VERSION} (${BUILD})"

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "${PRODUCT}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${VERSION} (${BUILD})"
VIAddVersionKey /LANG=2052 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "FileDescription" "${PRODUCT} 安装程序"
VIAddVersionKey /LANG=2052 "LegalCopyright" "${PRODUCT}"

!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_SMALLDESC
; An installer built without a code-signing certificate (-DUNSIGNED=1, every build today) says so up
; front: Windows SmartScreen shows 「Windows 已保护你的电脑 / 未知发布者」 for it.
!ifdef UNSIGNED
  !define SIGNATURE_NOTE "$\r$\n$\r$\n注意：本安装包未经代码签名。Windows SmartScreen 可能提示“已保护你的电脑 / 未知发布者”；请只运行从可信渠道取得的安装包，确认来源后点“更多信息 → 仍要运行”。"
!else
  !define SIGNATURE_NOTE ""
!endif
!define MUI_WELCOMEPAGE_TEXT "将在本机安装 ${PRODUCT} ${VERSION}。$\r$\n$\r$\n• 终端机：连接扩展屏（LED/投影）的展示电脑。$\r$\n• 投屏机：需要把屏幕投到终端机的电脑。$\r$\n$\r$\n安装包已包含 .NET 运行环境，无需另行安装。安装前会关闭正在运行的 ${PRODUCT}。${SIGNATURE_NOTE}"
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "立即启动"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchInstalled

!insertmacro MUI_PAGE_WELCOME
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE CheckComponents
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
UninstPage custom un.DataPageCreate un.DataPageLeave
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"

; ── Helpers ──────────────────────────────────────────────────────────────────────────────────

; Close a running EveryStage executable, after asking (Cancel aborts the install).
!macro CloseRunning IMAGE DISPLAY
  nsExec::ExecToStack 'tasklist /FI "IMAGENAME eq ${IMAGE}" /NH'
  Pop $R0
  Pop $R1
  ${If} $R1 != ""
    Push $R1
    Push "${IMAGE}"
    Call StrContains
    Pop $R3
    ${If} $R3 != ""
      MessageBox MB_OKCANCEL|MB_ICONINFORMATION "${DISPLAY}正在运行，继续将关闭它。$\r$\n（终端机的方案和文件库已实时保存，不会丢失。）" IDOK +2
        Abort
      nsExec::Exec 'taskkill /IM "${IMAGE}"'
      Sleep 1500
      nsExec::Exec 'taskkill /F /IM "${IMAGE}"'
      Sleep 500
    ${EndIf}
  ${EndIf}
!macroend

!macro UnCloseRunning IMAGE
  nsExec::Exec 'taskkill /IM "${IMAGE}"'
  Sleep 1500
  nsExec::Exec 'taskkill /F /IM "${IMAGE}"'
  Sleep 500
!macroend

; Push haystack, push needle; pops "" if absent, else the needle.
Function StrContains
  Exch $R1 ; needle
  Exch
  Exch $R2 ; haystack
  Push $R3
  Push $R4
  Push $R5
  StrLen $R3 $R1
  StrCpy $R4 0
  StrCpy $R5 ""
  loop:
    StrCpy $R0 $R2 $R3 $R4
    StrCmp $R0 "" done
    StrCmp $R0 $R1 found
    IntOp $R4 $R4 + 1
    Goto loop
  found:
    StrCpy $R5 $R1
  done:
  StrCpy $R1 $R5
  Pop $R5
  Pop $R4
  Pop $R3
  Pop $R2
  Exch $R1
FunctionEnd

; Inbound UDP for discovery (47990), video (47991) and audio (47992) RTP, local subnet only. Without
; it a dismissed Windows Firewall prompt - or a venue network classed as "Public" - silently breaks
; discovery and casting.
!macro AddFirewall NAME EXE
  nsExec::Exec 'netsh advfirewall firewall delete rule name="${NAME}"'
  nsExec::Exec 'netsh advfirewall firewall add rule name="${NAME}" dir=in action=allow program="${EXE}" protocol=UDP localport=47990-47992 remoteip=localsubnet profile=any enable=yes'
!macroend

!macro RemoveFirewall NAME
  nsExec::Exec 'netsh advfirewall firewall delete rule name="${NAME}"'
!macroend

; ── Sections ─────────────────────────────────────────────────────────────────────────────────

SectionGroup /e "程序" SecGroupApps

Section "终端机 (Terminal)" SecTerminal
  !insertmacro CloseRunning "EveryStage.Terminal.exe" "EveryStage 终端机"
  RMDir /r "$INSTDIR\Terminal"          ; upgrade: no stale files from an older version
  SetOutPath "$INSTDIR\Terminal"
  File /r "${PUBLISH_DIR}\Terminal\*.*"
  CreateDirectory "$SMPROGRAMS\EveryStage"
  CreateShortcut "$SMPROGRAMS\EveryStage\EveryStage 终端机.lnk" "$INSTDIR\Terminal\EveryStage.Terminal.exe"
  !insertmacro AddFirewall "EveryStage Terminal" "$INSTDIR\Terminal\EveryStage.Terminal.exe"
  WriteRegDWORD HKLM "${STATE_KEY}" "Terminal" 1
SectionEnd

Section "投屏机 (Caster)" SecCaster
  !insertmacro CloseRunning "EveryStage.Caster.exe" "EveryStage 投屏机"
  RMDir /r "$INSTDIR\Caster"
  SetOutPath "$INSTDIR\Caster"
  File /r "${PUBLISH_DIR}\Caster\*.*"
  CreateDirectory "$SMPROGRAMS\EveryStage"
  CreateShortcut "$SMPROGRAMS\EveryStage\EveryStage 投屏机.lnk" "$INSTDIR\Caster\EveryStage.Caster.exe"
  !insertmacro AddFirewall "EveryStage Caster" "$INSTDIR\Caster\EveryStage.Caster.exe"
  WriteRegDWORD HKLM "${STATE_KEY}" "Caster" 1
SectionEnd

SectionGroupEnd

SectionGroup /e "选项" SecGroupOptions

Section "开机自动启动" SecAutoStart
  ${If} ${SectionIsSelected} ${SecTerminal}
    WriteRegStr HKLM "${RUN_KEY}" "EveryStageTerminal" '"$INSTDIR\Terminal\EveryStage.Terminal.exe"'
  ${EndIf}
  ${If} ${SectionIsSelected} ${SecCaster}
    WriteRegStr HKLM "${RUN_KEY}" "EveryStageCaster" '"$INSTDIR\Caster\EveryStage.Caster.exe"'
  ${EndIf}
SectionEnd

Section "桌面快捷方式" SecDesktop
  ${If} ${SectionIsSelected} ${SecTerminal}
    CreateShortcut "$DESKTOP\EveryStage 终端机.lnk" "$INSTDIR\Terminal\EveryStage.Terminal.exe"
  ${EndIf}
  ${If} ${SectionIsSelected} ${SecCaster}
    CreateShortcut "$DESKTOP\EveryStage 投屏机.lnk" "$INSTDIR\Caster\EveryStage.Caster.exe"
  ${EndIf}
SectionEnd

SectionGroupEnd

Section "-Common"
  ; Auto-start unticked: remove an entry left by an earlier install of the selected components.
  ${IfNot} ${SectionIsSelected} ${SecAutoStart}
    ${If} ${SectionIsSelected} ${SecTerminal}
      DeleteRegValue HKLM "${RUN_KEY}" "EveryStageTerminal"
    ${EndIf}
    ${If} ${SectionIsSelected} ${SecCaster}
      DeleteRegValue HKLM "${RUN_KEY}" "EveryStageCaster"
    ${EndIf}
  ${EndIf}
  ; %ProgramData%\EveryStage holds both apps' data. Windows' default ProgramData permissions let a
  ; standard user create files there but only change their own, so files first written by another
  ; account or by an elevated run later fail with "access denied" (import, scenarios, settings).
  ; Grant local Users (S-1-5-32-545, language-independent) Modify, inherited, and repair existing files.
  ReadEnvStr $0 "ProgramData"
  CreateDirectory "$0\EveryStage"
  nsExec::Exec 'icacls "$0\EveryStage" /grant *S-1-5-32-545:(OI)(CI)M /T /C /Q'
  ; Pairing keys and device identities live in EveryStage\secure, which local Users may NOT read: only
  ; SYSTEM, Administrators and each file's owner (CREATOR OWNER: the account EveryStage runs as, which
  ; creates them). Users can list the folder and create files in it, nothing more. The Users grant just
  ; added with /T is taken back out of that subtree first.
  CreateDirectory "$0\EveryStage\secure"
  nsExec::Exec 'icacls "$0\EveryStage\secure" /remove:g *S-1-5-32-545 /T /C /Q'
  nsExec::Exec 'icacls "$0\EveryStage\secure" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-3-0:(OI)(CI)(IO)F *S-1-5-32-545:(RX,WD,AD) /C /Q'
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\EveryStage\卸载 EveryStage.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION} (${BUILD})"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "Publisher" "${PRODUCT}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  ${If} ${SectionIsSelected} ${SecTerminal}
    WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Terminal\EveryStage.Terminal.exe"
  ${Else}
    WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Caster\EveryStage.Caster.exe"
  ${EndIf}
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair" 1
  WriteRegStr HKLM "${STATE_KEY}" "Version" "${VERSION} (${BUILD})"
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecTerminal} "连接扩展屏（LED/投影）的展示电脑：本地图片/视频/音频/文档播放，接收投屏。播放 Word/PPT 需要本机已安装 WPS 或 Microsoft Office。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecCaster} "把本机屏幕和声音投到局域网内的终端机。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecAutoStart} "登录 Windows 后自动启动所选程序（终端机通常需要）。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "在桌面创建快捷方式。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ── Callbacks ────────────────────────────────────────────────────────────────────────────────

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "${PRODUCT} 需要 64 位 Windows 10（2004 或更高版本）或 Windows 11。"
    Abort
  ${EndIf}
  SetRegView 64
  ; Machine-wide install: Start Menu / desktop shortcuts for all users (the Terminal often runs under a
  ; separate venue account), matching Program Files and the HKLM auto-start entry.
  SetShellVarContext all
  ReadRegStr $0 HKLM "SOFTWARE\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
  ${If} $0 < 19041
    MessageBox MB_ICONSTOP "${PRODUCT} 需要 Windows 10 2004（内部版本 19041）或更高版本。当前内部版本：$0"
    Abort
  ${EndIf}
  ; Default: Terminal, unless an earlier install here was Caster only.
  ReadRegDWORD $1 HKLM "${STATE_KEY}" "Terminal"
  ReadRegDWORD $2 HKLM "${STATE_KEY}" "Caster"
  ${If} $2 == 1
  ${AndIf} $1 != 1
    !insertmacro UnselectSection ${SecTerminal}
    !insertmacro SelectSection ${SecCaster}
  ${ElseIf} $2 == 1
    !insertmacro SelectSection ${SecCaster}
  ${Else}
    !insertmacro UnselectSection ${SecCaster}
  ${EndIf}
FunctionEnd

Function CheckComponents
  ${IfNot} ${SectionIsSelected} ${SecTerminal}
  ${AndIfNot} ${SectionIsSelected} ${SecCaster}
    MessageBox MB_ICONEXCLAMATION "请至少选择“终端机”或“投屏机”之一。"
    Abort
  ${EndIf}
  ${If} ${SectionIsSelected} ${SecTerminal}
    ; Word/PPT playback drives WPS or Microsoft Office through COM; everything else works without.
    StrCpy $3 0
    ReadRegStr $0 HKCR "KWPS.Application\CLSID" ""
    ${If} $0 != ""
      StrCpy $3 1
    ${EndIf}
    ReadRegStr $0 HKCR "wps.Application\CLSID" ""   ; older WPS builds
    ${If} $0 != ""
      StrCpy $3 1
    ${EndIf}
    ReadRegStr $0 HKCR "Word.Application\CLSID" ""
    ${If} $0 != ""
      StrCpy $3 1
    ${EndIf}
    ${If} $3 == 0
      ; WPS files present but no COM registration: the personal / Microsoft Store edition keeps its
      ; automation interface off until a compatibility option is ticked.
      StrCpy $4 0
      ${If} ${FileExists} "$LOCALAPPDATA\Kingsoft\WPS Office\*.*"
        StrCpy $4 1
      ${EndIf}
      ${If} ${FileExists} "$PROGRAMFILES32\Kingsoft\WPS Office\*.*"
        StrCpy $4 1
      ${EndIf}
      ${If} ${FileExists} "$PROGRAMFILES64\Kingsoft\WPS Office\*.*"
        StrCpy $4 1
      ${EndIf}
      ${If} $4 == 1
        MessageBox MB_YESNO|MB_ICONEXCLAMATION "检测到已安装 WPS，但它的 COM 接口未开启（WPS 个人版/微软商店版默认关闭），终端机暂时无法播放 Word/PPT。$\r$\n$\r$\n安装完成后请：开始菜单 → WPS Office → 配置工具 → 高级 → 兼容设置，勾选“WPS Office 兼容第三方系统和软件”，确定后关闭所有 WPS 窗口。$\r$\n$\r$\n是否继续安装？" IDYES +2
          Abort
      ${Else}
        MessageBox MB_YESNO|MB_ICONEXCLAMATION "本机未检测到 WPS 或 Microsoft Office。$\r$\n$\r$\n终端机的图片、视频、音频、PDF 和投屏不受影响，但 Word/PPT 文档将无法播放。$\r$\n可以先继续安装，之后再安装并授权 WPS。是否继续？" IDYES +2
          Abort
      ${EndIf}
    ${EndIf}
  ${EndIf}
FunctionEnd

; Started through Explorer so the app runs with the signed-in user's normal rights, not the
; installer's elevated token (an elevated first run would own the data files it creates).
Function LaunchInstalled
  ${If} ${SectionIsSelected} ${SecTerminal}
    Exec '"$WINDIR\explorer.exe" "$INSTDIR\Terminal\EveryStage.Terminal.exe"'
  ${ElseIf} ${SectionIsSelected} ${SecCaster}
    Exec '"$WINDIR\explorer.exe" "$INSTDIR\Caster\EveryStage.Caster.exe"'
  ${EndIf}
FunctionEnd

; ── Uninstall ────────────────────────────────────────────────────────────────────────────────

Var DeleteData
Var DeleteDataCheckbox

Function un.onInit
  SetRegView 64
  SetShellVarContext all
  ; Silent uninstall keeps the data unless asked: Uninstall.exe /S /DELETEDATA=1
  StrCpy $DeleteData 0
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/DELETEDATA=" $R1
  ${IfNot} ${Errors}
  ${AndIf} $R1 == "1"
    StrCpy $DeleteData 1
  ${EndIf}
FunctionEnd

Function un.DataPageCreate
  !insertmacro MUI_HEADER_TEXT "配置与配对" "选择是否同时删除本机的 EveryStage 数据。"
  nsDialogs::Create 1018
  Pop $0
  ${NSD_CreateLabel} 0 0 100% 48u "EveryStage 的设置、文件库与方案索引、背景音乐歌单、设备身份和配对信息保存在 %ProgramData%\EveryStage。默认保留，重新安装后可直接使用。$\r$\n媒体文件本身不在这里，不会被删除；日志（logs）始终保留，便于排查问题。"
  Pop $0
  ${NSD_CreateCheckbox} 0 56u 100% 12u "同时删除配置与配对（删除后需要重新配对所有设备）"
  Pop $DeleteDataCheckbox
  ${If} $DeleteData == 1
    ${NSD_Check} $DeleteDataCheckbox
  ${EndIf}
  nsDialogs::Show
FunctionEnd

Function un.DataPageLeave
  ${NSD_GetState} $DeleteDataCheckbox $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $DeleteData 1
  ${Else}
    StrCpy $DeleteData 0
  ${EndIf}
FunctionEnd

; Deletes everything in %ProgramData%\EveryStage except logs\.
Function un.DeleteDataExceptLogs
  ReadEnvStr $0 "ProgramData"
  StrCpy $0 "$0\EveryStage"
  IfFileExists "$0\*.*" 0 done
  Delete "$0\*.*"
  FindFirst $1 $2 "$0\*.*"
  loop:
    StrCmp $2 "" close
    StrCmp $2 "." next
    StrCmp $2 ".." next
    StrCmp $2 "logs" next
    RMDir /r "$0\$2"
  next:
    FindNext $1 $2
    Goto loop
  close:
  FindClose $1
  done:
FunctionEnd

Section "Uninstall"
  !insertmacro UnCloseRunning "EveryStage.Terminal.exe"
  !insertmacro UnCloseRunning "EveryStage.Caster.exe"
  DeleteRegValue HKLM "${RUN_KEY}" "EveryStageTerminal"
  DeleteRegValue HKLM "${RUN_KEY}" "EveryStageCaster"
  !insertmacro RemoveFirewall "EveryStage Terminal"
  !insertmacro RemoveFirewall "EveryStage Caster"
  Delete "$DESKTOP\EveryStage 终端机.lnk"
  Delete "$DESKTOP\EveryStage 投屏机.lnk"
  RMDir /r "$SMPROGRAMS\EveryStage"
  RMDir /r "$INSTDIR\Terminal"
  RMDir /r "$INSTDIR\Caster"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKLM "${UNINSTALL_KEY}"
  DeleteRegKey HKLM "${STATE_KEY}"
  DeleteRegKey /ifempty HKLM "Software\EveryStage"
  ; %ProgramData%\EveryStage is kept unless 「同时删除配置与配对」 was ticked (or /S /DELETEDATA=1);
  ; even then logs\ stays.
  ${If} $DeleteData == 1
    Call un.DeleteDataExceptLogs
  ${EndIf}
SectionEnd
