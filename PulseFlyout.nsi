Unicode true
Name "PulseFlyout"
OutFile "installer\PulseFlyout-2.14.0-Setup.exe"
InstallDir "$PROGRAMFILES64\PulseFlyout"
InstallDirRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PulseFlyout" "InstallLocation"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
Icon "FluentFlyoutWPF\Resources\PulseFlyout.ico"
VIProductVersion "2.14.0.0"
VIAddVersionKey /LANG=1033 "ProductName" "PulseFlyout"
VIAddVersionKey /LANG=1033 "CompanyName" "PulseFlyout Contributors"
VIAddVersionKey /LANG=1033 "FileDescription" "PulseFlyout installer"
VIAddVersionKey /LANG=1033 "FileVersion" "2.14.0"

Page directory
Page instfiles
UninstPage uninstConfirm
UninstPage instfiles

Section "PulseFlyout" SecMain
  SectionIn RO
  SetOutPath "$INSTDIR"
  File /r "FluentFlyoutWPF\bin\Release\Publish\*"
  CreateDirectory "$SMPROGRAMS\PulseFlyout"
  CreateShortcut "$SMPROGRAMS\PulseFlyout\PulseFlyout.lnk" "$INSTDIR\FluentFlyout.exe" "" "$INSTDIR\Resources\PulseFlyout.ico"
  CreateShortcut "$DESKTOP\PulseFlyout.lnk" "$INSTDIR\FluentFlyout.exe" "" "$INSTDIR\Resources\PulseFlyout.ico"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PulseFlyout" "DisplayName" "PulseFlyout"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PulseFlyout" "DisplayVersion" "2.14.0"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PulseFlyout" "Publisher" "PulseFlyout Contributors"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PulseFlyout" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PulseFlyout" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PulseFlyout" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PulseFlyout" "NoRepair" 1
SectionEnd

Section "Uninstall"
  Delete "$DESKTOP\PulseFlyout.lnk"
  Delete "$SMPROGRAMS\PulseFlyout\PulseFlyout.lnk"
  RMDir "$SMPROGRAMS\PulseFlyout"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PulseFlyout"
  RMDir /r "$INSTDIR"
SectionEnd
