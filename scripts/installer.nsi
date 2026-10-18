Unicode True

!include "MUI2.nsh"
!include "FileFunc.nsh"

# ── General Configuration ──────────────────────────────
Name "Nocturne"
OutFile "../dist/Nocturne-v4.0.0-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\Nocturne"
InstallDirRegKey HKCU "Software\Nocturne" "InstallDir"
RequestExecutionLevel user

SetCompressor /SOLID zlib

# ── Interface Settings ─────────────────────────────────
!define MUI_ABORTWARNING
!define MUI_ICON "../build/icon.ico"
!define MUI_UNICON "../build/icon.ico"
!define MUI_WELCOMEFINISHPAGE_BITMAP_NOSTRETCH

# ── Pages ──────────────────────────────────────────────
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\Nocturne.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch Nocturne"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

# ── Languages ──────────────────────────────────────────
!insertmacro MUI_LANGUAGE "English"

# ── Installer Section ──────────────────────────────────
Section "Nocturne Core" SecCore
    SetOutPath "$INSTDIR"

    # Copy all published files
    File /r "..\bin\native-dist\*.*"

    # Write registry keys
    WriteRegStr HKCU "Software\Nocturne" "InstallDir" "$INSTDIR"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Nocturne" "DisplayName" "Nocturne"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Nocturne" "DisplayVersion" "4.0.0"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Nocturne" "Publisher" "Vishal"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Nocturne" "DisplayIcon" "$INSTDIR\Nocturne.exe"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Nocturne" "UninstallString" '"$INSTDIR\Uninstall Nocturne.exe"'
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Nocturne" "NoModify" 1
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Nocturne" "NoRepair" 1

    # Write uninstaller
    WriteUninstaller "$INSTDIR\Uninstall Nocturne.exe"

    # Start menu & Desktop shortcuts
    CreateDirectory "$SMPROGRAMS\Nocturne"
    CreateShortcut "$SMPROGRAMS\Nocturne\Nocturne.lnk" "$INSTDIR\Nocturne.exe" "" "$INSTDIR\Nocturne.exe" 0
    CreateShortcut "$SMPROGRAMS\Nocturne\Uninstall Nocturne.lnk" "$INSTDIR\Uninstall Nocturne.exe" "" "$INSTDIR\Uninstall Nocturne.exe" 0
    CreateShortcut "$DESKTOP\Nocturne.lnk" "$INSTDIR\Nocturne.exe" "" "$INSTDIR\Nocturne.exe" 0
SectionEnd

# ── Uninstaller Section ────────────────────────────────
Section "Uninstall"
    # Remove files
    RMDir /r "$INSTDIR"

    # Remove shortcuts
    Delete "$SMPROGRAMS\Nocturne\Nocturne.lnk"
    Delete "$SMPROGRAMS\Nocturne\Uninstall Nocturne.lnk"
    RMDir "$SMPROGRAMS\Nocturne"
    Delete "$DESKTOP\Nocturne.lnk"

    # Remove registry entries
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Nocturne"
    DeleteRegKey HKCU "Software\Nocturne"
SectionEnd
