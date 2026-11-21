; ToDooo per-user installer (Inno Setup 6).
; Build: ISCC.exe /DAppVersion=1.5.0 /DSourceDir=<absolute publish dir> installer\ToDooo.iss
; Installs into %LocalAppData%\Programs\ToDooo, so no UAC prompt.
; User data lives in %LocalAppData%\ToDooo and is intentionally preserved on uninstall.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\out\publish"
#endif

[Setup]
AppId={{B7E4A1C2-3F5D-4B8E-9A61-7C2D4E9F1B03}
AppName=ToDooo
AppVersion={#AppVersion}
AppVerName=ToDooo {#AppVersion}
AppPublisher=yyt
AppPublisherURL=https://github.com/yeyt97/ToDooo
AppSupportURL=https://github.com/yeyt97/ToDooo/issues
AppUpdatesURL=https://github.com/yeyt97/ToDooo/releases
DefaultDirName={localappdata}\Programs\ToDooo
DefaultGroupName=ToDooo
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\publish
OutputBaseFilename=ToDooo-{#AppVersion}-setup
SetupIconFile=..\src\ToDooo.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\ToDooo.exe
UninstallDisplayName=ToDooo
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ToDooo"; Filename: "{app}\ToDooo.exe"
Name: "{autodesktop}\ToDooo"; Filename: "{app}\ToDooo.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ToDooo.exe"; Description: "{cm:LaunchProgram,ToDooo}"; Flags: nowait postinstall skipifsilent

; No [UninstallDelete] entries: %LocalAppData%\ToDooo (todo.db, settings.json, backups) is kept on uninstall.