# Pruebas

Cubren todo lo que se puede verificar **sin Inventor instalado**: la capa JSON del add-in,
el transporte por named pipe, el cliente de pipe del servidor MCP, el transporte stdio, el
ejecutable empaquetado y el alta en Claude Desktop.

Lo único que queda fuera es la ejecución real de los comandos COM, que necesita
Inventor 2026 abierto.

## Ejecutar todo

```powershell
# Requiere .NET 8 SDK y un Python con las dependencias de mcp_server instaladas
.\tests\run_all.ps1

# Sin las pruebas del ejecutable (evita construirlo con PyInstaller)
.\tests\run_all.ps1 -SkipExe
```

`run_all.ps1` compila el add-in y los proyectos de prueba, arranca el host del pipe, espera
a que el pipe exista, ejecuta cada bloque y devuelve código distinto de cero si algo falla.

## Qué hay aquí

| Archivo | Qué comprueba | Necesita |
|---|---|---|
| `BridgeTest/` | `JsonPayload` (normalización y serialización) y `BridgeService` por pipe: comando desconocido, JSON inválido, excepción de handler, reconexión | .NET 8 SDK |
| `PipeHost/` | No es una prueba: levanta el `BridgeService` real sobre un pipe de pruebas para que lo ataquen las pruebas de Python | .NET 8 SDK |
| `test_pipe_client.py` | El cliente de pipe de `main.py` contra el add-in real: errores propagados, UTF-8, 3 MB de ida y de vuelta, reutilización de la conexión, bridge apagado | `PipeHost` en marcha |
| `test_stdio.py` | Handshake MCP por stdio (solo pasa si stdout está limpio), las 52 tools, y la cadena completa hasta el add-in | `PipeHost` en marcha |
| `test_readme_tools.py` | Que la tabla de tools del README coincida con las tools reales. Con `--update` la regenera | — |
| `test_exe.py` | `inventor-mcp.exe` como servidor MCP. Es la prueba que detecta los imports dinámicos que PyInstaller no ve | el `.exe` construido |
| `test_claude_config.ps1` | Fusión en `claude_desktop_config.json`: conserva lo ajeno, hace `.bak`, es idempotente, y no destruye un JSON ilegible | el `.exe` construido |

## Detalles que importan

- Las pruebas usan el pipe **`InventorMCPBridgeTests`**, nunca el nombre real, para no
  interferir con un Inventor abierto en la misma máquina.
- `PipeHost` construye el `BridgeService` con `inventorApp = null`: los handlers fallan a
  propósito. Lo que se mide es el transporte, no la API de Inventor. Un mensaje como
  `"No hay boceto activo."` viajando de vuelta con sus acentos es señal de éxito.
- Los proyectos C# referencian el `.csproj` del add-in, así que compilarlos compila el
  add-in: un fallo de compilación del plugin hace fallar las pruebas.
