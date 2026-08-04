# Plan de migración — .NET 8 (Inventor 2026) + servidor MCP stdio local

Estado del documento: plan aprobado. Fases 1 y 2 implementadas; el resto pendiente.

## Arquitectura objetivo

```
Claude Desktop ──stdio──> inventor-mcp (FastMCP, Python) ──named pipe──> Plugin .NET 8 ──COM──> Inventor 2026
                          52 tools                        \\.\pipe\InventorMCPBridge
```

## Decisiones tomadas

1. **Destino Inventor 2026** (interop 30.x), `net8.0-windows`. Se abandona el soporte de
   Inventor 2024: un ensamblado .NET 8 no carga en un host con CLR .NET Framework 4.8.
   Inventor 2025 fue la primera versión migrada a .NET 8; Inventor 2027 usa .NET 10, pero
   un binario `net8.0-windows` sigue cargando ahí por roll-forward.
2. **Canal IPC = named pipe, con el plugin como servidor.** Claude Desktop relanza el
   proceso del servidor MCP en cada arranque/reconexión y puede dejar más de uno vivo, así
   que el extremo estable tiene que ser Inventor. El pipe evita puertos, firewall y reservas
   de URL, y su ACL por defecto limita el acceso al usuario que lo crea.
3. **Se elimina la ruta de nube** (FastAPI, uvicorn/gunicorn, `/sse`, auth por API key,
   deploy a Azure). Queda en el historial de git. Copilot Studio deja de ser un cliente
   posible: no habla stdio.
4. **El plugin migra a `System.Text.Json`.** Al desaparecer HTTP, Newtonsoft.Json era la
   única dependencia externa. Quitarla elimina de raíz el riesgo de choque de versiones en
   el AssemblyLoadContext compartido de Inventor 2025/2026 (que no aísla add-ins hasta 2027)
   y deja la carpeta del add-in con un solo DLL.

## Qué se elimina por completo

Polling, `user_queues`, `pending_tasks`, `completed_tasks`, `task_events`, `/api/poll`,
`/api/result`, `/api/health`, `/api/debug`, `/sse`, `USERS_CONFIG`, API keys, `user_id`,
CORS, middleware de auth, FastAPI, uvicorn, gunicorn y el workflow de deploy a Azure.

---

## Fase 0 — Prerrequisitos

1. ✅ **.NET 8 SDK** instalado (8.0.423, vía `winget install Microsoft.DotNet.SDK.8`).
   VS 2022 >= 17.8 sigue siendo necesario para editar/depurar desde el IDE.
2. ⏳ Acceso a **Inventor 2026** para copiar
   `C:\Program Files\Autodesk\Inventor 2026\Bin\Public Assemblies\Autodesk.Inventor.Interop.dll`
   (v30.x) sobre `lib/Autodesk.Inventor.Interop.dll`, que hoy es 28.3.0.0 (Inventor 2024) y
   es el fallback que usa CI, donde no hay Inventor instalado.
3. Claude Desktop instalado; localizar `%APPDATA%\Claude\claude_desktop_config.json`.

## Fase 1 — `inventor_plugin/InventorMCPBridge.csproj`  ✅ hecho

1. `net48` → `net8.0-windows`.
2. Eliminar `<Reference Include="System.Net.Http" />`: es un *facade* de .NET Framework; en
   .NET 8 `System.Net.Http` viene en el framework compartido y la referencia rompe el build.
3. Interop: `Inventor 2024` → `Inventor 2026`, sobreescribible con `-p:InventorInteropPath=...`.
4. `<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>` para
   conservar la salida en `bin\x64\Release\` y no romper instalador/CI con la subcarpeta
   `net8.0-windows` (el problema clásico de rutas de los add-ins .NET 8).
5. `<GenerateDependencyFile>true</GenerateDependencyFile>` mientras siga habiendo una
   dependencia NuGet (ver Fase 4.2).
6. Se mantiene: `UseWindowsForms`, `x64`, `ImplicitUsings/Nullable=disable`,
   `EmbedInteropTypes=False`, `Private=False`, `CopyLocalLockFileAssemblies=true`.
7. No usar `EnableComHosting`, `PublishSingleFile`, trimming ni AOT: `Marshal.ReleaseComObject`
   y `Assembly.Location` (usado en `PluginConfig.Load`) dependen de ello. Inventor carga el
   add-in por el manifiesto `.addin`, no por registro COM; `[Guid]` y `[ComVisible(true)]`
   siguen siendo necesarios.
8. `PackageReference` de Newtonsoft.Json: se mantiene hasta la Fase 4.2 para no dejar el
   repo sin compilar.

## Fase 2 — `inventor_plugin/InventorMCPBridge.addin`  ✅ hecho

1. `SupportedSoftwareVersionGreaterThan`: `27..` → `29..` (Inventor 2026+). Evita que un
   usuario de 2024 lo instale y obtenga un fallo silencioso de carga.
2. `<OSType>Win64</OSType>` (el elemento se llama `OSType`, no `OS`).
3. Comentario con `<UseInventorAssemblyContext>0</UseInventorAssemblyContext>` para activar
   el aislamiento de ensamblados nativo cuando se pase a Inventor 2027.
4. `ClassId` / `ClientId` / `<Assembly>` sin cambios.

## Fase 3 — Plugin: de cliente HTTP a servidor de named pipe  ✅ hecho

Los ~2700 líneas de handlers no se tocaron: el contrato `{command, payload}` →
`{result, error}` se conserva. Solo cambió el transporte.

Desviaciones respecto a lo planeado:

- `Services/PollingService.cs` renombrado a `Services/BridgeService.cs` y la clase a
  `BridgeService`: ya no hay polling y el nombre engañaba. Git detecta el renombrado.
- `HandleTask(McpTask)` pasó a `Execute(string command, Dictionary<string, object> payload)`,
  que devuelve el resultado y lanza en el caso `default`. El cuerpo del `switch` con los 52
  comandos quedó intacto (solo `task.Payload` → `payload`, 49 sustituciones).
- **No se importa `System.IO`** en `BridgeService.cs`: `Path` colisiona con `Inventor.Path`
  (trayectoria de barrido, usada en `SweepProfile`) y daba error CS0104. Los tipos de
  `System.IO` van cualificados.
- La Fase 4.3 (`PluginConfig` → `PipeName`) se adelantó porque el constructor de
  `BridgeService` la arrastra.

1. Renombrar `PollingService` → `BridgeService` (opcional; mantener el nombre minimiza el diff).
   El `switch` de `HandleTask` con los ~52 comandos queda intacto.
2. Servidor: `NamedPipeServerStream("InventorMCPBridge", PipeDirection.InOut,
   maxNumberOfServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)`.
   Protocolo JSON delimitado por líneas: una petición por línea, una respuesta por línea.
   Bucle: aceptar conexión → servir hasta desconexión → volver a esperar. Debe sobrevivir a
   desconexiones sucias y aceptar varias conexiones (Claude Desktop deja procesos huérfanos).
3. Quitar `HttpClient`, `_baseUrl`, `_apiKey`, `_userId` y el header `x-api-key`.
4. `StandardAddInServer.cs:113-139` — `ToggleBridge()` ya no puede llamar a `TestConnection()`
   (línea 117): el plugin es el servidor, no hay nada que probar antes de arrancar. El botón
   pasa a ser start/stop del listener. Si el pipe ya está tomado por otra instancia de
   Inventor, mostrar ese error concreto.
5. **Marshalling a la hebra STA de Inventor** (crítico): las peticiones llegan en hebras del
   pool. Crear un `Control` oculto en `Activate()` para capturar la hebra principal de
   Inventor y ejecutar cada comando con `Invoke(...)`. Resuelve el `RPC_E_CALL_REJECTED`
   (0x80010001) que el diseño de polling ya arriesgaba y serializa los comandos.
6. `Deactivate()`: cancelar el token, cerrar y disponer el pipe, y después el
   `Marshal.ReleaseComObject` actual.
7. Opcional: restringir la ACL del pipe al usuario actual con `NamedPipeServerStreamAcl`
   (requiere el paquete `System.IO.Pipes.AccessControl`).

## Fase 4 — Plugin: correcciones de migración

Estado: 4.2 y 4.3 ✅ hechas (ver `Services/JsonPayload.cs`); 4.1 descartada (falso positivo,
ver más abajo); 4.4 no requiere cambios.
Verificación de 4.2 sin Inventor: proyecto de prueba temporal que compila `JsonPayload.cs` y
referencia el DLL ya construido; 30 comprobaciones en verde, incluidos los patrones
`Convert.ToDouble(payload[..])`, `payload[..].ToString()`, `List<object>` en `ParseIntList` y
`LoftProfiles`, y un ciclo completo por el pipe (comando desconocido, JSON inválido,
excepción de handler, reconexión). Newtonsoft.Json ya no se despliega: la salida del build es
`InventorMCPBridge.dll` + `.addin` + `.deps.json`.

1. ~~**Bug de iconos** — CLSID vacío en `IconConverter`.~~ **Descartado: falso positivo.**
   `IconConverter.ToIPictureDisp` es un método **estático**, así que el constructor
   `base(string.Empty)` nunca se ejecuta y su CLSID es irrelevante. Comprobado en .NET 8 con
   un programa de prueba: tanto `base(string.Empty)` como `base(<GUID válido>)` devuelven un
   `System.__ComObject` correcto desde `AxHost.GetIPictureDispFromPicture`. No se toca el
   código. (El constructor privado es código muerto, pero inofensivo.)
2. **`System.Text.Json`**: sustituir las 2 llamadas a `JsonConvert` (`PollingService.cs:274` y
   el deserializado del bucle) y los 3 `[JsonProperty]` de `McpTask` (líneas 2831-2841) por
   `[JsonPropertyName]`. Añadir un **normalizador recursivo** `JsonElement` →
   `double`/`string`/`bool`/`List<object>`/`Dictionary<string,object>` justo después de
   deserializar: los **194** accesos a `payload[...]` y los **55** `Convert.To*(payload[...])`
   dependen del boxing de Newtonsoft y `Convert.ToDouble(JsonElement)` lanza excepción. Con el
   shim (~40 líneas) ninguno de los 194 call sites se edita. Cambiar además los 2 chequeos
   `is Newtonsoft.Json.Linq.JArray` (líneas 1413 y 1537) a `is List<object>`.
   Al terminar: quitar el `PackageReference` de Newtonsoft.Json y `GenerateDependencyFile`.
3. `PluginConfig`: `ServerUrl`/`ApiKey`/`UserId` → `PipeName` (default `InventorMCPBridge`).
   Mantener `Assembly.Location` para resolver `config.json`.
4. `System.Drawing` (captura de pantalla, `PollingService.cs:459-463`) no necesita el paquete
   `System.Drawing.Common`: en `net8.0-windows` con `UseWindowsForms` viene en el Windows
   Desktop Framework.

## Fase 5 — Servidor Python: stdio puro  ✅ hecho

`main.py` pasó de 1823 a 1766 líneas. Las 52 tools quedaron intactas; se sustituyó el
bloque de cabecera (líneas 1-220) y el pie (1783-final). Cero referencias restantes a
FastAPI, uvicorn, StreamableHTTP, CORS, API keys, `user_queues` o `load_dotenv`.

**Corrección importante sobre el punto 5.4 de este plan:** la idea de declarar
`ContextVar("current_user_id", default="local")` **no funciona**. Las 52 tools llaman
`current_user_id.get(None)`, y `ContextVar.get(default)` devuelve el `None` que se le pasa,
ignorando el default del propio ContextVar: las tools respondían
`"Error: sesión no autenticada."`. Detectado en la prueba de stdio y resuelto con un
sustituto explícito (`_LocalUser.get()` devuelve siempre `"local"`), que además no depende
de la propagación de contextos entre tareas.

Otros dos ajustes que no estaban en el plan, detectados al ejecutar:

- `mcp.run(transport="stdio", show_banner=False)`: FastMCP imprimía un banner ASCII por
  stderr que solo ensucia el log de Claude Desktop.
- `os.environ.setdefault("FASTMCP_CHECK_FOR_UPDATES", "off")` **antes** de importar
  `fastmcp` (sus settings se leen del entorno al importar): cada arranque hacía una
  petición HTTP a pypi.org para comprobar actualizaciones.

Además: `requirements.txt` reducido a 4 paquetes y `.env.example` eliminado (documentaba
`USERS_CONFIG` y las API keys, que ya no existen). Se creó `mcp_server/venv` (ignorado por
git) con Python 3.12.10 para las pruebas y para la Fase 6.

**Verificación ejecutada** (tres pruebas, todas en verde):

1. *Cliente de pipe contra el `BridgeService` real* — un host C# levanta el mismo DLL que
   cargará Inventor y el cliente Python lo ataca: error del add-in propagado, acentos y `ñ`
   intactos ida y vuelta, petición de 3 MB y respuesta de 3 MB en una sola línea, excepción
   de handler, reutilización de la conexión, y pipe inexistente → mensaje accionable.
2. *Transporte stdio con el cliente MCP oficial* — handshake `initialize` (que solo pasa si
   stdout está limpio), `tools/list` devuelve las 52 tools, y la sesión sobrevive al error
   de una tool.
3. *Cadena completa* — cliente MCP → stdio → `main.py` → named pipe → C#: tres llamadas
   consecutivas, incluida una con payload, devolviendo mensajes reales de los handlers
   (`"No hay boceto activo."`) con acentos correctos.

Lo único que no se puede verificar sin Inventor 2026 es la ejecución real de los comandos
COM (Fase 10).

1. `main.py`: eliminar FastAPI, `CORSMiddleware`, `auth_middleware`,
   `StreamableHTTPSessionManager`, `StreamableHTTPASGIApp`, `lifespan`, `USERS`/`USERS_CONFIG`,
   los endpoints `/`, `/health`, `/api/health`, `/api/debug`, `/sse`, `/api/poll`,
   `/api/result` y el bloque `uvicorn.run`. Son solo 19 referencias, concentradas al inicio y
   al final del archivo.
2. Arranque: `mcp.run(transport="stdio")`. Renombrar el server a
   `FastMCP("Autodesk Inventor 2026 Assistant")`.
3. **`execute_in_inventor` se reescribe** como envío por pipe: abrir
   `\\.\pipe\InventorMCPBridge` con `open(..., 'r+b', buffering=0)`, escribir la línea JSON,
   leer la respuesta; `anyio.to_thread.run_sync` para no bloquear el event loop y
   `anyio.fail_after(timeout_seconds)` para conservar los timeouts por herramienta (15-60 s).
   Si el pipe no existe → error claro: "El MCP Bridge no está activo en Inventor: pulsa el
   botón MCP Bridge en la cinta".
4. **Los 52 cuerpos de las herramientas no se tocan**: declarar
   `current_user_id: ContextVar[str] = ContextVar("current_user_id", default="local")` y que
   `execute_in_inventor` ignore el primer argumento. Así las 52 llamadas y las 52 guardas
   `if not usuario` siguen funcionando sin edición. Limpieza opcional en una pasada posterior.
5. **Gotchas de stdio (críticos):**
   - Nada puede escribir en **stdout** salvo el protocolo JSON-RPC. Hoy no hay `print()` en
     `main.py`; hay que mantenerlo así. Los logs van a **stderr** o a fichero.
   - Claude Desktop lanza el proceso con cwd arbitrario y entorno mínimo: `load_dotenv()` no
     encontrará `.env`. Resolver rutas de config relativas a `__file__` / `sys.executable`.
   - Una excepción no capturada mata la sesión MCP: envolver el envío por pipe y devolver
     strings de error, como ya hace cada tool.
6. `requirements.txt`: quitar `fastapi`, `uvicorn`, `gunicorn`, `starlette`, `sse-starlette`,
   `Authlib`, `python-multipart`, `redis`/`fakeredis` y demás. Queda `fastmcp`, `mcp`,
   `pydantic`, `anyio` y sus transitivas.

## Fase 6 — Empaquetado y configuración de Claude Desktop  ✅ hecho

`inventor-mcp.exe`: 30,5 MB, un solo archivo, arranca y completa el handshake MCP en
~2,4 s (la descompresión de `--onefile` es la mayor parte). Receta versionada en
`mcp_server/inventor-mcp.spec`; se construye con `pyinstaller inventor-mcp.spec`.
`requirements-build.txt` fija PyInstaller 6.21.0. `mcp_server/build/` y `dist/` ignorados.

**El fallo previsto en el plan se materializó**, aunque no donde se esperaba: el exe moría
al arrancar con `ModuleNotFoundError: No module named 'burner_redis'`. FastMCP 2.14 arranca
siempre "docket" (su worker de tareas) en el lifespan, sin opción de desactivarlo, y con el
backend por defecto `memory://` éste importa `burner_redis` mediante `import_module`, que
PyInstaller no puede detectar. Resuelto con `collect_all('burner_redis')` +
`collect_submodules('docket')` + `copy_metadata('pydocket')`, documentado en el `.spec`.

El alta en Claude Desktop la hace **el propio servidor**, no el instalador
(`mcp_server/claude_config.py`, invocado con `--install-claude-config`): fusionar la entrada
con los `mcpServers` existentes exige un parser JSON de verdad, y escribirlo en Pascal
Script sería frágil. El instalador solo añade una casilla en `[Tasks]` y lo ejecuta desde
`[Run]`, con la baja correspondiente en `[UninstallRun]`. Modos disponibles:
`--install-claude-config`, `--remove-claude-config`, `--print-claude-config`.

**Verificación ejecutada:**

- *Exe como servidor MCP* (6 comprobaciones): handshake con stdout limpio, 52 tools,
  mensaje accionable con el bridge apagado, y cadena completa hasta el add-in C# con un
  payload de decimales y negativos.
- *Fusión del config de Claude Desktop* (10 comprobaciones, contra un `APPDATA` de prueba):
  conserva servidores preexistentes y claves ajenas, crea copia `.bak`, es idempotente al
  reinstalar, la baja deja intacto el resto, crea el archivo si no existía, y ante un JSON
  ilegible **no** sobrescribe y explica que hay que editarlo a mano.
- *Instalador*: compila incluyendo el exe (`Successful compile`).

**Ruido conocido, sin resolver:** al cerrarse, el exe congelado imprime en stderr
`ValueError: I/O operation on closed file` (dos veces). Viene de la finalización de
FastMCP/docket, no de nuestro código: no ocurre ejecutando `main.py` sin congelar, no
depende del nivel de log, y no se puede capturar con un `try/except` alrededor de
`mcp.run()` porque se lanza después. Es posterior al cierre de la sesión MCP y no afecta al
funcionamiento; aparecerá en el log de Claude Desktop al apagar el servidor.

1. Desarrollo: apuntar Claude Desktop al intérprete del venv.
2. Distribución: construir `inventor-mcp.exe` con **PyInstaller** (`--onefile --console`) para
   que la máquina del usuario no necesite Python. Verificar los imports dinámicos de
   `fastmcp`/`mcp` (hidden imports). Fallback si se atasca: Python + venv en cada máquina.
3. Snippet a documentar y, opcionalmente, a escribir desde el instalador en
   `%APPDATA%\Claude\claude_desktop_config.json`:

   ```json
   { "mcpServers": { "inventor": { "command": "C:\\...\\inventor-mcp.exe", "args": [] } } }
   ```

   Si lo escribe el instalador, debe **fusionar** con los `mcpServers` existentes, nunca
   sobrescribir el fichero.

## Fase 7 — `installer/setup.iss`  ✅ hecho

Verificado compilando con Inno Setup 6.7.3: `Successful compile`, sin warnings, empaquetando
los 3 archivos de salida. Además de lo planeado:

- **Corregido un bug de detección preexistente**: sin `ArchitecturesInstallIn64BitMode`, el
  instalador corre en modo 32 bits y `{pf}` resuelve a `Program Files (x86)`, donde Inventor
  nunca está. La detección automática no podía funcionar en ninguna máquina. Ahora se declara
  `ArchitecturesAllowed=x64compatible` y `ArchitecturesInstallIn64BitMode=x64compatible`
  (requiere Inno Setup >= 6.3; el identificador `x64` está deprecado en 6.7).
- Se comprueba la versión de `Bin\Inventor.exe` con `GetVersionNumbers` y se **avisa** (no se
  bloquea) si el major es < 30, por si el ejecutable no versiona como se espera.
- `config.json` se genera solo con `PipeName` y **solo si no existe**, para no pisar el valor
  en una actualización. Se añadió `[UninstallDelete]` porque el desinstalador no conoce los
  archivos creados desde `[Code]`.
- `inventor-mcp.exe` queda como TODO en `[Files]`: no existe hasta la Fase 6.

1. `SourcePath`: `..\inventor_plugin\bin\x64\Release\net48` → la nueva salida.
   `[Files]`: quitar `Newtonsoft.Json.dll`, añadir `inventor-mcp.exe`.
2. `Inventor 2024` → `Inventor 2026` en las 7 ocurrencias (líneas 13, 41, 69-71, 79, 117) y
   rechazar Inventor <= 2024.
3. Simplificar el wizard: desaparece la página que pide URL, API Key y User ID (`ConfigPage` y
   el `CurStepChanged` que genera `config.json`). Como mucho, un `config.json` con `PipeName`.
4. Añadir página opcional (checkbox) para configurar Claude Desktop, ver Fase 6.3.
5. No hay que instalar el .NET 8 Desktop Runtime: Inventor 2026 lo requiere y lo trae.

## Fase 8 — CI  ✅ hecho

`release.yml` reescrito (9 pasos, YAML validado): `setup-dotnet@v4` con 8.0.x,
`setup-python@v5` con 3.12, build del add-in, `pyinstaller inventor-mcp.spec`, Inno Setup vía
choco y publicación del release. `main_inventorassistant.yml` (deploy a Azure) eliminado.

Desviaciones y arreglos respecto a lo planeado:

- **`dotnet build` en lugar de `dotnet publish`**: el add-in no tiene dependencias NuGet, así
  que `publish` no añadiría nada y sí movería la salida a un subdirectorio distinto del que
  espera `setup.iss`.
- **Corregido un bug latente de versionado**: el workflow anterior pasaba el tag completo a
  `AssemblyVersion`/`FileVersion`, que solo aceptan números. Un tag de prerelease como
  `v1.2.0-beta` habría roto el build, pese a que el propio workflow contempla prereleases.
  Ahora se derivan dos valores: `VERSION` (completo, para el instalador) y `NUMERIC`
  (sin sufijo, para los atributos del ensamblado).
- La localización de `ISCC.exe` ya no depende de una ruta fija y falla con un mensaje claro
  si no aparece tras instalar Inno Setup.

**Pendiente manual:** borrar de la configuración del repositorio los secretos que usaba el
workflow de Azure (`AZUREAPPSERVICE_CLIENTID_…`, `AZUREAPPSERVICE_TENANTID_…`,
`AZUREAPPSERVICE_SUBSCRIPTIONID_…`). No se pueden eliminar desde el código.

1. `.github/workflows/release.yml`: quitar `microsoft/setup-msbuild` y el paso `msbuild`; usar
   `actions/setup-dotnet@v4` (`8.0.x`) + `dotnet publish -c Release -p:Platform=x64`.
2. Añadir job de PyInstaller (`windows-latest`, `setup-python` 3.12) que produzca
   `inventor-mcp.exe` y lo pase al paso de Inno Setup.
3. Eliminar `.github/workflows/main_inventorassistant.yml` (deploy a Azure) y sus secretos.

## Fase 9 — Documentación  ✅ hecho

`README.md` reescrito por completo (522 → 348 líneas): el ~70% del original trataba de Azure,
Copilot Studio, gestión de API keys y usuarios, así que parchear líneas sueltas no tenía
sentido. Secciones nuevas o rehechas: arquitectura stdio + named pipe, estructura del
proyecto, prerrequisitos, instalación en 4 pasos con el registro en Claude Desktop, generación
del instalador (CI y local, con los comandos verificados), desarrollo local con las variables
de entorno, **protocolo del bridge** (formato de línea, marshalling STA, aviso sobre varias
sesiones de Inventor), solución de problemas con los fallos nuevos —incluida una tabla de
HRESULT y el ruido conocido del exe— y una nota de migración que apunta a este documento.

La **referencia de tools pasó de 6 a las 52 reales**, con parámetros y descripción. No se
escribió a mano: se extrajo del servidor en marcha vía `tools/list` y se insertó en el README,
así que coincide exactamente con lo que ve Claude Desktop.

`.env.example` ya se había borrado en la Fase 5, y el nombre del servidor
(`FastMCP("Autodesk Inventor 2026 Assistant")`) se actualizó allí mismo.

## Fase 9 (plan original)

`README.md` (líneas 3, 16, 19, 41, 60-61, 201, 231, 240, 335, 398, 462, 519): nuevo diagrama
de arquitectura, Inventor 2026 / .NET 8, instrucciones de Claude Desktop en lugar de Copilot
Studio + Azure, y borrar la sección de API keys / multi-usuario. Reescribir o borrar
`.env.example`. `mcp_server/main.py:54` — `FastMCP("Autodesk Inventor 2024 Assistant")` → 2026.

## Batería de pruebas y CI continuo  ✅ hecho

Las verificaciones de las fases 3-6 se hicieron con scripts temporales; ahora viven en
`tests/` con rutas relativas y un orquestador. Ver `tests/README.md`.

- `tests/BridgeTest/` — `JsonPayload` y `BridgeService` (C#, 30 comprobaciones).
- `tests/PipeHost/` — no es una prueba: levanta el `BridgeService` real sobre el pipe
  `InventorMCPBridgeTests` para que lo ataquen las pruebas de Python.
- `tests/test_pipe_client.py`, `test_stdio.py`, `test_exe.py`, `test_claude_config.ps1` —
  las cuatro baterías descritas en las fases 5 y 6.
- `tests/test_readme_tools.py` — **nueva**: compara la tabla de tools del README con las
  tools reales del servidor y falla si se desincronizan; con `--update` la regenera. Es lo
  que mantiene honesta la documentación de las 52 herramientas.
- `tests/run_all.ps1` — compila add-in y proyectos de prueba, arranca el host esperando a
  que el pipe exista (sin `sleep` a ciegas), ejecuta las 6 baterías y resume.

Resultado local: **62 comprobaciones, 6 bloques, todo en verde, código de salida 0**.

`.github/workflows/tests.yml` ejecuta todo esto en cada push y pull request, y se declara
como `workflow_call` para que `release.yml` lo exija (`needs: tests`) antes de publicar un
instalador.

Dos detalles que costaron un intento:

- `$ErrorActionPreference = "Stop"` hacía fallar los scripts de PowerShell por un aviso de
  deprecación de authlib: en PowerShell 5.1, el stderr de un ejecutable nativo se convierte
  en `NativeCommandError` aunque el código de salida sea 0. Se usa `Continue` y el éxito se
  decide solo por el código de salida. Por lo mismo se quitaron los `2>$null`.
- Los proyectos de prueba usan `ProjectReference` al `.csproj` del add-in (así compilar las
  pruebas compila el add-in), pero necesitan el interop con `Private=True`: el add-in lo
  referencia con `Private=False` porque en producción lo provee Inventor, y sin copiarlo al
  output el CLR no puede resolver el tipo `BridgeService`.

**No verificado:** el workflow no se ha ejecutado en GitHub (aquí solo se validó su YAML).
Además, la batería se probó bajo Windows PowerShell 5.1; el CI la ejecuta con `pwsh` 7, que
no está instalado en la máquina de desarrollo. El primer run de Actions es la comprobación
real.

## Fase 10 — Verificación

1. `dotnet build -c Release -p:Platform=x64` sin errores ni warnings MSB3277/CS1701 sobre el interop.
2. Add-in cargado en Inventor 2026 (Add-In Manager); ribbon MCP visible en ZeroDoc, Part y
   Assembly, **con iconos** (valida Fase 4.1).
3. Botón ON → pipe creado. Comprobar con
   `[System.IO.Directory]::GetFiles("\\.\pipe\")` desde PowerShell.
4. Probar el pipe a mano antes de Claude Desktop: script Python que abra el pipe y mande
   `get_active_doc_info`.
5. Claude Desktop: el servidor aparece conectado, `tools/list` devuelve las 52 herramientas, y
   end-to-end de al menos parámetros (get/set), export STEP, captura de pantalla (valida
   `System.Drawing`), BOM de ensamblaje y sketch + extrusión (valida el normalizador de
   `payload`, Fase 4.2).
6. Casos de fallo: bridge apagado → mensaje claro; reiniciar Claude Desktop con Inventor
   abierto → reconexión limpia; cerrar Inventor con Claude Desktop abierto → error controlado;
   `Deactivate()` sin excepciones.

## Riesgos principales

1. **Marshalling STA** (Fase 3.5). Si se omite, los comandos fallan de forma intermitente e
   irreproducible. Conviene hacerlo bien de entrada.
2. **PyInstaller + FastMCP** (Fase 6.2). Los imports dinámicos suelen requerir ajustes; se
   decide en poco tiempo si hay que caer al fallback de Python + venv.
3. **Reconexiones del pipe** (Fase 3.2). Claude Desktop reinicia el proceso servidor con más
   frecuencia de lo esperable; el bucle de accept debe sobrevivir a desconexiones sucias.

## Referencias

- Migrating from .NET 4.8 to .NET Core 8 — https://blog.autodesk.io/migrating-from-net-48-to-net-core-8/
- Inventor 2025 Addin .NET8 (foro Autodesk) — https://forums.autodesk.com/t5/inventor-programming-forum/inventor-2025-addin-net8/td-p/13018674
- Migration addins to .Net core 8 (Inventor 2025), Jelte de Jong — http://www.hjalte.nl/tutorials/78-migrationinventor2025addins
- Guide to migrate existing Inventor Addin into .NET 8.0 Core — https://github.com/chandraRus/Autodesk-Desktop-NET8-Core
- Inventor 2025 Help, Creating an Add-In — https://help.autodesk.com/view/INVNTOR/2025/ENU/?guid=GUID-52422162-1784-4E8F-B495-CDB7BE9987AB
- Autodesk Inventor 2027 Add-In Isolation — https://tylerwarner.dev/assemblyloadcontext-for-inventor-2027-addins
- Inventor 2027 Entitlement & .NET 10 Migration — https://basautomationservices.com/blog/inventor-2027-net10-migration-entitlement/
- AxHost.GetIPictureDispFromPicture — https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.axhost.getipicturedispfrompicture?view=windowsdesktop-9.0
