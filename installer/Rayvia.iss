#ifndef MyAppVersion
  #define MyAppVersion "0.4.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

#define MyAppName "Rayvia"
#define MyAppPublisher "Rayvia"
#define MyAppExeName "Rayvia.exe"

[Setup]
AppId={{8A7D804B-5AF6-4DD7-99E2-2672D6A7C910}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Rayvia
DefaultGroupName=Rayvia
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=Rayvia-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\Rayvia\Assets\rayvia.ico
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Rayvia"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Rayvia"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить Rayvia"; Flags: nowait postinstall skipifsilent
