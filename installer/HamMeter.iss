; HamMeter for Aion 2 - installer engine.
; Runs invisibly (/VERYSILENT) behind HamMeter-Setup.exe, which shows the wizard in the
; HamMeter design and passes the user's choices as /TASKS and /npcapbyus.
; Build with build\build.ps1 (defines AppDir and AppVersion).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppDir
  #error AppDir must point to the published app folder
#endif

#define AppName "HamMeter for Aion 2"
#define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\HamMeter-Aion2"
#define FirewallRule "HamMeter for Aion 2"

[Setup]
AppId={{6C2E1B7A-3F4D-4E8B-9A51-2D7C0F4B8E13}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=NennMichSchinken
DefaultDirName={autopf}\HamMeter
UsePreviousAppDir=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=HamMeter-Core
Compression=lzma2/ultra64
SolidCompression=yes
; Our own "Apps & Features" entry (below) opens the HamMeter uninstall wizard, so Inno's
; entry is not created. Inno's uninstaller still exists and is started by our wizard.
CreateUninstallRegKey=no
Uninstallable=yes
UninstallFilesDir={app}\uninstall
CloseApplications=yes
RestartApplications=no
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
SetupLogging=yes

[Tasks]
Name: "startmenu"; Description: "Start menu entry"
Name: "desktop"; Description: "Desktop shortcut"; Flags: unchecked
Name: "firewall"; Description: "Firewall rule (capture without Npcap)"; Flags: unchecked

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\HamMeter"; Filename: "{app}\HamMeter.exe"; Tasks: startmenu
Name: "{autodesktop}\HamMeter"; Filename: "{app}\HamMeter.exe"; Tasks: desktop

[Registry]
Root: HKLM; Subkey: "{#UninstallKey}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "{#UninstallKey}"; ValueType: string; ValueName: "DisplayName"; ValueData: "{#AppName}"
Root: HKLM; Subkey: "{#UninstallKey}"; ValueType: string; ValueName: "DisplayVersion"; ValueData: "{#AppVersion}"
Root: HKLM; Subkey: "{#UninstallKey}"; ValueType: string; ValueName: "Publisher"; ValueData: "NennMichSchinken"
Root: HKLM; Subkey: "{#UninstallKey}"; ValueType: string; ValueName: "DisplayIcon"; ValueData: "{app}\HamMeter.exe"
Root: HKLM; Subkey: "{#UninstallKey}"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"
Root: HKLM; Subkey: "{#UninstallKey}"; ValueType: string; ValueName: "UninstallString"; ValueData: """{app}\HamMeter.exe"" --uninstall"
Root: HKLM; Subkey: "{#UninstallKey}"; ValueType: dword; ValueName: "NoModify"; ValueData: 1
Root: HKLM; Subkey: "{#UninstallKey}"; ValueType: dword; ValueName: "NoRepair"; ValueData: 1
Root: HKLM; Subkey: "{#UninstallKey}"; ValueType: dword; ValueName: "NpcapInstalledByHamMeter"; ValueData: 1; Check: NpcapByUs
; The chosen options, so an update can reuse them without asking again.
Root: HKLM; Subkey: "{#UninstallKey}"; ValueType: string; ValueName: "SetupTasks"; ValueData: "{code:SelectedTasks}"

[Run]
; Always drop an old rule first (mode may have changed), then add it only in raw-socket
; mode. The rule is limited to inbound TCP for HamMeter.exe itself.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallRule}"" dir=in action=allow protocol=TCP program=""{app}\HamMeter.exe"" enable=yes"; Flags: runhidden; Tasks: firewall

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function NpcapByUs: Boolean;
begin
  Result := ExpandConstant('{param:npcapbyus|0}') = '1';
end;

function SelectedTasks(Param: String): String;
begin
  Result := WizardSelectedTasks(False);
end;
