; ─────────────────────────────────────────────────────────────────────────────
;  NetMon Installer  –  Inno Setup 6
;
;  • Installs to %LocalAppData%\Programs\NetMon  (no UAC elevation needed)
;  • Optional desktop shortcut
;  • Optional "Start with Windows" registry entry (HKCU Run key)
;  • Uninstaller also removes the Run key regardless of how it was created
; ─────────────────────────────────────────────────────────────────────────────

#define MyAppName        "NetMon"
#define MyAppVersion     "1.5.1"
#define MyAppPublisher   "NetMon"
#define MyAppExeName     "NetMon.exe"
#define MyPublishDir     "NetMon\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{8F4E2A1B-C3D5-4E6F-A7B8-9C0D1E2F3A4B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; No UAC – installs into the user profile
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Output
OutputDir=installer
OutputBaseFilename=NetMon_Setup_v{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes

; Appearance
WizardStyle=modern
WizardSizePercent=100
DisableWelcomePage=no
ShowLanguageDialog=no
AllowNoIcons=yes

; Uninstaller
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

; Installer & wizard icon (same as the app)
SetupIconFile=NetMon\NetMon.ico

; Close the running instance before install/update
CloseApplications=yes
CloseApplicationsFilter=NetMon.exe
RestartApplications=no

; Minimum OS: Windows 10
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; ── Tasks shown on the "Select Additional Tasks" page ────────────────────────
[Tasks]
Name: "desktopicon"; \
    Description: "Create a &desktop shortcut"; \
    GroupDescription: "Additional icons:"; \
    Flags: unchecked

Name: "startup"; \
    Description: "Start &NetMon automatically when Windows starts"; \
    GroupDescription: "Windows startup:"; \
    Flags: unchecked

; ── Files ─────────────────────────────────────────────────────────────────────
[Files]
; Main executable (self-contained .NET 8 bundle)
Source: "{#MyPublishDir}\NetMon.exe";                    DestDir: "{app}"; Flags: ignoreversion
; Required native DLLs (cannot be merged into the single-file bundle)
Source: "{#MyPublishDir}\D3DCompiler_47_cor3.dll";       DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\PenImc_cor3.dll";               DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\PresentationNative_cor3.dll";   DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\vcruntime140_cor3.dll";         DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\wpfgfx_cor3.dll";               DestDir: "{app}"; Flags: ignoreversion

; ── Shortcuts ─────────────────────────────────────────────────────────────────
[Icons]
Name: "{group}\{#MyAppName}";           Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";     Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

; ── Registry ──────────────────────────────────────────────────────────────────
[Registry]
; "Start with Windows" – only written when the user ticks that task.
; Flags: uninsdeletevalue removes it again on uninstall.
Root: HKCU; \
    Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "NetMon"; \
    ValueData: """{app}\{#MyAppExeName}"""; \
    Tasks: startup; \
    Flags: uninsdeletevalue

; ── Post-install: offer to launch the app ─────────────────────────────────────
[Run]
Filename: "{app}\{#MyAppExeName}"; \
    Description: "Launch {#MyAppName} now"; \
    Flags: nowait postinstall skipifsilent

; ── Pre-uninstall: kill the running process ───────────────────────────────────
[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/IM NetMon.exe /F"; \
    Flags: runhidden; RunOnceId: "KillNetMon"

; ── Code ──────────────────────────────────────────────────────────────────────
[Code]

// On uninstall: remove the Run registry value no matter how it was created
// (the user may have enabled "Start with Windows" from inside the app itself,
//  which would not be covered by the [Registry] uninsdeletevalue flag above).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKCU,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
      'NetMon');
end;

// Warn the user if they are about to downgrade
function InitializeSetup(): Boolean;
var
  InstalledVer, ThisVer: String;
begin
  Result := True;
  if RegQueryStringValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F4E2A1B-C3D5-4E6F-A7B8-9C0D1E2F3A4B}_is1',
    'DisplayVersion', InstalledVer) then
  begin
    ThisVer := '{#MyAppVersion}';
    if CompareStr(InstalledVer, ThisVer) > 0 then
      Result := MsgBox(
        'A newer version (' + InstalledVer + ') is already installed.'#13#10 +
        'Continue and install version ' + ThisVer + ' anyway?',
        mbConfirmation, MB_YESNO) = IDYES;
  end;
end;
