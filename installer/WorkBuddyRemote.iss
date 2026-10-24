; WorkBuddy Remote installer (Inno Setup 6)
;
; Build with:
;   ISCC /DAppVersion=2.0.0 /DPayload=..\build\selfcontained /DFlavorSuffix=""                installer\WorkBuddyRemote.iss
;   ISCC /DAppVersion=2.0.0 /DPayload=..\build\slim           /DFlavorSuffix="slim-" /DCheckRuntime=1 installer\WorkBuddyRemote.iss
;
; IMPORTANT: keep this file UTF-8 WITH BOM. It contains Chinese UI strings; without the BOM
; the compiler reads them as ANSI and every Chinese message becomes mojibake.

#define MyAppName "WorkBuddy Remote"
#define MyAppExeName "WorkBuddyRemoteWinUI.exe"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef Payload
  #define Payload "..\build\selfcontained"
#endif

#ifndef FlavorSuffix
  #define FlavorSuffix ""
#endif

; "1" for the slim (framework-dependent) build, "0" for the self-contained one
#ifndef CheckRuntime
  #define CheckRuntime "0"
#endif

[Setup]
AppId={{8E2C0B52-5C4B-4E4E-9A9B-2F4C1C7D6A10}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher=Panda35064
AppPublisherURL=https://github.com/Panda35064/workbuddy-remote-winui3
AppSupportURL=https://github.com/Panda35064/workbuddy-remote-winui3/issues
AppUpdatesURL=https://github.com/Panda35064/workbuddy-remote-winui3/releases
DefaultDirName={localappdata}\Programs\WorkBuddyRemote
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=WorkBuddyRemote-{#AppVersion}-{#FlavorSuffix}setup
SetupIconFile=..\apps\console\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
LanguageDetectionMethod=uilanguage
; 始终显示语言选择（默认选中系统语言），中英文都能手动切换
ShowLanguageDialog=yes

[Languages]
Name: "zh"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
; ---------- Chinese (Simplified) ----------
zh.OptionsPageCaption=桥接设置
zh.OptionsPageDesc=调整桥接参数
zh.OptionsPageSubCaption=这些设置会在第一次启动时写入配置文件，之后可以在控制台的“设置”里修改。
zh.ScopeCaption=访问范围
zh.ScopeLocal=仅本机（127.0.0.1）
zh.ScopeLan=局域网 / 端口映射（需要访问密码）
zh.ParamsPageCaption=端口与密码
zh.ParamsPageDesc=桥接端口与访问密码
zh.ParamsPageSubCaption=端口冲突时桥接会自动顺延，这里填首选端口即可。
zh.BridgePortCaption=桥接端口
zh.CdpPortCaption=CDP 端口
zh.PasswordCaption=访问密码
zh.PasswordConfirmCaption=确认密码
zh.TaskGroup=附加图标与启动项
zh.TaskDesktopIcon=创建桌面快捷方式
zh.TaskStartup=登录 Windows 时自动启动（静默在托盘运行）
zh.LaunchApp=立即启动 {#MyAppName}
zh.ErrPortRange=端口必须是 1 到 65535 之间的整数。
zh.ErrPortSame=桥接端口和 CDP 端口不能相同。
zh.ErrPasswordRequired=选择局域网访问时必须设置访问密码。
zh.ErrPasswordMismatch=两次输入的密码不一致。
zh.ErrPasswordShort=密码至少 4 位。
zh.RuntimeMissing=这是精简版安装包，需要先安装 .NET 10 桌面运行时。%n%n下载地址：https://dotnet.microsoft.com/download/dotnet/10.0%n%n安装会继续，但缺少运行时程序无法启动。%n不需要安装运行时的话请使用自包含安装包。

; ---------- English ----------
en.OptionsPageCaption=Bridge settings
en.OptionsPageDesc=Tune the bridge
en.OptionsPageSubCaption=These values are written to the config file on first launch; you can change them later in the console settings.
en.ScopeCaption=Access scope
en.ScopeLocal=This computer only (127.0.0.1)
en.ScopeLan=LAN / port mapping (requires a password)
en.ParamsPageCaption=Ports and password
en.ParamsPageDesc=Bridge port and access password
en.ParamsPageSubCaption=If a port is busy the bridge picks the next free one; just enter your preferred port.
en.BridgePortCaption=Bridge port
en.CdpPortCaption=CDP port
en.PasswordCaption=Access password
en.PasswordConfirmCaption=Confirm password
en.TaskGroup=Additional icons and startup
en.TaskDesktopIcon=Create a desktop shortcut
en.TaskStartup=Start automatically when I sign in (silent, in the tray)
en.LaunchApp=Launch {#MyAppName}
en.ErrPortRange=Ports must be integers between 1 and 65535.
en.ErrPortSame=The bridge port and the CDP port must be different.
en.ErrPasswordRequired=LAN access requires an access password.
en.ErrPasswordMismatch=The two passwords do not match.
en.ErrPasswordShort=The password must be at least 4 characters.
en.RuntimeMissing=This is the slim package and needs the .NET 10 Desktop Runtime.%n%nDownload: https://dotnet.microsoft.com/download/dotnet/10.0%n%nSetup will continue, but the app will not start without it.%nUse the self-contained installer if you do not want to install runtimes.

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktopIcon}"; GroupDescription: "{cm:TaskGroup}"
Name: "startup"; Description: "{cm:TaskStartup}"; GroupDescription: "{cm:TaskGroup}"; Flags: unchecked

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userstartup}\WorkBuddyRemote.lnk"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--autostart"; WorkingDir: "{app}"; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{userstartup}\WorkBuddyRemote.lnk"

[Code]
var
  ScopePage: TInputOptionWizardPage;
  ParamsPage: TInputQueryWizardPage;

const
  OverridesName = 'install-overrides.json';
  HexDigits = '0123456789ABCDEF';

function ConfigDir(): string;
begin
  Result := ExpandConstant('{userappdata}\WorkBuddyRemote');
end;

function ConfigPath(): string;
begin
  Result := ConfigDir() + '\config.json';
end;

function OverridesPath(): string;
begin
  Result := ConfigDir() + '\' + OverridesName;
end;

{ Raw scalar (unquoted for strings) of "Key": value in a tiny JSON document. }
function ReadJsonScalar(const Json, Key: string): string;
var
  P, I, L: Integer;
  Rest: string;
  Ch: Char;
begin
  Result := '';
  P := Pos('"' + Key + '"', Json);
  if P = 0 then
    Exit;

  Rest := Copy(Json, P + Length(Key) + 2, MaxInt);
  P := Pos(':', Rest);
  if P = 0 then
    Exit;
  Rest := Copy(Rest, P + 1, MaxInt);

  I := 1;
  L := Length(Rest);
  while (I <= L) and ((Rest[I] = ' ') or (Rest[I] = #9)) do
    I := I + 1;

  if (I <= L) and (Rest[I] = '"') then
  begin
    I := I + 1;
    while (I <= L) and (Rest[I] <> '"') do
    begin
      Result := Result + Rest[I];
      I := I + 1;
    end;
    Exit;
  end;

  while I <= L do
  begin
    Ch := Rest[I];
    if (Ch = ',') or (Ch = '}') or (Ch = #13) or (Ch = #10) or (Ch = ' ') then
      Break;
    Result := Result + Ch;
    I := I + 1;
  end;
  Result := Trim(Result);
end;

{ UTF-16 code units as hex, so non-ASCII passwords survive the ANSI file write. }
function ToHex4(V: Integer): string;
var
  I, D: Integer;
begin
  Result := '';
  for I := 3 downto 0 do
  begin
    D := (V shr (I * 4)) and $F;
    Result := Result + HexDigits[D + 1];
  end;
end;

function Utf16Hex(const S: string): string;
var
  I: Integer;
begin
  Result := '';
  for I := 1 to Length(S) do
    Result := Result + ToHex4(Ord(S[I]));
end;

function TryPort(const S: string; var Port: Integer): Boolean;
var
  V: Integer;
begin
  V := StrToIntDef(Trim(S), -1);
  Result := (V >= 1) and (V <= 65535);
  if Result then
    Port := V;
end;

#if CheckRuntime == "1"
function HasDotNet10Desktop(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then
  try
    Result := (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0;
  finally
    FindClose(FindRec);
  end;
end;
#endif

procedure CreatePages();
var
  Json: string;
  Raw: AnsiString;
  Value: string;
  Port: Integer;
begin
  ScopePage := CreateInputOptionPage(wpSelectTasks,
    ExpandConstant('{cm:OptionsPageCaption}'),
    ExpandConstant('{cm:OptionsPageDesc}'),
    ExpandConstant('{cm:OptionsPageSubCaption}'),
    True, False);
  ScopePage.Add(ExpandConstant('{cm:ScopeLocal}'));
  ScopePage.Add(ExpandConstant('{cm:ScopeLan}'));
  ScopePage.Values[0] := True;

  ParamsPage := CreateInputQueryPage(ScopePage.ID,
    ExpandConstant('{cm:ParamsPageCaption}'),
    ExpandConstant('{cm:ParamsPageDesc}'),
    ExpandConstant('{cm:ParamsPageSubCaption}'));
  ParamsPage.Add(ExpandConstant('{cm:BridgePortCaption}') + ':', False);
  ParamsPage.Add(ExpandConstant('{cm:CdpPortCaption}') + ':', False);
  ParamsPage.Add(ExpandConstant('{cm:PasswordCaption}') + ':', True);
  ParamsPage.Add(ExpandConstant('{cm:PasswordConfirmCaption}') + ':', True);
  ParamsPage.Edits[0].Text := '8780';
  ParamsPage.Edits[1].Text := '9333';
  ParamsPage.Edits[2].Password := True;
  ParamsPage.Edits[3].Password := True;

  { Prefill from an existing config so upgrades do not silently reset ports. }
  if FileExists(ConfigPath()) then
  begin
    if LoadStringFromFile(ConfigPath(), Raw) then
    begin
      Json := Raw;
      Value := ReadJsonScalar(Json, 'bridgePort');
      if TryPort(Value, Port) then
        ParamsPage.Edits[0].Text := IntToStr(Port);
      Value := ReadJsonScalar(Json, 'cdpPort');
      if TryPort(Value, Port) then
        ParamsPage.Edits[1].Text := IntToStr(Port);
      Value := ReadJsonScalar(Json, 'listenHost');
      if (Value = '0.0.0.0') or (Value = '::') then
      begin
        ScopePage.Values[0] := False;
        ScopePage.Values[1] := True;
      end;
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  BridgePort, CdpPort: Integer;
  Password: string;
begin
  Result := True;
  if (ParamsPage = nil) or (CurPageID <> ParamsPage.ID) then
    Exit;

  if not TryPort(ParamsPage.Edits[0].Text, BridgePort) then
  begin
    MsgBox(ExpandConstant('{cm:ErrPortRange}'), mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if not TryPort(ParamsPage.Edits[1].Text, CdpPort) then
  begin
    MsgBox(ExpandConstant('{cm:ErrPortRange}'), mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if BridgePort = CdpPort then
  begin
    MsgBox(ExpandConstant('{cm:ErrPortSame}'), mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if ScopePage.Values[1] then
  begin
    Password := ParamsPage.Edits[2].Text;
    if Password = '' then
    begin
      MsgBox(ExpandConstant('{cm:ErrPasswordRequired}'), mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Length(Password) < 4 then
    begin
      MsgBox(ExpandConstant('{cm:ErrPasswordShort}'), mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Password <> ParamsPage.Edits[3].Text then
    begin
      MsgBox(ExpandConstant('{cm:ErrPasswordMismatch}'), mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

procedure WriteOverrides();
var
  Json, PasswordHex, ListenHost: string;
  Raw: AnsiString;
  BridgePort, CdpPort: Integer;
begin
  if not TryPort(ParamsPage.Edits[0].Text, BridgePort) then
    BridgePort := 8780;
  if not TryPort(ParamsPage.Edits[1].Text, CdpPort) then
    CdpPort := 9333;

  if ScopePage.Values[1] then
    ListenHost := '0.0.0.0'
  else
    ListenHost := '127.0.0.1';

  Json := '{' + #13#10 +
    '  "bridgePort": ' + IntToStr(BridgePort) + ',' + #13#10 +
    '  "cdpPort": ' + IntToStr(CdpPort) + ',' + #13#10 +
    '  "listenHost": "' + ListenHost + '"';

  PasswordHex := '';
  if ParamsPage.Edits[2].Text <> '' then
    PasswordHex := Utf16Hex(ParamsPage.Edits[2].Text);
  if PasswordHex <> '' then
    Json := Json + ',' + #13#10 + '  "passwordUtf16Hex": "' + PasswordHex + '"';

  Json := Json + #13#10 + '}' + #13#10;

  ForceDirectories(ConfigDir());
  Raw := Json;
  SaveStringToFile(OverridesPath(), Raw, False);
end;

procedure InitializeWizard();
begin
  CreatePages();

#if CheckRuntime == "1"
  if not HasDotNet10Desktop() then
    MsgBox(ExpandConstant('{cm:RuntimeMissing}'), mbInformation, MB_OK);
#endif
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;

  { A silent install must not clobber an existing user config. }
  if WizardSilent and FileExists(ConfigPath()) then
    Exit;

  if ParamsPage <> nil then
    WriteOverrides();
end;
