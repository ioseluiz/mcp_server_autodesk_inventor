# Alta y baja del servidor en claude_desktop_config.json.
#
# Se ejecuta contra un %APPDATA% de prueba: este codigo escribe en un archivo del
# usuario, asi que lo importante es que fusione sin destruir.
#
# Requiere inventor-mcp.exe construido.

# Igual que en run_all.ps1: el exe escribe avisos por stderr y con ErrorActionPreference
# = Stop, PowerShell 5.1 los convierte en NativeCommandError aunque el codigo de salida
# sea 0. Tampoco se redirige stderr de las llamadas nativas por el mismo motivo.
$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo "mcp_server\dist\inventor-mcp.exe"

if (-not (Test-Path $exe)) {
    Write-Host "  FALTA  $exe"
    Write-Host "  Construye el servidor:  pyinstaller inventor-mcp.spec"
    exit 1
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) "claude_cfg_test"
$cfgPath = Join-Path $root "Claude\claude_desktop_config.json"
$failed = 0

function Check($ok, $label, $detail = "") {
    $prefix = if ($ok) { "  PASS  " } else { "  FAIL  " }
    $suffix = if ($detail) { "  [$detail]" } else { "" }
    Write-Host ($prefix + $label + $suffix)
    if (-not $ok) { $script:failed++ }
}

function Reset-Appdata {
    if (Test-Path $root) { Remove-Item $root -Recurse -Force }
    New-Item -ItemType Directory -Path (Join-Path $root "Claude") -Force | Out-Null
}

# El servidor resuelve la ruta con %APPDATA%, asi que basta redirigirlo.
$env:APPDATA = $root

Write-Host "== fusion en un config existente con otros servidores =="
Reset-Appdata
@'
{
  "otraClave": 123,
  "mcpServers": {
    "filesystem": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem"] }
  }
}
'@ | Set-Content -Path $cfgPath -Encoding utf8

& $exe --install-claude-config | Out-Null
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
Check ($null -ne $cfg.mcpServers.filesystem) "servidor preexistente conservado"
Check ($cfg.otraClave -eq 123) "claves ajenas a mcpServers conservadas"
Check ($cfg.mcpServers.inventor.command -eq $exe) "entrada inventor apunta al exe"
Check (Test-Path "$cfgPath.bak") "copia de seguridad .bak creada"

Write-Host "== idempotencia =="
& $exe --install-claude-config | Out-Null
$cfg2 = Get-Content $cfgPath -Raw | ConvertFrom-Json
Check ($cfg2.mcpServers.PSObject.Properties.Name.Count -eq 2) "reinstalar no duplica entradas" ($cfg2.mcpServers.PSObject.Properties.Name -join ",")

Write-Host "== baja =="
& $exe --remove-claude-config | Out-Null
$cfg3 = Get-Content $cfgPath -Raw | ConvertFrom-Json
Check ($null -eq $cfg3.mcpServers.inventor) "entrada inventor eliminada"
Check ($null -ne $cfg3.mcpServers.filesystem) "el resto sigue intacto tras la baja"

Write-Host "== config inexistente =="
Reset-Appdata
& $exe --install-claude-config | Out-Null
Check (Test-Path $cfgPath) "se crea el archivo si no existia"

Write-Host "== JSON invalido: no se pisa =="
Reset-Appdata
"{ esto no es json" | Set-Content -Path $cfgPath -Encoding utf8
$salida = & $exe --install-claude-config 2>&1
$contenido = Get-Content $cfgPath -Raw
Check ($contenido -like "*esto no es json*") "el archivo ilegible NO se sobrescribe"
Check (($salida -join " ") -like "*JSON*") "el error explica que hay que hacerlo a mano"

Remove-Item $root -Recurse -Force
Write-Host ""
if ($failed -eq 0) { Write-Host "TODO OK" } else { Write-Host "$failed COMPROBACIONES FALLIDAS" }
exit $failed
