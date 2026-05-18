#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName      "Inventor MCP Bridge"
#define AppPublisher "InventorMCPBridge"
#define SourcePath   "..\inventor_plugin\bin\x64\Release\net48"

[Setup]
AppId={{E1A5A5A5-A5A5-A5A5-A5A5-A5A5A5A5A5A5}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={userappdata}\Autodesk\Inventor 2024\Addins\InventorMCPBridge
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=.\output
OutputBaseFilename=InventorMCPBridgeSetup-{#AppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
Source: "{#SourcePath}\InventorMCPBridge.dll";  DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePath}\InventorMCPBridge.addin"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourcePath}\Newtonsoft.Json.dll";     DestDir: "{app}"; Flags: ignoreversion

[Code]
var
  InventorDirPage: TInputDirWizardPage;
  ConfigPage: TInputQueryWizardPage;
  DetectedInventorPath: string;

function FindInventorPath(): string;
var
  DefaultPath: string;
begin
  DefaultPath := ExpandConstant('{pf}\Autodesk\Inventor 2024');
  if FileExists(DefaultPath + '\Bin\Inventor.exe') then
    Result := DefaultPath
  else
    Result := '';
end;

function EscapeJson(const S: string): string;
var
  I: Integer;
begin
  Result := '';
  for I := 1 to Length(S) do
  begin
    case S[I] of
      '"':  Result := Result + '\"';
      '\':  Result := Result + '\\';
    else
      Result := Result + S[I];
    end;
  end;
end;

procedure InitializeWizard;
begin
  DetectedInventorPath := FindInventorPath();

  InventorDirPage := CreateInputDirPage(wpWelcome,
    'Ubicación de Autodesk Inventor 2024',
    'No se encontró Autodesk Inventor 2024 en la ruta predeterminada.',
    'Selecciona la carpeta raíz de instalación de Autodesk Inventor 2024 ' +
    '(debe contener la carpeta Bin con Inventor.exe y Autodesk.Inventor.Interop.dll):',
    False, '');
  InventorDirPage.Add('');

  if DetectedInventorPath <> '' then
    InventorDirPage.Values[0] := DetectedInventorPath
  else
    InventorDirPage.Values[0] := ExpandConstant('{pf}\Autodesk\Inventor 2024');

  ConfigPage := CreateInputQueryPage(InventorDirPage.ID,
    'Configuración del servidor MCP',
    'Ingresa los datos de conexión al servidor MCP.',
    'Estos valores se guardan en config.json dentro de la carpeta del plugin y se pueden editar en cualquier momento.');

  ConfigPage.Add('URL del servidor:', False);
  ConfigPage.Add('API Key:', True);
  ConfigPage.Add('ID de usuario:', False);

  ConfigPage.Values[0] := '';
  ConfigPage.Values[1] := '';
  ConfigPage.Values[2] := '';
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
        'Verifica que seleccionaste la carpeta raíz correcta de Autodesk Inventor 2024.',
        mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if not FileExists(SelectedPath + '\Bin\Autodesk.Inventor.Interop.dll') then
    begin
      MsgBox(
        'No se encontró Autodesk.Inventor.Interop.dll en:' + #13#10 +
        SelectedPath + '\Bin\' + #13#10 + #13#10 +
        'Verifica que tu instalación de Autodesk Inventor 2024 esté completa.',
        mbError, MB_OK);
      Result := False;
      Exit;
    end;

    DetectedInventorPath := SelectedPath;
  end;

  if CurPageID = ConfigPage.ID then
  begin
    if Trim(ConfigPage.Values[0]) = '' then
    begin
      MsgBox('La URL del servidor es requerida.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Trim(ConfigPage.Values[1]) = '' then
    begin
      MsgBox('El API Key es requerido.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Trim(ConfigPage.Values[2]) = '' then
    begin
      MsgBox('El ID de usuario es requerido.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigJson: string;
begin
  if CurStep = ssPostInstall then
  begin
    ConfigJson :=
      '{' + #13#10 +
      '  "ServerUrl": "' + EscapeJson(Trim(ConfigPage.Values[0])) + '",' + #13#10 +
      '  "ApiKey": "' + EscapeJson(Trim(ConfigPage.Values[1])) + '",' + #13#10 +
      '  "UserId": "' + EscapeJson(Trim(ConfigPage.Values[2])) + '"' + #13#10 +
      '}';
    SaveStringToFile(ExpandConstant('{app}\config.json'), ConfigJson, False);
  end;
end;
