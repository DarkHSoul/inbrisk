; Inno Setup 7 Script for Inbrisk
; Native Windows Desktop Shell, System Tray HUD, and MCP Computer-Use Runtime

#define MyAppName "Inbrisk"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Inbrisk"
#define MyAppURL "https://github.com/DarkHSoul/inbrisk"
#define MyAppExeName "inbrisk.exe"

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
; requestedExecutionLevel coverage (F31): Inno derives the setup/uninstall
; manifest from PrivilegesRequired — "lowest" emits asInvoker, and the
; overrides below relaunch elevated on demand rather than embedding
; requireAdministrator. The SendInput binary's own manifest lives at
; src\Inbrisk.Cli\app.manifest (no uiAccess — SendInput/UIA does not need it
; from a non-accessibility, unsigned-or-user-signed binary).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
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

; --- opt-in Authenticode signing (F31) --------------------------------------
; Dev/source builds stay unsigned. A release build signs the payload exe,
; the setup binary and the uninstaller once a cert is configured — either
; pass /DSignCert plus the sign-tool definition:
;   iscc /DSignCert /Sinbrisk="signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /sha1 $q<THUMBPRINT>$q $f" inbrisk.iss
; or just set INBRISK_SIGN_SHA1 in the environment (the cert thumbprint the
; release pipeline's signtool /S line already consumes).
#ifndef SignCert
  #if GetEnv("INBRISK_SIGN_SHA1") != ""
    #define SignCert
  #endif
#endif
#ifdef SignCert
  #pragma message "inbrisk: Authenticode signing ENABLED (SignCert / INBRISK_SIGN_SHA1)"
  #define ExeFlags "ignoreversion signonce"
#else
  #pragma message "inbrisk: unsigned build — set INBRISK_SIGN_SHA1 or pass /DSignCert to sign"
  #define ExeFlags "ignoreversion"
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
