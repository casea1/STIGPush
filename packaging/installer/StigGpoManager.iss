; Inno Setup script for STIG GPO Manager.
; Build (CI does this): ISCC.exe /DAppVersion=1.0.0-beta.3 /DAppVersionNum=1.0.0.3 packaging\installer\StigGpoManager.iss
; Expects the built exe at dist\StigGpoManager.exe. Output: dist\StigGpoManager-Setup-<AppVersion>.exe

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef AppVersionNum
  #define AppVersionNum "1.0.0.0"
#endif

[Setup]
AppId={{6F1C2B7E-3D4A-4E5F-9A8B-1C2D3E4F5A6B}
AppName=STIG GPO Manager
AppVersion={#AppVersion}
AppVerName=STIG GPO Manager {#AppVersion}
AppPublisher=Site ISSO / Systems Administration
AppComments=Quarterly import of DISA STIG GPOs into Active Directory: prepare, review, pilot, promote, roll back.
VersionInfoVersion={#AppVersionNum}
VersionInfoProductVersion={#AppVersionNum}
VersionInfoDescription=STIG GPO Manager setup
DefaultDirName={autopf}\StigGpoManager
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableReadyPage=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\..\dist
OutputBaseFilename=StigGpoManager-Setup-{#AppVersion}
SetupIconFile=..\..\src\App\Resources\app.ico
UninstallDisplayIcon={app}\StigGpoManager.exe
UninstallDisplayName=STIG GPO Manager
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
CloseApplications=force
RestartApplications=no
SetupLogging=yes

[Messages]
WelcomeLabel2=This installs STIG GPO Manager {#AppVersion}.%n%nIt imports each quarter's DISA STIG GPO package into Active Directory as new, versioned GPOs, pilots them, and promotes them, with your exception GPOs always kept on top and one-click rollback.%n%nNothing in Active Directory changes until you confirm it in the tool. Your configuration, run history and audit log are kept on upgrade and uninstall.

[Tasks]
Name: "tray"; Description: "Show the status icon in the notification area at logon (administrators)"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "..\..\dist\StigGpoManager.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\config.sample.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\STIG GPO Manager"; Filename: "{app}\StigGpoManager.exe"; Comment: "Quarterly DISA STIG GPO import"
Name: "{autoprograms}\STIG GPO Manager (demo)"; Filename: "{app}\StigGpoManager.exe"; Parameters: "/demo"; Comment: "Try the tool on a simulated domain (no AD changes)"
Name: "{autodesktop}\STIG GPO Manager"; Filename: "{app}\StigGpoManager.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\StigGpoManager.exe"; Parameters: "/postinstall /quiet"; Flags: runhidden waituntilterminated; StatusMsg: "Preparing the data folder..."
Filename: "{app}\StigGpoManager.exe"; Parameters: "/register-tray /quiet"; Flags: runhidden waituntilterminated; Tasks: tray; StatusMsg: "Registering the status icon..."
Filename: "{app}\StigGpoManager.exe"; Description: "Open STIG GPO Manager now"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{app}\StigGpoManager.exe"; Parameters: "/preuninstall /quiet"; Flags: runhidden waituntilterminated; RunOnceId: "StigGpoManagerPreUninstall"

[Code]
// Upgrades: stop a running copy (tray or window) before files are replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM StigGpoManager.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;
