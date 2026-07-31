; IndepenDesk kurulum sihirbazı (Inno Setup 6)
; Kullanım: ISCC installer.iss /DArch=x64 /DMyVersion=1.2.3 /Oartifacts

#ifndef Arch
  #define Arch "x64"
#endif
#ifndef MyVersion
  #define MyVersion "0.0.0"
#endif

[Setup]
AppId={{9D3C7A52-3F4B-4C61-8E2A-1B5D6F7A8C90}
AppName=IndepenDesk
AppVersion={#MyVersion}
AppPublisher=harungecit
AppPublisherURL=https://github.com/harungecit/IndepenDesk
AppSupportURL=https://github.com/harungecit/IndepenDesk/issues
DefaultDirName={autopf}\IndepenDesk
DisableProgramGroupPage=yes
OutputBaseFilename=IndepenDesk-Setup-{#MyVersion}-{#Arch}
SetupIconFile=app.ico
UninstallDisplayIcon={app}\IndepenDesk.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
#if SameText(Arch, "x64")
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#elif SameText(Arch, "arm64")
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\publish\{#Arch}\IndepenDesk.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\IndepenDesk"; Filename: "{app}\IndepenDesk.exe"
Name: "{autodesktop}\IndepenDesk"; Filename: "{app}\IndepenDesk.exe"; Tasks: desktopicon

; Başlangıç, kısayol yerine uygulamanın da yönettiği HKCU Run değeriyle sağlanır
; (varsayılan açık; tepsi menüsünden kapatılabilir; kaldırırken temizlenir).
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "IndepenDesk"; ValueData: """{app}\IndepenDesk.exe"""; Flags: uninsdeletevalue

; 0.3.x'in Başlangıç klasörü kısayolu kaldırılır (Run değeriyle çift başlatmayı önler)
[InstallDelete]
Type: files; Name: "{autostartup}\IndepenDesk.lnk"

[Run]
Filename: "{app}\IndepenDesk.exe"; Description: "{cm:LaunchProgram,IndepenDesk}"; Flags: nowait postinstall skipifsilent
