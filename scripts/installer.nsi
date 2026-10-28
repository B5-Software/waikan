Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

!ifndef APP_VERSION
  !define APP_VERSION "1.1.0"
!endif

!define APP_NAME "这个词在外刊中"
!define APP_ID "Waikan"
!define APP_PUBLISHER "B5-Software"
!define APP_URL "https://github.com/B5-Software/waikan"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Waikan"
!define RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"
!define PUBLISH_DIR "..\dist\Waikan-win-x64"
!define SOURCE_ICON "..\src\Waikan\Assets\app.ico"

Name "${APP_NAME}"
Caption "${APP_NAME} ${APP_VERSION} 安装程序"
OutFile "..\dist\Waikan-${APP_VERSION}-x64-setup.exe"
InstallDir "$LOCALAPPDATA\Programs\Waikan"
InstallDirRegKey HKCU "Software\Waikan" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32
ShowInstDetails show
ShowUninstDetails show
BrandingText "${APP_NAME} · 本地离线关键词计数"

VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=2052 "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey /LANG=2052 "FileDescription" "${APP_NAME} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "CC0-1.0"

!define MUI_ABORTWARNING
!define MUI_ICON "${SOURCE_ICON}"
!define MUI_UNICON "${SOURCE_ICON}"
!define MUI_WELCOMEPAGE_TITLE "安装 ${APP_NAME}"
!define MUI_WELCOMEPAGE_TEXT "这个安装程序会把《${APP_NAME}》安装到当前用户目录。$\r$\n$\r$\n安装过程不需要管理员权限，默认启用开机后台自启，并创建开始菜单快捷方式。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\Waikan.exe"
!define MUI_FINISHPAGE_RUN_TEXT "立即运行《${APP_NAME}》"
!define MUI_FINISHPAGE_LINK "打开项目主页"
!define MUI_FINISHPAGE_LINK_LOCATION "${APP_URL}"
!define MUI_FINISHPAGE_TEXT "${APP_NAME} 已安装完成。$\r$\n$\r$\n首次运行请允许 Windows 麦克风访问；之后应用会从系统托盘和桌面悬浮窗显示今日计数。"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Function .onInit
  SetRegView 64
  SetShellVarContext current
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "《${APP_NAME}》仅支持 64 位 Windows。"
    Abort
  ${EndIf}

  ; Upgrade/reinstall: close the previous instance before replacing files.
  nsExec::ExecToLog 'taskkill /F /IM Waikan.exe /T'
FunctionEnd

Function un.onInit
  SetRegView 64
  SetShellVarContext current
FunctionEnd

Section "安装" SecInstall
  SetRegView 64
  SetShellVarContext current
  SetOutPath "$INSTDIR"

  File /r "${PUBLISH_DIR}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\Waikan"
  CreateShortcut "$SMPROGRAMS\Waikan\这个词在外刊中.lnk" "$INSTDIR\Waikan.exe" "" "$INSTDIR\Waikan.exe" 0
  CreateShortcut "$SMPROGRAMS\Waikan\卸载 Waikan.lnk" "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Uninstall.exe" 0

  WriteRegStr HKCU "Software\Waikan" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Waikan" "Version" "${APP_VERSION}"

  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Waikan.exe,0"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "URLInfoAbout" "${APP_URL}"
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1

  ; The app also maintains this value on startup when the user toggles it.
  WriteRegStr HKCU "${RUN_KEY}" "Waikan" '"$INSTDIR\Waikan.exe" --background'

  DetailPrint "应用文件已安装到 $INSTDIR"
  DetailPrint "已启用开机后台自启"
SectionEnd

Section "Uninstall"
  SetRegView 64
  SetShellVarContext current

  nsExec::ExecToLog 'taskkill /F /IM Waikan.exe /T'

  Delete "$SMPROGRAMS\Waikan\这个词在外刊中.lnk"
  Delete "$SMPROGRAMS\Waikan\卸载 Waikan.lnk"
  Delete "$SMPROGRAMS\Waikan\*.lnk"
  RMDir "$SMPROGRAMS\Waikan"
  Delete "$DESKTOP\这个词在外刊中.lnk"

  DeleteRegValue HKCU "${RUN_KEY}" "Waikan"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey HKCU "Software\Waikan"

  ; Ask before deleting the user's local occurrence history. Silent
  ; uninstallers keep the data so unattended upgrades never destroy it.
  IfSilent KeepData
  MessageBox MB_YESNO|MB_ICONQUESTION "是否同时删除本机的识别记录和设置？$\r$\n$\r$\n$LOCALAPPDATA\Waikan$\r$\n$\r$\n选择“否”会保留这些数据，方便以后重新安装恢复。" IDNO KeepData
    RMDir /r "$LOCALAPPDATA\Waikan"
  KeepData:

  DetailPrint "正在删除程序文件…"
  RMDir /r "$INSTDIR"
SectionEnd
