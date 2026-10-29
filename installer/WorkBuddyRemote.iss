; WorkBuddy Remote installer (Inno Setup 6)
;
; Build with:
;   ISCC /DAppVersion=2.0.0 /DPayload=..\build\selfcontained /DFlavorSuffix=""                installer\WorkBuddyRemote.iss
;   ISCC /DAppVersion=2.0.0 /DPayload=..\build\slim           /DFlavorSuffix="slim-" /DCheckRuntime=1 installer\WorkBuddyRemote.iss
;
; IMPORTANT: keep this file UTF-8 WITH BOM. It carries Chinese UI strings; without the BOM
; the compiler reads them as ANSI and the Chinese text turns into mojibake.

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
; 默认跟随系统语言，并且始终显示语言选择（简体中文 / English）
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=yes

[Languages]
Name: "zh"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
zh.TaskGroup=附加图标与启动项
zh.TaskDesktopIcon=创建桌面快捷方式
zh.TaskStartup=登录 Windows 时自动启动（静默在托盘运行）
zh.LaunchApp=立即启动 {#MyAppName}
zh.RuntimeMissing=这是精简版安装包，需要先安装 .NET 10 桌面运行时。%n%n下载地址：https://dotnet.microsoft.com/download/dotnet/10.0%n%n安装会继续，但缺少运行时程序无法启动。%n不需要安装运行时的话请使用自包含安装包。

en.TaskGroup=Additional icons and startup
en.TaskDesktopIcon=Create a desktop shortcut
en.TaskStartup=Start WorkBuddy Remote when I sign in (silent, in the tray)
en.LaunchApp=Launch {#MyAppName}
en.RuntimeMissing=This is the slim build and needs the .NET 10 Desktop Runtime.%n%nDownload: https://dotnet.microsoft.com/download/dotnet/10.0%n%nSetup will continue, but the app will not start without it.%nUse the self-contained installer if you do not want to install runtimes.

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktopIcon}"; GroupDescription: "{cm:TaskGroup}"
Name: "startup"; Description: "{cm:TaskStartup}"; GroupDescription: "{cm:TaskGroup}"; Flags: unchecked

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
; 注意：{userstartup} 下的文件名不要带 .lnk，Inno 会自动补扩展名
Name: "{userstartup}\WorkBuddyRemote"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--autostart"; WorkingDir: "{app}"; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{userstartup}\WorkBuddyRemote.lnk"

[Code]
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

procedure InitializeWizard();
begin
  if not HasDotNet10Desktop() then
    MsgBox(ExpandConstant('{cm:RuntimeMissing}'), mbInformation, MB_OK);
end;
#endif
