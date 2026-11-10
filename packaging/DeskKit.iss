; DeskKit suite installer. Build with packaging\build-suite.ps1 (it passes the defines below
; and writes GenDir\generated.iss: the component list, the module files and the helper table).
; Contract: docs/module-contract.md sections 2.3, 8.3, 10.4.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef StageDir
  #define StageDir "..\dist\stage"
#endif
#ifndef GenDir
  #define GenDir "..\dist"
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

#include GenDir + "\generated.iss"

[Setup]
; Fixed AppId: never change it, or upgrades install side by side.
AppId={{60B9D8BF-5C25-489A-B8BA-99D41F974319}
AppName=DeskKit
AppVersion={#AppVersion}
AppVerName=DeskKit {#AppVersion}
AppPublisher=inNATE Tools
AppCopyright=Copyright (c) 2026 inNATE Tools
; Per-user default: %LOCALAPPDATA%\Programs\DeskKit
DefaultDirName={autopf}\DeskKit
DefaultGroupName=DeskKit
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile={#StageDir}\LICENSE
WizardStyle=modern
VersionInfoCompany=inNATE Tools
VersionInfoCopyright=Copyright (c) 2026 inNATE Tools
VersionInfoProductName=DeskKit
VersionInfoDescription=DeskKit Setup
VersionInfoVersion={#AppVersion}
OutputDir={#OutDir}
OutputBaseFilename=DeskKit-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
UninstallDisplayName=DeskKit
#ifdef HasShell
UninstallDisplayIcon={app}\DeskKit.exe
; No AppMutex: a silent self-update waits for the shell mutex in InitializeSetup instead.
CloseApplications=yes
RestartApplications=no
CloseApplicationsFilter=DeskKit.exe
#endif
; SIGNING: unsigned for now (same as Region Control).

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &Desktop shortcut"; Flags: unchecked
#ifdef HasShell
Name: "startup"; Description: "&Start DeskKit when I sign in to Windows"; Flags: unchecked
#endif
Name: "helpers"; Description: "&Download missing helpers (for example FFmpeg, SoundVolumeView). Each download asks first and is checked against a SHA256."; Flags: unchecked
#ifdef HasRC
; Region Control tasks. Same text and behaviour as its own installer (RegionControl.iss v0.1.0).
Name: "rcstartup"; Description: "Start &Snap Layouts with Windows"; Flags: unchecked; Components: regioncontrol
Name: "snapauto"; Description: "Let Snap Layouts turn off Windows' own snapping for me (keeps Win+Arrow)"; GroupDescription: "Windows' own snap-on-drag (Snap Layouts):"; Flags: exclusive unchecked; Components: regioncontrol
Name: "snapmanual"; Description: "I will turn it off myself in Settings (steps on the last page)"; GroupDescription: "Windows' own snap-on-drag (Snap Layouts):"; Flags: exclusive; Components: regioncontrol
#endif

#ifdef HasShell
[Files]
Source: "{#StageDir}\DeskKit.exe"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Files]
Source: "{#StageDir}\suite.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

#ifdef HasShell
[Icons]
Name: "{autoprograms}\DeskKit"; Filename: "{app}\DeskKit.exe"
Name: "{autodesktop}\DeskKit"; Filename: "{app}\DeskKit.exe"; Tasks: desktopicon

[Registry]
; The shell's own autostart value (contract 7.4): HKCU Run, name DeskKit.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DeskKit"; ValueData: """{app}\DeskKit.exe"""; Tasks: startup; Flags: uninsdeletevalue
#endif
#ifdef HasRC
; Same value Region Control's own toggle writes: HKCU Run, name RegionControl, quoted exe path.
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RegionControl"; ValueData: """{app}\modules\regioncontrol\bin\RegionControl.exe"""; Tasks: rcstartup; Flags: uninsdeletevalue
#endif

[UninstallDelete]
; Everything under modules, including files an update added and the staging folder _update (contract 16.6).
; User data lives under %APPDATA%\DeskKit, not here.
Type: filesandordirs; Name: "{app}\modules"

#ifdef HasShell
[Run]
Filename: "{app}\DeskKit.exe"; Description: "Start DeskKit"; Flags: nowait postinstall skipifsilent runasoriginaluser
; Silent upgrade over an existing install (the shell's self-update): start the shell again.
Filename: "{app}\DeskKit.exe"; Flags: nowait runasoriginaluser; Check: RestartShellAfterSilentUpgrade
#endif

[Code]
const
  RcAppId = '{6F1D2B7E-3C54-4E0A-9B6A-5D2C8A41E7F3}_is1';
  UninstKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\';
  AdvKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced';
  DeskKey = 'Control Panel\Desktop';
  SPI_SETDOCKMOVING = $0091;
  SPIF_UPDATE_AND_SEND = 3;

function SystemParametersInfoW(uiAction, uiParam: Cardinal; pvParam: Cardinal; fWinIni: Cardinal): Boolean;
  external 'SystemParametersInfoW@user32.dll stdcall';

{ ---- Region Control: separate install check ---- }

function RegionControlInstalledSeparately: Boolean;
begin
  Result := RegKeyExists(HKCU, UninstKey + RcAppId) or RegKeyExists(HKLM, UninstKey + RcAppId);
end;

function RcUserDataDir: String;
begin
  Result := ExpandConstant('{userappdata}\RegionControl');
end;

function RcBackupFile: String;
begin
  Result := RcUserDataDir + '\native-snap-backup.json';
end;

function ModuleIndex(const Id: String): Integer;
var
  I: Integer;
begin
  Result := -1;
  for I := 0 to ModuleCount - 1 do
    if ModuleId(I) = Id then Result := I;
end;

procedure InitializeWizard;
var
  Idx: Integer;
begin
  Idx := ModuleIndex('regioncontrol');
  if (Idx >= 0) and RegionControlInstalledSeparately then
  begin
    WizardForm.ComponentsList.Checked[Idx] := False;
    WizardForm.ComponentsList.ItemCaption[Idx] := WizardForm.ComponentsList.ItemCaption[Idx] + ' (already installed separately)';
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine + MemoComponentsInfo + NewLine + NewLine + MemoTasksInfo;
  if WizardIsTaskSelected('snapauto') then
    Result := Result + NewLine + NewLine + 'Windows snapping change (undone on uninstall):' + NewLine +
      Space + 'HKCU\' + DeskKey + '  DockMoving -> 0 (SPI_SETDOCKMOVING)' + NewLine +
      Space + 'HKCU\' + AdvKey + '  EnableSnapBar -> DWORD 0' + NewLine +
      Space + 'Old values saved to ' + RcBackupFile;
end;

{ ---- Region Control: native snap off / restore (copied from RegionControl.iss v0.1.0) ---- }

function ReadRegText(const Key, Name: String; var Text: String): Boolean;
var
  S: String;
  D: Cardinal;
begin
  Result := True;
  if RegQueryStringValue(HKCU, Key, Name, S) then Text := S
  else if RegQueryDWordValue(HKCU, Key, Name, D) then Text := IntToStr(D)
  else Result := False;
end;

procedure SnapOff;
var
  Dock, Bar, Json: String;
begin
  ForceDirectories(RcUserDataDir);
  if not FileExists(RcBackupFile) then
  begin
    Json := '{' + #13#10;
    if ReadRegText(DeskKey, 'DockMoving', Dock) then Json := Json + '  "DockMoving": "' + Dock + '",' + #13#10
    else Json := Json + '  "DockMoving": null,' + #13#10;
    if ReadRegText(AdvKey, 'EnableSnapBar', Bar) then Json := Json + '  "EnableSnapBar": ' + Bar + #13#10
    else Json := Json + '  "EnableSnapBar": null' + #13#10;
    Json := Json + '}' + #13#10;
    SaveStringToFile(RcBackupFile, Json, False);
  end;
  SystemParametersInfoW(SPI_SETDOCKMOVING, 0, 0, SPIF_UPDATE_AND_SEND);
  RegWriteDWordValue(HKCU, AdvKey, 'EnableSnapBar', 0);
end;

{ Reads one field of the simple backup JSON. Returns '' when the field is null or absent. }
function JsonField(const Json, Name: String): String;
var
  P, E: Integer;
  Rest: String;
begin
  Result := '';
  P := Pos('"' + Name + '":', Json);
  if P = 0 then Exit;
  Rest := Trim(Copy(Json, P + Length(Name) + 3, Length(Json)));
  E := 1;
  while (E <= Length(Rest)) and (Rest[E] <> ',') and (Rest[E] <> #13) and (Rest[E] <> #10) and (Rest[E] <> '}') do E := E + 1;
  Rest := Trim(Copy(Rest, 1, E - 1));
  if Rest = 'null' then Exit;
  StringChange(Rest, '"', '');
  Result := Rest;
end;

procedure SnapRestore;
var
  Raw: AnsiString;
  Dock, Bar: String;
begin
  if not FileExists(RcBackupFile) then Exit;
  if not LoadStringFromFile(RcBackupFile, Raw) then Exit;
  Dock := JsonField(String(Raw), 'DockMoving');
  Bar := JsonField(String(Raw), 'EnableSnapBar');
  SystemParametersInfoW(SPI_SETDOCKMOVING, StrToIntDef(Dock, 1), 0, SPIF_UPDATE_AND_SEND);
  if Bar = '' then RegDeleteValue(HKCU, AdvKey, 'EnableSnapBar')
  else RegWriteDWordValue(HKCU, AdvKey, 'EnableSnapBar', StrToIntDef(Bar, 1));
  DeleteFile(RcBackupFile);
end;

{ ---- Helpers: detect and download (contract 8.2, 8.3) ---- }

function HelpersDir: String;
begin
  Result := ExpandConstant('{localappdata}\DeskKit\helpers');
end;

function ExpandHelperPath(const P: String): String;
begin
  Result := P;
  StringChangeEx(Result, '{helpers_dir}', HelpersDir, True);
  StringChangeEx(Result, '%LOCALAPPDATA%', GetEnv('LOCALAPPDATA'), True);
  StringChangeEx(Result, '%APPDATA%', GetEnv('APPDATA'), True);
  StringChangeEx(Result, '%TEMP%', GetEnv('TEMP'), True);
end;

{ One path, with at most one '*' standing for one folder name. }
function PathExistsWild(const P: String): Boolean;
var
  S, Prefix, Suffix: String;
  Star: Integer;
  FR: TFindRec;
begin
  Result := False;
  S := ExpandHelperPath(P);
  Star := Pos('*', S);
  if Star = 0 then
  begin
    Result := FileExists(S);
    Exit;
  end;
  Prefix := Copy(S, 1, Star - 1);
  Suffix := Copy(S, Star + 1, Length(S));
  if FindFirst(Prefix + '*', FR) then
  try
    repeat
      if ((FR.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (FR.Name <> '.') and (FR.Name <> '..') then
        if FileExists(Prefix + FR.Name + Suffix) then Result := True;
    until Result or not FindNext(FR);
  finally
    FindClose(FR);
  end;
end;

function HelperPresent(Row: Integer): Boolean;
var
  Paths, One, OnPath: String;
  P: Integer;
begin
  Result := False;
  Paths := HelperField(Row, 'paths');
  while (Paths <> '') and not Result do
  begin
    P := Pos('|', Paths);
    if P = 0 then begin One := Paths; Paths := ''; end
    else begin One := Copy(Paths, 1, P - 1); Paths := Copy(Paths, P + 1, Length(Paths)); end;
    if PathExistsWild(One) then Result := True;
  end;
  OnPath := HelperField(Row, 'onpath');
  if (not Result) and (OnPath <> '') then
    Result := FileSearch(OnPath, GetEnv('PATH')) <> '';
end;

function UrlFileName(const Url: String): String;
var
  I: Integer;
begin
  Result := Url;
  for I := Length(Url) downto 1 do
    if Url[I] = '/' then begin Result := Copy(Url, I + 1, Length(Url)); Break; end;
end;

procedure DownloadHelper(Row: Integer);
var
  Name, Url, Dest, Tmp, Archive: String;
  Rc: Integer;
begin
  Name := HelperField(Row, 'name');
  if not WizardIsComponentSelected(HelperField(Row, 'module')) then Exit;
  if HelperPresent(Row) then Exit;
  if not WizardSilent then
    if MsgBox('Download ' + Name + '?' + #13#10 + #13#10 +
              'Licence: ' + HelperField(Row, 'licence') + #13#10 +
              'Page: ' + HelperField(Row, 'page') + #13#10 + #13#10 +
              'The file is checked against a SHA256 before it is kept.', mbConfirmation, MB_YESNO) <> IDYES then Exit;
  Url := HelperField(Row, 'url');
  Archive := HelperField(Row, 'archive');
  Dest := HelpersDir + '\' + HelperField(Row, 'id');
  try
    DownloadTemporaryFile(Url, 'helper-' + HelperField(Row, 'id') + '.dl', HelperField(Row, 'sha256'), nil);
  except
    Log('Helper download failed: ' + GetExceptionMessage);
    if not WizardSilent then
      MsgBox('The download of ' + Name + ' did not match its checksum or failed. Download it yourself from ' + HelperField(Row, 'page'), mbError, MB_OK);
    Exit;
  end;
  Tmp := ExpandConstant('{tmp}\helper-' + HelperField(Row, 'id') + '.dl');
  ForceDirectories(Dest);
  if Archive = 'zip' then
  begin
    Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath ''' + Tmp + ''' -DestinationPath ''' + Dest + ''' -Force"',
      '', SW_HIDE, ewWaitUntilTerminated, Rc);
  end
  else
    CopyFile(Tmp, Dest + '\' + UrlFileName(Url), False);
  if HelperPresent(Row) then Log('Helper installed: ' + Name)
  else if not WizardSilent then
    MsgBox(Name + ' was downloaded but not found afterwards. Download it yourself from ' + HelperField(Row, 'page'), mbError, MB_OK);
end;

{ ---- update support (contract 16) ---- }

var
  UpgradeInPlace: Boolean;

{ SemVer 2.0 precedence (16.4): -1, 0 or 1. Build metadata is ignored. }
function NextPart(var S: String): String;
var
  P: Integer;
begin
  P := Pos('.', S);
  if P = 0 then begin Result := S; S := ''; end
  else begin Result := Copy(S, 1, P - 1); S := Copy(S, P + 1, Length(S)); end;
end;

function IsNum(const S: String): Boolean;
var
  I: Integer;
begin
  Result := S <> '';
  for I := 1 to Length(S) do
    if (S[I] < '0') or (S[I] > '9') then Result := False;
end;

function CmpStr(const A, B: String): Integer;
begin
  if A < B then Result := -1 else if A > B then Result := 1 else Result := 0;
end;

function CmpInt(A, B: Integer): Integer;
begin
  if A < B then Result := -1 else if A > B then Result := 1 else Result := 0;
end;

function SemVerCompare(V1, V2: String): Integer;
var
  P, I: Integer;
  Core1, Core2, Pre1, Pre2, A, B: String;
begin
  Result := 0;
  P := Pos('+', V1); if P > 0 then V1 := Copy(V1, 1, P - 1);
  P := Pos('+', V2); if P > 0 then V2 := Copy(V2, 1, P - 1);
  P := Pos('-', V1); if P > 0 then begin Core1 := Copy(V1, 1, P - 1); Pre1 := Copy(V1, P + 1, Length(V1)); end else begin Core1 := V1; Pre1 := ''; end;
  P := Pos('-', V2); if P > 0 then begin Core2 := Copy(V2, 1, P - 1); Pre2 := Copy(V2, P + 1, Length(V2)); end else begin Core2 := V2; Pre2 := ''; end;
  for I := 1 to 3 do
  begin
    Result := CmpInt(StrToIntDef(NextPart(Core1), 0), StrToIntDef(NextPart(Core2), 0));
    if Result <> 0 then Exit;
  end;
  if (Pre1 = '') and (Pre2 = '') then Exit;
  if Pre1 = '' then begin Result := 1; Exit; end;
  if Pre2 = '' then begin Result := -1; Exit; end;
  while (Pre1 <> '') and (Pre2 <> '') do
  begin
    A := NextPart(Pre1); B := NextPart(Pre2);
    if IsNum(A) and IsNum(B) then Result := CmpInt(StrToIntDef(A, 0), StrToIntDef(B, 0))
    else if IsNum(A) then Result := -1
    else if IsNum(B) then Result := 1
    else Result := CmpStr(A, B);
    if Result <> 0 then Exit;
  end;
  if Pre1 <> '' then Result := 1 else if Pre2 <> '' then Result := -1;
end;

{ Read "version" from an installed module.json without a JSON parser. }
function InstalledModuleVersion(const Id: String): String;
var
  Raw: AnsiString;
  S, Rest: String;
  P, E: Integer;
begin
  Result := '';
  if not LoadStringFromFile(ExpandConstant('{app}\modules\' + Id + '\module.json'), Raw) then Exit;
  S := String(Raw);
  P := Pos('"version"', S);
  if P = 0 then Exit;
  Rest := Copy(S, P + 9, Length(S));
  P := Pos('"', Rest);
  if P = 0 then Exit;
  Rest := Copy(Rest, P + 1, Length(Rest));
  E := Pos('"', Rest);
  if E = 0 then Exit;
  Result := Copy(Rest, 1, E - 1);
end;

{ Files Check: copy the payload unless the installed module is newer (the shell updated it). }
function PayloadNotOlder(const Id, PayloadVersion: String): Boolean;
var
  Inst: String;
begin
  Result := True;
  Inst := InstalledModuleVersion(Id);
  if (Inst <> '') and (PayloadVersion <> '') and (SemVerCompare(Inst, PayloadVersion) > 0) then
  begin
    Log('Module ' + Id + ': installed ' + Inst + ' is newer than payload ' + PayloadVersion + '; kept.');
    Result := False;
  end;
end;

function RestartShellAfterSilentUpgrade: Boolean;
begin
  Result := WizardSilent and UpgradeInPlace;
end;

{ A silent self-update starts while the old shell is still closing: wait up to 20 s for its mutex. }
function InitializeSetup: Boolean;
var
  I: Integer;
begin
  Result := True;
  I := 0;
  while CheckForMutexes('Local\DeskKit.Shell') and (I < 40) do
  begin
    Sleep(500);
    I := I + 1;
  end;
end;

{ ---- install flow ---- }

procedure CurStepChanged(CurStep: TSetupStep);
var
  I: Integer;
begin
  if CurStep = ssInstall then
    UpgradeInPlace := FileExists(ExpandConstant('{app}\DeskKit.exe'));
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('snapauto') then SnapOff;
    if WizardIsTaskSelected('helpers') then
      for I := 0 to HelperRowCount - 1 do DownloadHelper(I);
  end;
end;

{ ---- uninstall flow ---- }

{ Stops only processes that run from the install dir. A separate Region Control install is never touched. }
procedure StopInstalledProcesses;
var
  Rc: Integer;
  Ps: String;
begin
  Ps := '-NoProfile -ExecutionPolicy Bypass -Command "Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith(''' +
        ExpandConstant('{app}') + '\'', [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force"';
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Ps, '', SW_HIDE, ewWaitUntilTerminated, Rc);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopInstalledProcesses;
    { Restore Windows snapping only if this install held Region Control. }
    if DirExists(ExpandConstant('{app}\modules\regioncontrol')) then SnapRestore;
  end;
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
    MsgBox('DeskKit is removed. Your settings and data are kept:' + #13#10 +
           ExpandConstant('{userappdata}\DeskKit') + #13#10 +
           ExpandConstant('{localappdata}\DeskKit') + #13#10#13#10 +
           'Delete those folders yourself if you do not want them.', mbInformation, MB_OK);
end;

{ Finish page: steps for choice B (Region Control). }
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and WizardIsTaskSelected('snapmanual') then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'One step left for Snap Layouts: turn off Windows'' own snapping.' + #13#10 +
      '1. Open Settings > System > Multitasking.' + #13#10 +
      '2. Turn off "Snap windows".' + #13#10 +
      'This also turns off Win+Arrow. Snap Layouts cannot work with the Windows snap bar on.';
end;
