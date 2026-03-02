; FotoShow Escritorio — InnoSetup Installer
; Compilar con: iscc setup.iss
; Requiere: Inno Setup 6+, .NET 8 runtime, .NET Framework 4.8

#define AppName "FotoShow Escritorio"
#define AppVersion "1.0.0"
#define AppPublisher "FotoShow"
#define AppURL "https://fotoshow.online"
#define AppExeName "FotoshowTray.exe"
#define AppGuid "{{FOTOSHOW-DESK-0001-0001-000000000001}"

[Setup]
AppId={#AppGuid}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
DefaultDirName={autopf}\FotoShow
DefaultGroupName=FotoShow
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=FotoShowEscritorio_Setup_{#AppVersion}
SetupIconFile=..\FotoshowTray\Resources\fotoshow.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
; Requiere Windows 10 64-bit
MinVersion=10.0.17763
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "startup"; Description: "Iniciar FotoShow automáticamente con Windows"; GroupDescription: "Opciones:"

[Files]
; ── Tray app (.NET 8) ───────────────────────────────────────────────────────
Source: "..\FotoshowTray\bin\Release\net8.0-windows\win-x64\publish\*"; \
    DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; ── Shell extension (.NET Framework 4.8) ───────────────────────────────────
Source: "..\FotoshowShell\bin\Release\net48\*"; \
    DestDir: "{app}\shell"; Flags: ignoreversion recursesubdirs createallsubdirs

; ── AI Worker (InsightFace — compilado con PyInstaller) ─────────────────────
Source: "..\dist\ai_worker.exe"; DestDir: "{app}"; Flags: ignoreversion
; Fallback script (para Python portable si el exe falla)
Source: "..\ai_worker.py"; DestDir: "{app}"; Flags: ignoreversion

; ── Iconos overlay ──────────────────────────────────────────────────────────
Source: "icons\overlay_pending.ico"; DestDir: "{app}\shell\icons"; Flags: ignoreversion
Source: "icons\overlay_synced.ico"; DestDir: "{app}\shell\icons"; Flags: ignoreversion
Source: "icons\overlay_sold.ico"; DestDir: "{app}\shell\icons"; Flags: ignoreversion

[Icons]
Name: "{group}\FotoShow Escritorio"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Desinstalar FotoShow"; Filename: "{uninstallexe}"

[Registry]
; ── Context menu: clic derecho en CARPETAS ──────────────────────────────────
Root: HKLM; Subkey: "SOFTWARE\Classes\Directory\shell\FotoshowAddFolder"; \
    ValueType: string; ValueName: ""; ValueData: "Agregar carpeta a FotoShow"; \
    Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Classes\Directory\shell\FotoshowAddFolder"; \
    ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExeName},0"
Root: HKLM; Subkey: "SOFTWARE\Classes\Directory\shell\FotoshowAddFolder\command"; \
    ValueType: string; ValueName: ""; \
    ValueData: """{app}\{#AppExeName}"" --add-folder ""%1"""

; También en clic derecho en el fondo de una carpeta (DirectoryBackground)
Root: HKLM; Subkey: "SOFTWARE\Classes\Directory\Background\shell\FotoshowAddFolder"; \
    ValueType: string; ValueName: ""; ValueData: "Agregar carpeta a FotoShow"; \
    Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Classes\Directory\Background\shell\FotoshowAddFolder"; \
    ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExeName},0"
Root: HKLM; Subkey: "SOFTWARE\Classes\Directory\Background\shell\FotoshowAddFolder\command"; \
    ValueType: string; ValueName: ""; \
    ValueData: """{app}\{#AppExeName}"" --add-folder ""%V"""

; ── Context menu: clic derecho en IMÁGENES ──────────────────────────────────
Root: HKLM; Subkey: "SOFTWARE\Classes\SystemFileAssociations\image\shell\FotoshowAddPhoto"; \
    ValueType: string; ValueName: ""; ValueData: "Agregar foto a FotoShow"; \
    Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Classes\SystemFileAssociations\image\shell\FotoshowAddPhoto"; \
    ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExeName},0"
Root: HKLM; Subkey: "SOFTWARE\Classes\SystemFileAssociations\image\shell\FotoshowAddPhoto\command"; \
    ValueType: string; ValueName: ""; \
    ValueData: """{app}\{#AppExeName}"" --add-file ""%1"""

; ── URI Scheme: fotoshow:// (para callback de login Google) ─────────────────
Root: HKLM; Subkey: "SOFTWARE\Classes\fotoshow"; \
    ValueType: string; ValueName: ""; ValueData: "URL:FotoShow Protocol"; \
    Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Classes\fotoshow"; \
    ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKLM; Subkey: "SOFTWARE\Classes\fotoshow\shell\open\command"; \
    ValueType: string; ValueName: ""; \
    ValueData: """{app}\{#AppExeName}"" --auth-token ""%1"""

[Run]
; Registrar la shell extension COM (overlay icons)
Filename: "{dotnet4032}\regasm.exe"; \
    Parameters: "/codebase ""{app}\shell\FotoshowShell.dll"""; \
    StatusMsg: "Registrando overlay icons..."; \
    Flags: runhidden waituntilterminated; Check: IsWin64

; Iniciar el tray al terminar la instalación
Filename: "{app}\{#AppExeName}"; \
    Description: "Iniciar FotoShow ahora"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; Matar procesos antes de desinstalar
Filename: "taskkill.exe"; Parameters: "/F /IM FotoshowTray.exe"; Flags: runhidden waituntilterminated
Filename: "taskkill.exe"; Parameters: "/F /IM ai_worker.exe"; Flags: runhidden waituntilterminated
; Desregistrar COM overlay
Filename: "{dotnet4032}\regasm.exe"; \
    Parameters: "/unregister ""{app}\shell\FotoshowShell.dll"""; \
    Flags: runhidden waituntilterminated; Check: IsWin64

[UninstallDelete]
; Limpiar carpeta de datos si el usuario lo desea (no se hace por defecto)
; Type: filesandordirs; Name: "{userappdata}\Fotoshow"

[Code]
// ── arranque automático con Windows ──────────────────────────────────────────
procedure CurStepChanged(CurStep: TSetupStep);
var
  RegPath: String;
begin
  if CurStep = ssPostInstall then
  begin
    RegPath := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run';
    if IsTaskSelected('startup') then
      RegWriteStringValue(HKCU, RegPath, 'FotoshowTray',
        ExpandConstant('"{app}\{#AppExeName}"'))
    else
      RegDeleteValue(HKCU, RegPath, 'FotoshowTray');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKCU,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'FotoshowTray');
end;

// ── Verificar que .NET 8 Desktop Runtime esté instalado ─────────────────────
function DotNet8Installed(): Boolean;
begin
  // Checar si dotnet.exe existe (instalado por SDK o Runtime)
  Result := FileExists(ExpandConstant('{pf}\dotnet\dotnet.exe')) or
            FileExists(ExpandConstant('{pf64}\dotnet\dotnet.exe')) or
            RegKeyExists(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedframework\Microsoft.WindowsDesktop.App') or
            RegKeyExists(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App');
end;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if not DotNet8Installed() then
  begin
    if MsgBox('.NET 8 Desktop Runtime no está instalado.' + #13#10 +
              'Se abrirá la página de descarga. Instálalo y volvé a ejecutar el installer.',
              mbInformation, MB_OKCANCEL) = IDOK then
      ShellExec('open',
        'https://dotnet.microsoft.com/download/dotnet/8.0/runtime',
        '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
    Result := False;
  end;
end;
