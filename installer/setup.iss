#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName      "Inventor MCP Bridge"
#define AppPublisher "InventorMCPBridge"
; El plugin es .NET 8 y AppendTargetFrameworkToOutputPath=false, así que la salida
; queda directamente en bin\x64\Release (sin subcarpeta net8.0-windows).
#define SourcePath   "..\inventor_plugin\bin\x64\Release"
; Salida de PyInstaller (mcp_server/dist), el servidor MCP local congelado.
#define ServerPath   "..\mcp_server\dist"

[Setup]
AppId={{E1A5A5A5-A5A5-A5A5-A5A5-A5A5A5A5A5A5}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={userappdata}\Autodesk\Inventor 2026\Addins\InventorMCPBridge
DisableDirPage=yes
PrivilegesRequired=lowest
; Inventor 2026 es solo x64. En modo 64 bits {pf} apunta a Program Files real y no a
; Program Files (x86), que es lo que hacía que no se detectara la instalación.
; Requiere Inno Setup 6.3 o superior por los identificadores "x64compatible".
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=.\output
OutputBaseFilename=InventorMCPBridgeSetup-{#AppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "claudeconfig"; Description: "Registrar el servidor MCP en Claude Desktop"; GroupDescription: "Integración:"

[Files]
; El add-in ya no lleva dependencias NuGet: usa System.Text.Json del framework.
Source: "{#SourcePath}\InventorMCPBridge.dll";       DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePath}\InventorMCPBridge.addin";     DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePath}\InventorMCPBridge.deps.json"; DestDir: "{app}"; Flags: ignoreversion
; Servidor MCP local que lanza Claude Desktop por stdio (PyInstaller --onefile).
Source: "{#ServerPath}\inventor-mcp.exe";            DestDir: "{app}"; Flags: ignoreversion

[Run]
; La entrada de claude_desktop_config.json la escribe el propio servidor: hay que
; fusionarla con los mcpServers que el usuario ya tenga, y eso necesita un parser JSON.
Filename: "{app}\inventor-mcp.exe"; Parameters: "--install-claude-config"; \
  Description: "Registrar en Claude Desktop"; Flags: runhidden; Tasks: claudeconfig

[UninstallRun]
Filename: "{app}\inventor-mcp.exe"; Parameters: "--remove-claude-config"; \
  Flags: runhidden; RunOnceId: "RemoveClaudeConfig"

[UninstallDelete]
; config.json lo genera [Code], así que el desinstalador no lo conoce.
Type: files; Name: "{app}\config.json"

[Code]
const
  { Versión interna de Inventor 2026. 2024 = 28, 2025 = 29, 2026 = 30. }
  MinInventorMajor = 30;

var
  InventorDirPage: TInputDirWizardPage;
  DetectedInventorPath: string;

function FindInventorPath(): string;
var
  DefaultPath: string;
begin
  DefaultPath := ExpandConstant('{pf}\Autodesk\Inventor 2026');
  if FileExists(DefaultPath + '\Bin\Inventor.exe') then
    Result := DefaultPath
  else
    Result := '';
end;

function InventorMajorVersion(const InventorRoot: string): Integer;
var
  VersionMS, VersionLS: Cardinal;
begin
  Result := 0;
  if GetVersionNumbers(InventorRoot + '\Bin\Inventor.exe', VersionMS, VersionLS) then
    Result := VersionMS shr 16;
end;

procedure InitializeWizard;
begin
  DetectedInventorPath := FindInventorPath();

  InventorDirPage := CreateInputDirPage(wpWelcome,
    'Ubicación de Autodesk Inventor 2026',
    'No se encontró Autodesk Inventor 2026 en la ruta predeterminada.',
    'Selecciona la carpeta raíz de instalación de Autodesk Inventor 2026 ' +
    '(debe contener la carpeta Bin con Inventor.exe y Autodesk.Inventor.Interop.dll):',
    False, '');
  InventorDirPage.Add('');

  if DetectedInventorPath <> '' then
    InventorDirPage.Values[0] := DetectedInventorPath
  else
    InventorDirPage.Values[0] := ExpandConstant('{pf}\Autodesk\Inventor 2026');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = InventorDirPage.ID then
    Result := DetectedInventorPath <> '';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  SelectedPath: string;
  Major: Integer;
begin
  Result := True;

  if CurPageID = InventorDirPage.ID then
  begin
    SelectedPath := InventorDirPage.Values[0];

    if not FileExists(SelectedPath + '\Bin\Inventor.exe') then
    begin
      MsgBox(
        'No se encontró Inventor.exe en:' + #13#10 +
        SelectedPath + '\Bin\' + #13#10 + #13#10 +
        'Verifica que seleccionaste la carpeta raíz correcta de Autodesk Inventor 2026.',
        mbError, MB_OK);
      Result := False;
      Exit;
    end;

    { El add-in está compilado para .NET 8: Inventor 2024 y anteriores hospedan
      .NET Framework 4.8 y no pueden cargarlo. Se avisa en lugar de bloquear, por si
      la versión del ejecutable no coincide con lo esperado. }
    Major := InventorMajorVersion(SelectedPath);
    if (Major > 0) and (Major < MinInventorMajor) then
    begin
      if MsgBox(
        'La instalación seleccionada parece ser anterior a Inventor 2026 ' +
        '(versión interna ' + IntToStr(Major) + ').' + #13#10 + #13#10 +
        'Este complemento está compilado para .NET 8 y solo carga en Inventor 2025 ' +
        'o superior. ¿Continuar de todos modos?',
        mbConfirmation, MB_YESNO) = IDNO then
      begin
        Result := False;
        Exit;
      end;
    end;

    DetectedInventorPath := SelectedPath;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigPath: string;
begin
  if CurStep = ssPostInstall then
  begin
    { Ya no hay que pedir URL, API Key ni usuario: el servidor MCP es local y se
      conecta al named pipe del plugin. Solo se deja el config.json para poder
      cambiar el nombre del pipe si se ejecutan varias sesiones de Inventor a la vez.
      No se sobrescribe si el usuario ya lo había ajustado. }
    ConfigPath := ExpandConstant('{app}\config.json');
    if not FileExists(ConfigPath) then
      SaveStringToFile(ConfigPath,
        '{' + #13#10 +
        '  "PipeName": "InventorMCPBridge"' + #13#10 +
        '}' + #13#10, False);
  end;
end;
