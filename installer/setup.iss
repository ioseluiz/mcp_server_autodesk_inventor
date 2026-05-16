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
  ConfigPage: TInputQueryWizardPage;

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
  ConfigPage := CreateInputQueryPage(wpWelcome,
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

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
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
