; Inno Setup 7 Script for Inbrisk
; Native Windows Desktop Shell, System Tray HUD, and MCP Computer-Use Runtime

#define MyAppName "Inbrisk"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Inbrisk"
#define MyAppURL "https://github.com/DarkHSoul/inbrisk"
#define MyAppExeName "inbrisk.exe"

; --- opt-in Authenticode signing (F31) --------------------------------------
; Evaluated BEFORE [Setup] so SignTool / SignedUninstaller take effect cleanly.
; Dev/source builds stay unsigned. Release builds pass /DSignCert plus /Sinbrisk=...
; (wired automatically by tools\publish.ps1 -Sign) or set INBRISK_SIGN_SHA1,
; INBRISK_SIGN_PFX, or INBRISK_AZURE_SIGNING_METADATA in the environment.
#ifndef SignCert
  #if GetEnv("INBRISK_SIGN_SHA1") != "" || GetEnv("INBRISK_SIGN_PFX") != "" || GetEnv("INBRISK_AZURE_SIGNING_METADATA") != ""
    #define SignCert
  #endif
#endif
#ifdef SignCert
  #pragma message "inbrisk: Authenticode signing ENABLED"
  #define ExeFlags "ignoreversion signonce"
#else
  #pragma message "inbrisk: unsigned build — pass /DSignCert or set INBRISK_SIGN_SHA1 / INBRISK_SIGN_PFX / INBRISK_AZURE_SIGNING_METADATA to sign"
  #define ExeFlags "ignoreversion"
#endif

[Setup]
AppId={{E5D4B261-2A1B-4C98-8F9E-379E9F3D83D1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
; Per-user install by default (no UAC prompt required), with option to elevate to all users.
; When /DUiAccess is passed (signed Program Files build using app.uiaccess.manifest),
; Windows requires the executable to reside in a secure location such as Program Files,
; so we require administrative install scope.
#ifdef UiAccess
PrivilegesRequired=admin
#else
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
#endif
OutputDir=..\artifacts\release
OutputBaseFilename=InbriskSetup
SetupIconFile=inbrisk.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=no
RestartApplications=no
DisableProgramGroupPage=yes
#ifdef SignCert
SignTool=inbrisk
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Windows başladığında sistem tepsisinde (Tray) otomatik çalıştır"; GroupDescription: "Başlatma Seçenekleri:"; Flags: checkedonce
Name: "addtopath"; Description: "Inbrisk komutunu sistem PATH ortam değişkenine ekle (Terminal erişimi için)"; GroupDescription: "Geliştirici Seçenekleri:"; Flags: checkedonce

[Files]
Source: "..\artifacts\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: {#ExeFlags}
Source: "inbrisk.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\Inbrisk.Platform.Windows\Assets\inbrisk_logo.png"; DestDir: "{app}\Assets"; Flags: ignoreversion
; Legal payload — license + attribution notices must ship inside the install dir.
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NOTICE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "tray"; IconFilename: "{app}\inbrisk.ico"; WorkingDir: "{app}"
Name: "{autoprograms}\{#MyAppName} Ayarları"; Filename: "{app}\{#MyAppExeName}"; Parameters: "settings"; IconFilename: "{app}\inbrisk.ico"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "tray"; IconFilename: "{app}\inbrisk.ico"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"" tray"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "tray"; Description: "{#MyAppName}'i Sistem Tepsisinde (Tray) Başlat"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#MyAppExeName}"; Parameters: "setup"; Description: "AI Hostlarını (Claude Desktop, Cursor vb.) Otomatik Yapılandır"; Flags: nowait postinstall skipifsilent unchecked

[Code]
procedure KillRunningInbrisk();
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM inbrisk.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup(): Boolean;
begin
  KillRunningInbrisk();
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillRunningInbrisk();
  Result := '';
end;

procedure AddToPath();
var
  OldPath, NewPath: string;
  AppDir: string;
  RootKey: Integer;
  SubKey: string;
begin
  AppDir := ExpandConstant('{app}');
  if IsAdminInstallMode then
  begin
    RootKey := HKEY_LOCAL_MACHINE;
    SubKey := 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';
  end
  else
  begin
    RootKey := HKEY_CURRENT_USER;
    SubKey := 'Environment';
  end;

  if RegQueryStringValue(RootKey, SubKey, 'Path', OldPath) then
  begin
    if Pos(';' + Uppercase(AppDir) + ';', ';' + Uppercase(OldPath) + ';') = 0 then
    begin
      if (OldPath <> '') and (OldPath[Length(OldPath)] <> ';') then
        OldPath := OldPath + ';';
      NewPath := OldPath + AppDir;
      RegWriteStringValue(RootKey, SubKey, 'Path', NewPath);
    end;
  end
  else
  begin
    RegWriteStringValue(RootKey, SubKey, 'Path', AppDir);
  end;
end;

procedure RemoveFromPath();
var
  OldPath, NewPath: string;
  AppDir: string;
  P: Integer;
  RootKey: Integer;
  SubKey: string;
begin
  AppDir := ExpandConstant('{app}');
  if IsAdminInstallMode then
  begin
    RootKey := HKEY_LOCAL_MACHINE;
    SubKey := 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';
  end
  else
  begin
    RootKey := HKEY_CURRENT_USER;
    SubKey := 'Environment';
  end;

  if RegQueryStringValue(RootKey, SubKey, 'Path', OldPath) then
  begin
    P := Pos(';' + Uppercase(AppDir) + ';', ';' + Uppercase(OldPath) + ';');
    if P > 0 then
    begin
      NewPath := OldPath;
      if Pos(Uppercase(AppDir) + ';', Uppercase(NewPath)) = 1 then
        Delete(NewPath, 1, Length(AppDir) + 1)
      else if Pos(';' + Uppercase(AppDir), Uppercase(NewPath)) > 0 then
        Delete(NewPath, Pos(';' + Uppercase(AppDir), Uppercase(NewPath)), Length(AppDir) + 1)
      else if Uppercase(NewPath) = Uppercase(AppDir) then
        NewPath := '';
      RegWriteStringValue(RootKey, SubKey, 'Path', NewPath);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('addtopath') then
      AddToPath();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveFromPath();
  end;
end;
