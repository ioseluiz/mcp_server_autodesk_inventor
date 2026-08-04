# Ejecuta toda la bateria de pruebas que no necesita Inventor instalado.
#
#   .\tests\run_all.ps1                 # todo (requiere inventor-mcp.exe construido)
#   .\tests\run_all.ps1 -SkipExe        # omite las pruebas del ejecutable congelado
#   .\tests\run_all.ps1 -Python C:\...\python.exe
#
# Lo unico que no se puede cubrir sin Inventor 2026 es la ejecucion real de comandos COM.

param(
    [switch]$SkipExe,
    [string]$Python = ""
)

# No se usa ErrorActionPreference = Stop: los ejecutables de estas pruebas escriben
# avisos por stderr (p. ej. la deprecacion de authlib que arrastra fastmcp) y PowerShell
# los convertiria en errores fatales. El exito se decide por el codigo de salida.
$ErrorActionPreference = "Continue"
$tests = $PSScriptRoot
$repo = Split-Path -Parent $tests
$pipeName = "InventorMCPBridgeTests"
$results = [ordered]@{}

if (-not $Python) {
    $venvPython = Join-Path $repo "mcp_server\venv\Scripts\python.exe"
    if (Test-Path $venvPython) { $Python = $venvPython } else { $Python = "python" }
}

function Invoke-Step($name, $scriptBlock) {
    Write-Host ""
    Write-Host "=============================================================="
    Write-Host " $name"
    Write-Host "=============================================================="
    & $scriptBlock
    $script:results[$name] = $LASTEXITCODE
}

function Wait-ForPipe($name, $timeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ([System.IO.Directory]::GetFiles("\\.\pipe\") -match [regex]::Escape($name)) {
            return $true
        }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

Write-Host "Python:  $Python"
Write-Host "Repo:    $repo"

# --- Compilacion -----------------------------------------------------------------
# Los proyectos de prueba referencian el .csproj del add-in, asi que esto compila
# tambien el add-in y falla si la migracion rompio algo.
Write-Host ""
Write-Host "Compilando add-in y proyectos de prueba..."
dotnet build (Join-Path $tests "BridgeTest\BridgeTest.csproj") -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Host "FALLO la compilacion de BridgeTest"; exit 1 }
dotnet build (Join-Path $tests "PipeHost\PipeHost.csproj") -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Host "FALLO la compilacion de PipeHost"; exit 1 }

# --- Pruebas del add-in (sin pipe externo) ---------------------------------------
Invoke-Step "C#: JsonPayload + BridgeService" {
    & (Join-Path $tests "BridgeTest\bin\Release\net8.0-windows\BridgeTest.exe")
}

# --- Host del pipe para las pruebas de Python ------------------------------------
$hostExe = Join-Path $tests "PipeHost\bin\Release\net8.0-windows\PipeHost.exe"
$hostLog = Join-Path ([System.IO.Path]::GetTempPath()) "pipehost_out.txt"
$hostProc = Start-Process -FilePath $hostExe -ArgumentList $pipeName, "0" `
    -WindowStyle Hidden -RedirectStandardOutput $hostLog -PassThru

try {
    if (-not (Wait-ForPipe $pipeName)) {
        Write-Host "El host del pipe no llego a escuchar en \\.\pipe\$pipeName"
        exit 1
    }
    Write-Host "Host del pipe listo (PID $($hostProc.Id))"

    Invoke-Step "Python: cliente del pipe" {
        & $Python (Join-Path $tests "test_pipe_client.py")
    }

    Invoke-Step "Python: transporte stdio y cadena completa" {
        & $Python (Join-Path $tests "test_stdio.py")
    }

    Invoke-Step "Docs: tabla de tools del README" {
        & $Python (Join-Path $tests "test_readme_tools.py")
    }

    if (-not $SkipExe) {
        Invoke-Step "Exe: inventor-mcp.exe por stdio" {
            & $Python (Join-Path $tests "test_exe.py")
        }

        Invoke-Step "Exe: alta/baja en Claude Desktop" {
            & (Join-Path $tests "test_claude_config.ps1")
        }
    }
}
finally {
    if ($hostProc -and -not $hostProc.HasExited) {
        Stop-Process -Id $hostProc.Id -Force
    }
}

# --- Resumen ---------------------------------------------------------------------
Write-Host ""
Write-Host "=============================================================="
Write-Host " RESUMEN"
Write-Host "=============================================================="
$failedSteps = 0
foreach ($name in $results.Keys) {
    $code = $results[$name]
    if ($code -eq 0) { Write-Host "  OK    $name" }
    else { Write-Host "  FALLO $name (codigo $code)"; $failedSteps++ }
}

Write-Host ""
if ($failedSteps -eq 0) {
    Write-Host "TODAS LAS PRUEBAS EN VERDE"
    exit 0
}
Write-Host "$failedSteps BLOQUES CON FALLOS"
exit 1
