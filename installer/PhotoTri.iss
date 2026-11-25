; Installeur de PhotoTri (Inno Setup 6). Ne pas lancer directement : utiliser installer\build.ps1,
; qui compile d'abord la version autonome dans publish\PhotoTri puis appelle ce script.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
; Identifiant fixe : une nouvelle version s'installe par-dessus l'ancienne. Ne jamais le changer.
AppId={{3ED45C2B-39D1-4026-BA78-701B157B614B}
AppName=PhotoTri
AppVersion={#AppVersion}
AppVerName=PhotoTri {#AppVersion}
AppPublisher=Olivier
AppComments=Transfert et classement des photos d'un téléphone Android
; Installation pour l'utilisateur seul, sans droits administrateur (dans %LocalAppData%\Programs\PhotoTri).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\PhotoTri
DefaultGroupName=PhotoTri
DisableProgramGroupPage=yes
; Windows 10 version 1809 ou plus récent, 64 bits.
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish
OutputBaseFilename=PhotoTri-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=PhotoTri
UninstallDisplayIcon={app}\PhotoTri.App.exe
; Ferme PhotoTri s'il est ouvert pendant une mise à jour.
CloseApplications=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; Une mise à jour remplace entièrement les fichiers du programme (les photos et les réglages sont ailleurs).
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "..\publish\PhotoTri\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\PhotoTri"; Filename: "{app}\PhotoTri.App.exe"; Comment: "Transférer et classer les photos du téléphone"
Name: "{autodesktop}\PhotoTri"; Filename: "{app}\PhotoTri.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\PhotoTri.App.exe"; Description: "{cm:LaunchProgram,PhotoTri}"; Flags: nowait postinstall skipifsilent
