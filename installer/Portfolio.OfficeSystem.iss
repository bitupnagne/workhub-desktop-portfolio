; ============================================================
; WorkHub Desktop 인스톨러 (Inno Setup 6 스크립트)
;  - .NET 10 framework-dependent 본체와 self-contained 업데이터를 패키징
;  - 시작 메뉴 / 바탕화면 바로가기 + 제거 프로그램 등록 자동
;  - 빌드: ..\installer\build-installer.ps1
; ============================================================

#define MyAppName        "WorkHub Desktop"
#define MyAppExeName     "Portfolio.OfficeSystem.exe"
#ifndef MyAppVersion
  #define MyAppVersion   "1.0.27"
#endif
#define MyAppPublisher   "Office Suite"
#define MyAppId          "Portfolio.OfficeSystem"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Portfolio.OfficeSystem
DefaultGroupName=WorkHub Desktop
UninstallDisplayIcon={app}\{#MyAppExeName}
DisableProgramGroupPage=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/ultra
SolidCompression=yes
OutputDir=..\publish\release
OutputBaseFilename=Portfolio.OfficeSystem-Setup-{#MyAppVersion}
SetupIconFile=..\App.ico
CloseApplications=force
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면 바로가기 만들기"; GroupDescription: "추가 작업:"; Flags: checkedonce
Name: "startupicon"; Description: "Windows 시작 시 자동 실행"; GroupDescription: "추가 작업:"; Flags: unchecked

[Files]
; publish 폴더 전체를 그대로 복사
Source: "..\publish\installer-payload\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Dirs]
Name: "{commonappdata}\Portfolio.OfficeSystem\Updates"; Permissions: users-modify

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser; Check: not IsAutoUpdate
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser; Check: IsAutoUpdate

[UninstallDelete]
; 설치 본체 빌드 산출물
Type: filesandordirs; Name: "{app}\bin"
Type: filesandordirs; Name: "{app}\runtimes"
; WebView2 캐시 (LOCALAPPDATA — 새 경로) + 옛 위치 (Program Files 안) 양쪽 모두 정리
Type: filesandordirs; Name: "{localappdata}\Portfolio.OfficeSystem\WebView2"
Type: filesandordirs; Name: "{app}\Portfolio.OfficeSystem.exe.WebView2"
; 사용자 설정 (자동 로그인 토큰, 서버 주소, 다크모드, 디버그 로그)
;  → 제거 시 자동 로그인이 풀려야 "완전 제거"라는 사용자 기대에 부합
Type: filesandordirs; Name: "{userappdata}\Portfolio.OfficeSystem"
Type: filesandordirs; Name: "{localappdata}\Portfolio.OfficeSystem"
Type: filesandordirs; Name: "{commonappdata}\Portfolio.OfficeSystem"

[Code]
var
  ServerUrlPage: TInputQueryWizardPage;

// [T-2] 설치 시작 시 동일 AppId의 기존 설치본을 감지하면 자동으로 silent uninstall 후 진행한다.
// HKLM/HKCU의 Uninstall 키에서 QuietUninstallString을 찾아 실행.
// 사용자에게 한 번 확인을 받은 후 진행.
function GetUninstallString(): String;
var
  sUnInstPath: String;
  sUnInstallString: String;
begin
  // UninstallString만 사용 (QuietUninstallString은 args 포함되어 RemoveQuotes 후에도 깨끗하지 않음)
  sUnInstPath := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + '{#MyAppId}' + '_is1';
  sUnInstallString := '';
  if not RegQueryStringValue(HKLM, sUnInstPath, 'UninstallString', sUnInstallString) then
    RegQueryStringValue(HKCU, sUnInstPath, 'UninstallString', sUnInstallString);
  Result := sUnInstallString;
end;

function IsAppInstalled(): Boolean;
begin
  Result := GetUninstallString() <> '';
end;

function IsAutoUpdate(): Boolean;
begin
  Result := Pos('/AUTOUPDATE', Uppercase(GetCmdTail)) > 0;
end;

function NormalizeServerUrl(Url: String): String;
begin
  Result := Trim(Url);
  while (Length(Result) > 0) and (Copy(Result, Length(Result), 1) = '/') do
    Delete(Result, Length(Result), 1);
end;

function IsValidServerUrl(Url: String): Boolean;
var
  normalized: String;
begin
  normalized := NormalizeServerUrl(Url);
  Result := ((Pos('http://', Lowercase(normalized)) = 1) or
             (Pos('https://', Lowercase(normalized)) = 1)) and
            (Length(normalized) > Length('http://'));
end;

function GetInitialServerUrl(): String;
begin
  Result := NormalizeServerUrl(ExpandConstant('{param:SERVERURL|http://localhost:18080}'));
end;

function GetSelectedServerUrl(): String;
begin
  if IsAutoUpdate() then
    Result := ''
  else if Assigned(ServerUrlPage) then
    Result := NormalizeServerUrl(ServerUrlPage.Values[0])
  else
    Result := GetInitialServerUrl();
end;

function JsonEscape(Value: String): String;
begin
  StringChangeEx(Value, '\', '\\', True);
  StringChangeEx(Value, '"', '\"', True);
  Result := Value;
end;

procedure WriteInitialSettings();
var
  targetDir: String;
  target: String;
  serverUrl: String;
  json: String;
begin
  if IsAutoUpdate() then Exit;

  serverUrl := GetSelectedServerUrl();
  if serverUrl = '' then Exit;

  targetDir := ExpandConstant('{userappdata}\Portfolio.OfficeSystem');
  target := targetDir + '\settings.json';
  ForceDirectories(targetDir);

  json := '{' + #13#10 +
          '  "ServerUrl": "' + JsonEscape(serverUrl) + '",' + #13#10 +
          '  "Username": null,' + #13#10 +
          '  "RememberLogin": true,' + #13#10 +
          '  "ThemeMode": "Light",' + #13#10 +
          '  "EncryptedToken": null' + #13#10 +
          '}';
  SaveStringToFile(target, json, False);
end;

procedure KillRunningApp(); forward;
procedure WipeRegistry(); forward;
procedure DelTreeWithRetry(const Path: String; MaxTries: Integer); forward;

procedure ForceWipeUserData();
var
  findRec: TFindRec;
  usersRoot: String;
  profileDir: String;
begin
  // [1.0.1] 완전 제거: 프로세스 강제 종료 → 모든 사용자 프로필 AppData 청소 (재시도 포함) → 레지스트리.
  KillRunningApp();

  DelTreeWithRetry(ExpandConstant('{userappdata}\Portfolio.OfficeSystem'), 20);
  DelTreeWithRetry(ExpandConstant('{localappdata}\Portfolio.OfficeSystem'), 20);
  DelTreeWithRetry(ExpandConstant('{commonappdata}\Portfolio.OfficeSystem'), 20);

  if GetEnv('APPDATA') <> '' then
    DelTreeWithRetry(GetEnv('APPDATA') + '\Portfolio.OfficeSystem', 20);
  if GetEnv('LOCALAPPDATA') <> '' then
    DelTreeWithRetry(GetEnv('LOCALAPPDATA') + '\Portfolio.OfficeSystem', 20);
  if GetEnv('USERPROFILE') <> '' then
  begin
    DelTreeWithRetry(GetEnv('USERPROFILE') + '\AppData\Roaming\Portfolio.OfficeSystem', 20);
    DelTreeWithRetry(GetEnv('USERPROFILE') + '\AppData\Local\Portfolio.OfficeSystem', 20);
  end;

  usersRoot := ExpandConstant('{sd}\Users');
  if DirExists(usersRoot) and FindFirst(usersRoot + '\*', findRec) then
  begin
    try
      repeat
        if (findRec.Name <> '.') and (findRec.Name <> '..') then
        begin
          profileDir := usersRoot + '\' + findRec.Name;
          if DirExists(profileDir + '\AppData') then
          begin
            DelTreeWithRetry(profileDir + '\AppData\Roaming\Portfolio.OfficeSystem', 10);
            DelTreeWithRetry(profileDir + '\AppData\Local\Portfolio.OfficeSystem', 10);
          end;
        end;
      until not FindNext(findRec);
    finally
      FindClose(findRec);
    end;
  end;

  WipeRegistry();
end;

procedure BackupUserSettings();
var
  source: String;
  target: String;
begin
  source := ExpandConstant('{userappdata}\Portfolio.OfficeSystem\settings.json');
  target := ExpandConstant('{tmp}\Portfolio.OfficeSystem-settings.json');
  if FileExists(source) then
    CopyFile(source, target, False);
end;

procedure RestoreUserSettings();
var
  source: String;
  targetDir: String;
  target: String;
begin
  source := ExpandConstant('{tmp}\Portfolio.OfficeSystem-settings.json');
  targetDir := ExpandConstant('{userappdata}\Portfolio.OfficeSystem');
  target := targetDir + '\settings.json';
  if FileExists(source) then
  begin
    ForceDirectories(targetDir);
    CopyFile(source, target, False);
  end;
end;

procedure UninstallExisting(PreserveUserData: Boolean);
var
  sUnInstallString: String;
  sAppDir: String;
  iResultCode: Integer;
  i: Integer;
begin
  if PreserveUserData then
    BackupUserSettings();

  sUnInstallString := GetUninstallString();
  if sUnInstallString = '' then Exit;
  sUnInstallString := RemoveQuotes(sUnInstallString);
  sAppDir := ExtractFilePath(sUnInstallString);
  Exec(sUnInstallString, '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES', '', SW_HIDE, ewWaitUntilTerminated, iResultCode);
  // Inno Setup uninstaller는 자기 자신을 %TEMP%로 복사 후 원본을 즉시 종료한다.
  // ewWaitUntilTerminated가 너무 빨리 리턴되므로 두 단계로 폴링한다:
  //  1) unins exe 파일이 삭제될 때까지 최대 30초
  //  2) 설치 폴더 전체가 사라질 때까지 추가 최대 60초 (백그라운드 자식 프로세스가 폴더 정리)
  for i := 1 to 60 do
  begin
    if not FileExists(sUnInstallString) then Break;
    Sleep(500);
  end;
  for i := 1 to 120 do
  begin
    if not DirExists(sAppDir) then Break;
    Sleep(500);
  end;
  if not PreserveUserData then
  begin
    // 폴더가 다 사라진 후 user data 까지 명시적으로 정리 (옛 unins 호환)
    ForceWipeUserData();
  end;
  if PreserveUserData then
    RestoreUserSettings();
end;

procedure KillRunningApp();
var
  resultCode: Integer;
begin
  // [1.0.1] uninstall 시 파일 잠금 방지 — 본체 + Updater + WebView2 자식 까지 강제 종료.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#MyAppExeName} /F /T', '', SW_HIDE, ewWaitUntilTerminated, resultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Portfolio.OfficeSystem.Updater.exe /F /T', '', SW_HIDE, ewWaitUntilTerminated, resultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM msedgewebview2.exe /F /T', '', SW_HIDE, ewWaitUntilTerminated, resultCode);
  Sleep(1500);
end;

procedure WipeRegistry();
begin
  // [1.0.1] 알림 등록 / AppUserModelID / 기타 HKCU 흔적 정리.
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Classes\AppUserModelId\Portfolio.OfficeSystem.Desktop');
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Portfolio.OfficeSystem.Desktop');
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Portfolio.OfficeSystem');
  RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE, 'Software\Portfolio.OfficeSystem');
end;

procedure DelTreeWithRetry(const Path: String; MaxTries: Integer);
var
  i: Integer;
begin
  // 파일 잠금이 풀릴 때까지 폴링 — WebView2 / tray icon / Defender 스캔이 늦게 놓아주는 케이스.
  if not DirExists(Path) and not FileExists(Path) then Exit;
  for i := 1 to MaxTries do
  begin
    DelTree(Path, True, True, True);
    if not DirExists(Path) and not FileExists(Path) then Exit;
    Sleep(500);
  end;
end;

function InitializeSetup(): Boolean;
var
  choice: Integer;
begin
  Result := True;
  if IsAppInstalled() then
  begin
    if IsAutoUpdate() then
    begin
      KillRunningApp();
      UninstallExisting(True);
      Exit;
    end;

    choice := MsgBox('이미 ' + '{#MyAppName}' + '이(가) 설치되어 있습니다.' + #13#10 + #13#10 +
                     '[예]    기존 버전을 제거하고 새 버전을 설치합니다.' + #13#10 +
                     '[아니오] 기존 버전만 제거하고 종료합니다.' + #13#10 +
                     '[취소]  아무 작업도 하지 않습니다.',
                     mbConfirmation, MB_YESNOCANCEL);
    if choice = IDYES then
    begin
      KillRunningApp();
      UninstallExisting(False)
    end
    else if choice = IDNO then
    begin
      KillRunningApp();
      UninstallExisting(False);
      MsgBox('{#MyAppName} 이(가) 제거되었습니다.', mbInformation, MB_OK);
      Result := False;
    end
    else
      Result := False;
  end;
end;

procedure InitializeWizard();
begin
  if IsAutoUpdate() then Exit;

  ServerUrlPage := CreateInputQueryPage(
    wpSelectTasks,
    '서버 주소 설정',
    '앱이 접속할 서버 주소를 입력하세요.',
    '설치 완료 후 앱이 처음 실행될 때 이 주소로 업데이트 확인과 로그인을 진행합니다.');
  ServerUrlPage.Add('서버 주소:', False);
  ServerUrlPage.Values[0] := GetInitialServerUrl();
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  serverUrl: String;
begin
  Result := True;
  if Assigned(ServerUrlPage) and (CurPageID = ServerUrlPage.ID) then
  begin
    serverUrl := NormalizeServerUrl(ServerUrlPage.Values[0]);
    if not IsValidServerUrl(serverUrl) then
    begin
      MsgBox('서버 주소를 올바르게 입력해 주세요.' + #13#10 +
             '예: http://localhost:18080', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    ServerUrlPage.Values[0] := serverUrl;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteInitialSettings();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // [1.0.1] 파일 잠금 방지 — 파일 제거 단계 진입 전에 모든 프로세스 강제 종료.
  if CurUninstallStep = usUninstall then
    KillRunningApp();
  // 파일 제거 끝난 다음 사용자 데이터 + 레지스트리 + 다른 프로필까지 청소.
  if CurUninstallStep = usPostUninstall then
    ForceWipeUserData();
end;
