; Installer Nexus Streamer — Inno Setup 6
; Multilingua: la lingua scelta nel wizard viene scritta in Documenti\Nexus Streamer\app.json,
; che l'app legge al primo avvio (modificabile sempre da Impostazioni).

#define AppName "Nexus Streamer"
#define AppExe "Nexus Streamer.exe"
#define AppVer "1.0.0"
; Cartella sorgente = output di build-obfuscated.ps1 (publish self-contained + OFFUSCATO)
#define SrcDir "C:\Users\Fin\Desktop\StreamingDoc\publish"
#define IcoFile "C:\Users\Fin\Desktop\StreamingDoc\src\StreamingDoc.App\NexusStreamer.ico"

[Setup]
AppId={{B7E3F1A2-4C5D-4E6F-8A9B-0C1D2E3F4A5B}
AppName={#AppName}
AppVersion={#AppVer}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=C:\Users\Fin\Desktop\StreamingDoc\installer
OutputBaseFilename=NexusStreamer-Setup
SetupIconFile={#IcoFile}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes

[Languages]
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  Dir, FilePath, Code: string;
begin
  if CurStep = ssPostInstall then
  begin
    // ActiveLanguage() = il Name della lingua scelta (it/en/es/fr/de): lo passo all'app.
    Code := ActiveLanguage();
    Dir := ExpandConstant('{userdocs}\Nexus Streamer');
    if not DirExists(Dir) then
      ForceDirectories(Dir);
    FilePath := Dir + '\app.json';
    SaveStringToFile(FilePath, '{"Lang":"' + Code + '"}', False);
  end;
end;
