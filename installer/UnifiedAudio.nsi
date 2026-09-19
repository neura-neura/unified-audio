Unicode true
RequestExecutionLevel user
SetCompressor /SOLID lzma

!include "FileFunc.nsh"

!define PRODUCT_NAME "UnifiedAudio"
!define PRODUCT_VERSION "0.1.5"
!define PRODUCT_PUBLISHER "UnifiedAudio contributors"
!define PRODUCT_WEB_SITE "https://github.com/neura-neura/unified-audio"
!define PRODUCT_DIR_REGKEY "Software\Microsoft\Windows\CurrentVersion\App Paths\UnifiedAudio.exe"
!define PRODUCT_UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedAudio"

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "..\artifacts\UnifiedAudioSetup-${PRODUCT_VERSION}-x64.exe"
InstallDir "$LOCALAPPDATA\Programs\UnifiedAudio"
InstallDirRegKey HKCU "${PRODUCT_DIR_REGKEY}" ""
ShowInstDetails show
ShowUnInstDetails show

Page directory
Page instfiles
UninstPage uninstConfirm
UninstPage instfiles

Function .onInstSuccess
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/LAUNCH" $R1
  IfErrors done
  Exec '"$INSTDIR\UnifiedAudio.exe"'
done:
FunctionEnd

Section "UnifiedAudio" SEC_MAIN
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  File /r "payload\*.*"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\UnifiedAudio"
  CreateShortcut "$SMPROGRAMS\UnifiedAudio\UnifiedAudio.lnk" "$INSTDIR\UnifiedAudio.exe" "" "$INSTDIR\Assets\AppIcon.ico"
  CreateShortcut "$SMPROGRAMS\UnifiedAudio\Desinstalar UnifiedAudio.lnk" "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "${PRODUCT_DIR_REGKEY}" "" "$INSTDIR\UnifiedAudio.exe"
  WriteRegStr HKCU "${PRODUCT_DIR_REGKEY}" "Path" "$INSTDIR"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "URLInfoAbout" "${PRODUCT_WEB_SITE}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "DisplayIcon" "$INSTDIR\Assets\AppIcon.ico"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "${PRODUCT_UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${PRODUCT_UNINST_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  Delete "$SMSTARTUP\UnifiedAudio.lnk"
  Delete "$SMPROGRAMS\UnifiedAudio\UnifiedAudio.lnk"
  Delete "$SMPROGRAMS\UnifiedAudio\Desinstalar UnifiedAudio.lnk"
  RMDir "$SMPROGRAMS\UnifiedAudio"
  DeleteRegKey HKCU "${PRODUCT_DIR_REGKEY}"
  DeleteRegKey HKCU "${PRODUCT_UNINST_KEY}"
  RMDir /r "$INSTDIR"
  ; Los perfiles y logs bajo LocalAppData\UnifiedAudio se conservan deliberadamente.
SectionEnd
