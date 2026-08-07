# Inventor MCP Bridge

Conecta **Autodesk Inventor 2026** con **Claude Desktop** a través del protocolo MCP (Model Context Protocol). Permite controlar Inventor mediante lenguaje natural: leer y modificar parámetros, crear bocetos y sólidos, aplicar operaciones, exportar STEP/STL/DXF, capturar la vista y extraer la lista de materiales.

Todo corre **en local**: no hay servidor en la nube, ni puertos abiertos, ni API keys.

---

## Arquitectura

```
[Claude Desktop]
         │  MCP sobre stdio  (lanza el proceso del servidor)
         ▼
[Servidor MCP — inventor-mcp.exe (Python/FastMCP)]
         │  named pipe  \\.\pipe\InventorMCPBridge  (JSON por líneas)
         ▼
[Add-in de Inventor — C# .NET 8]
         │  API COM, en la hebra principal de Inventor
         ▼
[Autodesk Inventor 2026]
```

**El add-in es el servidor del pipe y el proceso Python su cliente.** Claude Desktop relanza el servidor MCP en cada arranque o reconexión y puede dejar más de uno vivo, mientras que Inventor permanece abierto: el extremo estable tiene que ser Inventor. Esto además elimina el polling: cada comando se atiende en el momento.

El pipe no necesita autenticación porque su ACL por defecto ya limita el acceso al usuario que lo crea.

---

## Estructura del proyecto

```
mcp_server_autodesk_inventor/
├── .github/
│   └── workflows/
│       └── release.yml            # tag → build plugin + exe → instalador → GitHub Release
├── installer/
│   └── setup.iss                  # Inno Setup (instala sin admin, registra en Claude Desktop)
├── lib/
│   └── Autodesk.Inventor.Interop.dll   # Referencia COM para compilar sin Inventor (CI)
├── mcp_server/                    # Servidor MCP local
│   ├── main.py                    # Cliente del pipe + las 52 tools MCP
│   ├── claude_config.py           # Alta/baja en claude_desktop_config.json
│   ├── inventor-mcp.spec          # Receta de PyInstaller
│   ├── requirements.txt
│   └── requirements-build.txt     # Solo para empaquetar (PyInstaller)
├── inventor_plugin/               # Add-in C# para Inventor 2026
│   ├── Config/
│   │   └── PluginConfig.cs        # Lee config.json (solo PipeName)
│   ├── Services/
│   │   ├── BridgeService.cs       # Servidor del named pipe + ejecución de comandos COM
│   │   └── JsonPayload.cs         # JSON con System.Text.Json, sin dependencias externas
│   ├── StandardAddInServer.cs     # Punto de entrada del add-in y marshalling a la hebra STA
│   ├── InventorMCPBridge.addin    # Manifiesto XML del add-in
│   └── InventorMCPBridge.csproj
└── MIGRATION_NET8_STDIO.md        # Plan y bitácora de la migración a .NET 8 + stdio
```

---

## Prerrequisitos

| Componente | Requerido en |
|---|---|
| Autodesk Inventor 2026 | Cada máquina de usuario |
| Claude Desktop | Cada máquina de usuario |
| .NET 8 SDK | Solo para compilar el add-in |
| Python 3.12 | Solo para compilar o desarrollar el servidor |
| Inno Setup 6.3 o superior | Solo para compilar el instalador |
| Cuenta en GitHub | Para usar el pipeline de release |

No hace falta instalar el runtime de .NET 8: Inventor 2026 lo requiere y lo trae consigo. Los usuarios finales no necesitan Python: el servidor se distribuye como un único `.exe`.

> **Inventor 2024 y anteriores no son compatibles.** El add-in es .NET 8 e Inventor 2024 hospeda .NET Framework 4.8, así que no puede cargarlo. Inventor 2025 sí funciona, aunque las rutas de este README asumen 2026.

---

## Instalación

### 1. Ejecutar el instalador

Ejecuta `InventorMCPBridgeSetup-X.X.X.exe`. No requiere permisos de administrador.

Deja marcada la casilla **"Registrar el servidor MCP en Claude Desktop"** para que el instalador añada la entrada automáticamente. La entrada se **fusiona** con los servidores MCP que ya tengas configurados y se hace una copia de seguridad `.bak` del archivo.

Los archivos quedan en:

```
%APPDATA%\Autodesk\Inventor 2026\Addins\InventorMCPBridge\
├── InventorMCPBridge.dll
├── InventorMCPBridge.addin
├── InventorMCPBridge.deps.json
├── inventor-mcp.exe          ← servidor MCP que lanza Claude Desktop
└── config.json               ← solo PipeName; editable
```

### 2. Activar el add-in en Inventor

1. Abre Autodesk Inventor 2026.
2. Ve a **Tools → Add-Ins**.
3. Busca **Inventor MCP Bridge**, marca **Load on Startup** y **Loaded Now** → OK.
4. Aparece la pestaña **MCP** en el ribbon.

### 3. Reiniciar Claude Desktop

Claude Desktop lee su configuración al arrancar. Tras reiniciarlo, el servidor `inventor` debe aparecer conectado en sus ajustes de MCP.

### 4. Verificar

1. En Inventor, pulsa **MCP Bridge: OFF** en la pestaña MCP. El botón pasa a **MCP Bridge: ON** y el pipe queda abierto.
2. En Claude Desktop, pregunta: *"¿Qué documento tengo abierto en Inventor?"*

Si el bridge está apagado, las herramientas responden con un mensaje explícito pidiéndote que lo actives.

### Registro manual en Claude Desktop

Si prefieres no usar la casilla del instalador:

```powershell
& "$env:APPDATA\Autodesk\Inventor 2026\Addins\InventorMCPBridge\inventor-mcp.exe" --install-claude-config
```

O consulta el fragmento a pegar en `%APPDATA%\Claude\claude_desktop_config.json`:

```powershell
& "...\inventor-mcp.exe" --print-claude-config
```

Para darlo de baja: `--remove-claude-config` (es lo que hace el desinstalador).

---

## Generar el instalador

### Opción A — Pipeline de release (recomendado)

```bash
git tag v1.0.0
git push origin v1.0.0
```

El workflow compila el add-in en Release x64, empaqueta `inventor-mcp.exe` con PyInstaller, compila el instalador con Inno Setup y publica un GitHub Release con el `.exe` adjunto. Los tags con guion (`v1.2.0-beta`) se marcan como pre-release automáticamente.

### Opción B — Compilar localmente

```powershell
# 1. Add-in (.NET 8, x64)
dotnet build inventor_plugin\InventorMCPBridge.csproj -c Release -p:Platform=x64

# 2. Servidor MCP como ejecutable único
cd mcp_server
python -m venv venv
venv\Scripts\pip install -r requirements.txt -r requirements-build.txt
venv\Scripts\pyinstaller --noconfirm inventor-mcp.spec
cd ..

# 3. Instalador
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /DAppVersion=1.0.0 installer\setup.iss
# Queda en: installer\output\InventorMCPBridgeSetup-1.0.0.exe
```

> Si compilas sin Inventor instalado, el `.csproj` usa el interop de `lib/`. Ese archivo debe ser el de Inventor 2026 (versión 30.x): con el de otra versión el build pasa igualmente —un interop COM solo aporta definiciones de tipos— pero no valida la API real.

---

## Desarrollo local

### Servidor MCP

```powershell
cd mcp_server
python -m venv venv
venv\Scripts\pip install -r requirements.txt
```

Durante el desarrollo puedes apuntar Claude Desktop al intérprete del venv en lugar del `.exe`; el fragmento correcto lo genera el propio script:

```powershell
venv\Scripts\python main.py --print-claude-config
```

Variables de entorno reconocidas (van en el bloque `env` de `claude_desktop_config.json`):

| Variable | Por defecto | Para qué |
|---|---|---|
| `INVENTOR_PIPE_NAME` | `InventorMCPBridge` | Nombre del pipe; debe coincidir con el `PipeName` del add-in |
| `INVENTOR_MCP_LOG_LEVEL` | `INFO` | Nivel de log (va siempre a stderr) |

> Con transporte stdio, **stdout está reservado al protocolo JSON-RPC**. Cualquier `print()` en el servidor rompe la sesión MCP: los diagnósticos van a stderr o a un archivo.

### Add-in C#

1. Abre `inventor_plugin/InventorMCPBridge.sln` en Visual Studio 2022 (17.8 o superior).
2. Configura **Debug | x64** y compila.
3. Copia el contenido de `bin\x64\Debug\` a `%APPDATA%\Autodesk\Inventor 2026\Addins\InventorMCPBridge\`.
4. Si necesitas otro nombre de pipe, crea `config.json` en esa carpeta:

   ```json
   { "PipeName": "InventorMCPBridge" }
   ```

Para depurar, adjunta el depurador a `Inventor.exe` después de que cargue el add-in.

---

## Protocolo del bridge

Petición y respuesta son **una línea de JSON cada una**, codificadas en UTF-8 y terminadas en `\n`:

```jsonc
// →  del servidor MCP al add-in
{"id": "1", "command": "get_active_doc_info", "payload": {}}

// ←  del add-in al servidor MCP
{"id": "1", "result": { "Status": "ok", "DocumentName": "pieza.ipt" }, "error": null}
```

- Si el comando falla, `result` es `null` y `error` trae el mensaje; los errores COM incluyen su HRESULT.
- Las respuestas pueden pesar varios MB (`render_screenshot` devuelve el PNG en base64) y viajan en una sola línea.
- El add-in devuelve cada comando a la **hebra principal de Inventor** antes de tocar la API COM, mediante un control oculto creado en `Activate()`. Sin eso, las llamadas desde la hebra del pipe fallan de forma intermitente con `RPC_E_CALL_REJECTED`.
- Los comandos se ejecutan de uno en uno, así que el cliente reutiliza una sola conexión.

**Varias sesiones de Inventor a la vez:** dos instancias pueden abrir el mismo nombre de pipe y el servidor MCP se conectaría a cualquiera de ellas indistintamente. Si necesitas usarlas en paralelo, asigna un `PipeName` distinto en el `config.json` de cada una y pasa el mismo valor en `INVENTOR_PIPE_NAME`.

---

## Referencia de tools MCP

52 herramientas. `param?` indica parámetro opcional.

| Tool | Parámetros | Descripción |
|---|---|---|
| `get_active_document_info` | — | Obtiene información básica del documento activo en Inventor. |
| `list_parameters` | — | Lista todos los parámetros del documento activo. |
| `update_parameter` | name, value | Actualiza el valor de un parámetro en el documento activo. |
| `export_to_step` | path? | Exporta el documento activo a un archivo STEP para fabricación o interoperabilidad. |
| `create_line` | x1, x2, y1, y2 | Crea una línea en un boceto (sketch) en el plano XY del documento activo. |
| `create_circle` | center_x, center_y, radius | Crea un círculo en un boceto (sketch) en el plano XY del documento activo. |
| `create_new_part` | units? | Crea un nuevo documento de pieza (.ipt) en Inventor a partir de una plantilla. |
| `create_new_assembly` | units? | Crea un nuevo documento de ensamble (.iam) en Inventor a partir de una plantilla. |
| `open_document` | path | Abre un documento de Inventor existente desde una ruta de archivo completa. |
| `save_document` | path? | Guarda el documento activo en Inventor. |
| `change_units` | units | Cambia las unidades de longitud del documento activo en Inventor. |
| `set_material` | material_name | Asigna un material de la biblioteca de Inventor a la pieza activa. |
| `create_sketch` | name?, plane? | Crea un nuevo boceto 2D en el plano de origen especificado. |
| `draw_rectangle` | cx?, cy?, mode?, px?, py?, x1?, x2?, y1?, y2? | Dibuja un rectángulo en el boceto activo. |
| `draw_arc` | clockwise?, cx?, cy?, mode?, x1?, x2?, x3?, y1?, y2?, y3? | Dibuja un arco en el boceto activo. |
| `draw_slot` | cx1, cx2, cy1, cy2, width | Dibuja una ranura (slot) recta en el boceto activo por dos centros y ancho. |
| `add_sketch_dimension` | driven?, entity1?, entity2?, entity_index?, orientation?, text_x?, text_y?, type, units?, value? | Agrega una cota paramétrica a una entidad del boceto activo. |
| `add_sketch_constraint` | entity1?, entity2?, entity_index?, type | Aplica una restricción geométrica a entidades del boceto activo. |
| `project_geometry` | source? | Proyecta geometría existente al boceto activo como referencias. |
| `close_sketch` | — | Cierra el boceto activo y lo deja listo para operaciones 3D (extrusión, revolución, etc.). |
| `extrude_profile` | direction?, distance, operation?, units? | Extruye el perfil cerrado del boceto activo para crear o modificar un sólido. |
| `revolve_profile` | angle?, axis?, direction?, operation? | Crea un sólido de revolución girando el perfil del boceto activo alrededor de un eje. |
| `sweep_profile` | operation?, path_sketch | Crea un sólido de barrido moviendo el perfil del boceto activo a lo largo de una trayectoria. |
| `loft_profiles` | operation?, sketches | Crea un sólido de transición (loft) entre dos o más perfiles en bocetos distintos. |
| `create_hole` | cbore_depth?, cbore_diameter?, csink_angle?, csink_diameter?, depth?, diameter, hole_type?, through?, units? | Coloca agujeros en el sólido usando las posiciones del boceto activo. |
| `add_fillet` | edge_indices, radius, units? | Aplica un redondeo (fillet) de radio constante a una o más aristas del sólido activo. |
| `add_chamfer` | angle?, distance, edge_indices, units? | Aplica un chaflán (chamfer) a una o más aristas del sólido activo. |
| `shell_solid` | direction?, face_indices?, thickness, units? | Vacía el sólido activo dejando una pared delgada de espesor constante (Shell). |
| `thread_feature` | cosmetic?, designation?, face_index, full_length?, length?, right_handed?, thread_type?, units? | Aplica una rosca cosmética o física a una cara cilíndrica del sólido activo. |
| `split_body` | keep_both?, plane? | Divide el cuerpo sólido activo usando un plano de trabajo. |
| `combine_bodies` | base_body?, operation?, tool_bodies? | Realiza una operación booleana entre cuerpos sólidos independientes de la pieza activa. |
| `get_parameters` | — | Lista todos los parámetros del modelo activo, categorizados por tipo. |
| `set_parameter_value` | expression?, name, units?, value?, value_type? | Cambia el valor de un parámetro existente y fuerza la actualización del modelo. |
| `add_custom_parameter` | name, units?, value?, value_type? | Crea un nuevo parámetro de usuario (UserParameter) en el documento activo. |
| `update_iproperties` | author?, description?, keywords?, part_number?, subject?, title? | Modifica los metadatos (iProperties) del documento activo en Inventor. |
| `create_work_plane` | angle?, axis?, mode?, offset?, plane?, point1?, point2?, point3?, units? | Crea un plano de trabajo paramétrico en la pieza activa. |
| `create_work_axis` | face_index?, mode?, plane1?, plane2?, point1?, point2? | Crea un eje de trabajo paramétrico en la pieza activa. |
| `create_work_point` | edge_index?, mode?, plane1?, plane2?, plane3?, units?, x?, y?, z? | Crea un punto de trabajo paramétrico de referencia en la pieza activa. |
| `render_screenshot` | height?, width? | Captura la vista actual de Inventor como imagen PNG codificada en base64. |
| `export_to_stl` | path? | Exporta el modelo activo (pieza o ensamble) a un archivo STL para impresión 3D. |
| `export_to_dxf` | path? | Exporta la cara de una chapa metálica desplegada o un dibujo (drawing) a DXF. |
| `check_interference` | — | Detecta colisiones geométricas entre componentes del ensamble activo. |
| `get_mass_properties` | — | Calcula y devuelve las propiedades de masa del modelo activo. |
| `insert_component` | file_path | Inserta una pieza (.ipt) o subensamble (.iam) en el ensamble activo. |
| `ground_component` | ground?, occurrence | Fija (o libera) un componente en el espacio para que sirva como base fija del ensamble. |
| `add_assembly_constraint` | axes_opposed?, constraint_type?, face1?, face2?, inside?, occurrence1, occurrence2, units?, value? | Aplica una restricción de ensamble tradicional entre dos componentes. |
| `add_assembly_joint` | face1?, face2?, joint_type?, occurrence1, occurrence2 | Conecta dos componentes con un Joint del sistema moderno de ensamble de Inventor. |
| `get_assembly_bom` | — | Extrae la lista de materiales (BOM) del ensamble activo. |
| `create_rectangular_pattern` | feature_names?, units?, x_axis?, x_count?, x_spacing?, y_axis?, y_count?, y_spacing? | Crea un patrón rectangular (arreglo de filas y columnas) de una o más operaciones. |
| `create_circular_pattern` | angle?, axis?, count?, feature_names?, fit_within_angle? | Crea un patrón circular de una operación alrededor de un eje de trabajo. |
| `mirror_feature` | feature_names?, plane? | Realiza la simetría (espejo) de una o más operaciones respecto a un plano de trabajo. |
| `mirror_solid` | keep_original?, plane? | Realiza la simetría (espejo) de todo el cuerpo sólido respecto a un plano de trabajo. |

---

## Solución de problemas

### El add-in no aparece en el ribbon de Inventor

- Comprueba que los archivos están en `%APPDATA%\Autodesk\Inventor 2026\Addins\InventorMCPBridge\` y que el `.addin` acompaña al `.dll`.
- Ve a **Tools → Add-Ins** y actívalo manualmente; reinicia Inventor.
- Si usas Inventor 2024 o anterior, el add-in **no puede cargar**: es .NET 8. El manifiesto declara `SupportedSoftwareVersionGreaterThan 29..`, así que Inventor lo oculta.

### El botón "MCP Bridge: OFF" no cambia a ON

Ahora el add-in solo abre el pipe, no se conecta a nada, así que un fallo aquí es local: aparece un MessageBox con el motivo (nombre de pipe inválido o permisos). Revisa el `PipeName` de `config.json`.

### Claude Desktop no ve el servidor

- Reinicia Claude Desktop: lee la configuración al arrancar.
- Verifica que la entrada existe y apunta a un ejecutable que existe:
  ```powershell
  Get-Content "$env:APPDATA\Claude\claude_desktop_config.json"
  ```
- Ejecuta el servidor a mano para ver sus errores en stderr:
  ```powershell
  & "...\inventor-mcp.exe" --print-claude-config
  ```

### Las tools responden "El MCP Bridge no está activo en Inventor"

El pipe no existe: Inventor está cerrado, el add-in no cargó, o el botón del ribbon está en OFF. Compruébalo:

```powershell
[System.IO.Directory]::GetFiles("\\.\pipe\") | Select-String InventorMCPBridge
```

Si el nombre que aparece no es el que espera el servidor, ajusta `PipeName` o `INVENTOR_PIPE_NAME`.

### Una tool devuelve "Timeout: Inventor no respondió en Ns"

El comando tardó más que su límite (entre 15 y 60 s según la tool). Suele significar que Inventor está ocupado o mostrando un diálogo modal que bloquea su hebra principal: atiéndelo y vuelve a intentarlo.

### Errores COM con HRESULT

El mensaje incluye el código, que es lo que permite identificarlos:

| HRESULT | Significado habitual |
|---|---|
| `0x80010001` (RPC_E_CALL_REJECTED) | Inventor rechazó la llamada por estar ocupado |
| `0x80004005` (E_FAIL) | La operación no es válida en el estado actual del documento |
| `0x800A01A8` | Se usó un objeto que ya no existe (documento cerrado) |

### `ValueError: I/O operation on closed file` en el log al cerrar

Ruido conocido e inocuo del ejecutable empaquetado: aparece al apagarse, después de cerrarse la sesión MCP, y proviene de la finalización de FastMCP, no de este código. No ocurre ejecutando `main.py` sin empaquetar.

---

## Migración desde la versión en la nube

Las versiones anteriores usaban un servidor FastAPI en Azure, transporte MCP sobre HTTP/SSE para Copilot Studio, autenticación por API key con soporte multiusuario y polling HTTP cada 2 segundos desde el plugin. Todo eso se eliminó al pasar a local.

**Copilot Studio ya no es un cliente posible**: no habla stdio. Si lo necesitas, la ruta en la nube sigue en el historial de git.

El plan detallado de la migración, con las decisiones tomadas y las verificaciones ejecutadas, está en [`MIGRATION_NET8_STDIO.md`](MIGRATION_NET8_STDIO.md).
