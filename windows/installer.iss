; Builds MeteorDownloadManager-Setup-<version>.exe (per-user install, no admin rights needed).
#define Version GetEnv("NDM_VERSION")
[Setup]
AppId={{3C2B9F17-47B0-4C5A-B616-7B5648490D26}
AppName=Meteor Download Manager
AppVersion={#Version}
AppPublisher=Meteor Download Manager contributors
AppPublisherURL=https://github.com/sbolhasani/meteor-download-manager
AppSupportURL=https://github.com/sbolhasani/meteor-download-manager/issues
AppUpdatesURL=https://github.com/sbolhasani/meteor-download-manager/releases/latest
AppCopyright=Copyright (c) 2026 original authors and Meteor contributors. MIT OR Apache-2.0.
LicenseFile=..\packaging\TERMS.txt
DefaultDirName={localappdata}\Programs\Meteor Download Manager
DefaultGroupName=Meteor Download Manager
PrivilegesRequired=lowest
OutputDir=..\build
OutputBaseFilename=MeteorDownloadManager-Setup-{#Version}
SetupIconFile=App\app.ico
UninstallDisplayIcon={app}\MeteorDownloadManager.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ChangesEnvironment=yes

[InstallDelete]
; The one-file video engine of versions before 0.18.0 (now the yt-dlp folder).
Type: files; Name: "{app}\yt-dlp.exe"
Type: filesandordirs; Name: "{app}\yt-dlp"

[Files]
Source: "..\build\win\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Meteor Download Manager"; Filename: "{app}\MeteorDownloadManager.exe"
Name: "{userdesktop}\Meteor Download Manager"; Filename: "{app}\MeteorDownloadManager.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"
Name: "addtopath"; Description: "Add the ndm command to the command line"

[Registry]
; Open .torrent files and magnet links with the app. Another program's existing handler is never replaced: then the app
; only appears under "Open with".
Root: HKCU; Subkey: "Software\Classes\MeteorDownloadManager.torrent"; ValueType: string; ValueData: "BitTorrent file"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\MeteorDownloadManager.torrent\DefaultIcon"; ValueType: string; ValueData: "{app}\MeteorDownloadManager.exe,0"
Root: HKCU; Subkey: "Software\Classes\MeteorDownloadManager.torrent\shell\open\command"; ValueType: string; ValueData: """{app}\MeteorDownloadManager.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.torrent\OpenWithProgids"; ValueType: string; ValueName: "MeteorDownloadManager.torrent"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.torrent"; ValueType: string; ValueData: "MeteorDownloadManager.torrent"; Check: TorrentFree; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\magnet"; ValueType: string; ValueData: "URL:Magnet link"; Check: MagnetFree; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\magnet"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Check: MagnetFree
Root: HKCU; Subkey: "Software\Classes\magnet\shell\open\command"; ValueType: string; ValueData: """{app}\MeteorDownloadManager.exe"" ""%1"""; Check: MagnetFree
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Code]
var
  TorrentIsFree, MagnetIsFree: Boolean;

{ Decided once before anything is written: free when nothing handles it yet, or when it is already this app. }
function InitializeSetup(): Boolean;
var
  S: string;
begin
  TorrentIsFree := True;
  if RegQueryStringValue(HKEY_CLASSES_ROOT, '.torrent', '', S) then
    TorrentIsFree := (S = '') or (CompareText(S, 'MeteorDownloadManager.torrent') = 0);
  MagnetIsFree := True;
  if RegQueryStringValue(HKEY_CLASSES_ROOT, 'magnet\shell\open\command', '', S) then
    MagnetIsFree := (S = '') or (Pos('meteordownloadmanager', Lowercase(S)) > 0);
  Result := True;
end;

function TorrentFree(): Boolean;
begin
  Result := TorrentIsFree;
end;

function MagnetFree(): Boolean;
begin
  Result := MagnetIsFree;
end;

function NeedsAddPath(Dir: string): Boolean;
var
  Path: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Path) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';') = 0;
end;

[Run]
Filename: "{app}\MeteorDownloadManager.exe"; Description: "Start Meteor Download Manager"; Flags: nowait postinstall skipifsilent
; In-app updates run the setup quietly: open the new version again afterwards.
Filename: "{app}\MeteorDownloadManager.exe"; Flags: nowait runasoriginaluser; Check: WizardSilent

[Messages]
FinishedLabel=Thank you for installing Meteor Download Manager!%n%nProject: github.com/sbolhasani/meteor-download-manager
