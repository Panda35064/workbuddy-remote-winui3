; WorkBuddy Remote installer (Inno Setup 6)
; Build with:
;   ISCC /DAppVersion=2.0.0 /DPayload=..\build\selfcontained /DFlavorSuffix="" installer\WorkBuddyRemote.iss
;   ISCC /DAppVersion=2.0.0 /DPayload=..\build\slim           /DFlavorSuffix="slim-" installer\WorkBuddyRemote.iss
; Keep this file pure ASCII: the build machine may use a non-UTF8 code page.

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

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"
Name: "startup"; Description: "Start WorkBuddy Remote when I sign in"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userstartup}\WorkBuddyRemote.lnk"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--autostart"; WorkingDir: "{app}"; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

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
  begin
    MsgBox('This is the slim build and needs the .NET 10 Desktop Runtime.' + #13#10 +
           'Download: https://dotnet.microsoft.com/download/dotnet/10.0' + #13#10 + #13#10 +
           'Setup will continue, but the app will not start without it.' + #13#10 +
           'Use the self-contained installer if you do not want to install runtimes.',
           mbInformation, MB_OK);
  end;
end;
#endif
