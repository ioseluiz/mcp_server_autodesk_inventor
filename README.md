# Inventor MCP Bridge

Conecta **Autodesk Inventor 2024** con agentes de IA (Claude, Copilot Studio) a través del protocolo MCP (Model Context Protocol). Permite controlar Inventor mediante lenguaje natural: leer documentos, listar parámetros, modificarlos, exportar STEP, y crear geometría en sketches.

## Arquitectura

```
[Claude / Copilot Studio]
         │  MCP (SSE)
         ▼
[Servidor MCP - Python/FastAPI]  ← Azure Web Apps o localhost
         │  HTTP polling (cada 2s)
         ▼
[Plugin de Inventor - C# .NET 4.8]
         │  COM API
         ▼
[Autodesk Inventor 2024]
```

El plugin hace polling al servidor cada 2 segundos. Cuando el agente de IA envía un comando, el servidor lo encola; el plugin lo recoge, lo ejecuta en Inventor y devuelve el resultado.

## Estructura del proyecto

```
mcp_server_autodesk_inventor/
├── mcp_server/          # Servidor Python (FastAPI + FastMCP)
│   ├── main.py
│   └── requirements.txt
└── inventor_plugin/     # Plugin C# para Inventor 2024
    ├── StandardAddInServer.cs
    ├── Services/
    │   └── PollingService.cs
    ├── InventorMCPBridge.addin
    └── InventorMCPBridge.csproj
```

---

## Servidor MCP (Python)

### Requisitos

- Python 3.11+
- Autodesk Inventor 2024 no requerido en el servidor

### Configuración local

```bash
cd mcp_server
python -m venv venv
venv\Scripts\activate        # Windows
pip install -r requirements.txt
```

Crea un archivo `.env` (o usa variables de entorno):

```env
API_KEY=tu_clave_secreta
```

Inicia el servidor:

```bash
uvicorn main:app --reload --port 8000
```

El servidor queda disponible en:
- API REST: `http://localhost:8000`
- Endpoint MCP (SSE): `http://localhost:8000/sse`
- Health check: `http://localhost:8000/api/health`

### Despliegue en Azure Web Apps

1. Crea una Web App en Azure (Linux, Python 3.11).

2. Configura las variables de entorno en **Configuration → Application Settings**:

   | Nombre    | Valor              |
   |-----------|--------------------|
   | `API_KEY` | tu clave secreta   |

3. Despliega con Azure CLI:

   ```bash
   az webapp up --name tu-app --resource-group tu-rg --runtime "PYTHON:3.11"
   ```

   O desde VS Code con la extensión **Azure App Service**.

4. Configura el startup command en **Configuration → General Settings**:

   ```
   gunicorn -w 1 -k uvicorn.workers.UvicornWorker main:app
   ```

   > Se usa `-w 1` (1 worker) porque el estado de las colas es en memoria. Con múltiples workers, las tareas se perderían entre procesos.

5. El endpoint MCP quedará en:
   ```
   https://tu-app.azurewebsites.net/sse
   ```

### Endpoints disponibles

| Método | Ruta                       | Descripción                             |
|--------|----------------------------|-----------------------------------------|
| GET    | `/api/health`              | Health check (requiere API key)         |
| GET    | `/api/poll/{user_id}`      | El plugin recoge tareas pendientes      |
| POST   | `/api/result/{task_id}`    | El plugin devuelve el resultado         |
| GET    | `/sse`                     | Endpoint MCP para agentes de IA         |

---

## Plugin de Inventor (C#)

### Requisitos

- Autodesk Inventor 2024
- Visual Studio 2022
- .NET Framework 4.8 Developer Pack  
  → Descarga: https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48

### Configuración

Antes de compilar, edita `StandardAddInServer.cs` y actualiza estos valores en el constructor de `PollingService`:

```csharp
_pollingService = new PollingService(
    _inventorApp,
    "https://tu-app.azurewebsites.net",  // URL del servidor
    "tu_clave_secreta",                   // API_KEY (debe coincidir con el servidor)
    "tu_usuario");                         // ID de usuario para identificar esta sesión
```

Para desarrollo local cambia la URL a `http://localhost:8000`.

### Compilación

1. Abre `inventor_plugin/InventorMCPBridge.sln` en Visual Studio 2022.
2. Selecciona configuración **Debug** y plataforma **x64**.
3. Compila (`Ctrl+Shift+B`).

O desde terminal:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
  InventorMCPBridge.csproj /p:Platform=x64 /p:Configuration=Debug
```

### Instalación

Copia los archivos del output (`bin\x64\Debug\net48\`) a la carpeta de addins de Inventor:

```
%APPDATA%\Autodesk\Inventor 2024\Addins\InventorMCPBridge\
├── InventorMCPBridge.dll
├── InventorMCPBridge.addin
└── Newtonsoft.Json.dll
```

> No copies `Autodesk.Inventor.Interop.dll` — Inventor ya lo provee en runtime.

Para automatizar la copia en cada build, agrega al `.csproj`:

```xml
<Target Name="CopyToAddins" AfterTargets="Build">
  <Copy SourceFiles="$(OutputPath)InventorMCPBridge.dll"
        DestinationFolder="$(APPDATA)\Autodesk\Inventor 2024\Addins\InventorMCPBridge\" />
  <Copy SourceFiles="$(OutputPath)Newtonsoft.Json.dll"
        DestinationFolder="$(APPDATA)\Autodesk\Inventor 2024\Addins\InventorMCPBridge\" />
  <Copy SourceFiles="InventorMCPBridge.addin"
        DestinationFolder="$(APPDATA)\Autodesk\Inventor 2024\Addins\InventorMCPBridge\" />
</Target>
```

### Activación en Inventor

1. Abre Inventor 2024.
2. Ve a **Tools → Add-Ins**.
3. Busca **Inventor MCP Bridge** y marca **Load on Startup** y **Loaded Now**.
4. Aparecerá la pestaña **MCP** en el ribbon.
5. Haz click en **MCP Bridge: OFF** para activar el bridge.
   - Si el servidor está disponible, el botón cambia a **MCP Bridge: ON**.
   - Si no hay conexión, aparece un aviso con el error.

---

## Comandos MCP disponibles

| Tool MCP                   | Parámetros                                      | Descripción                              |
|----------------------------|-------------------------------------------------|------------------------------------------|
| `get_active_document_info` | `usuario`                                       | Info del documento activo                |
| `list_parameters`          | `usuario`                                       | Lista parámetros del documento           |
| `update_parameter`         | `usuario`, `name`, `value`                      | Modifica un parámetro                    |
| `export_to_step`           | `usuario`                                       | Exporta el documento a STEP              |
| `create_line`              | `usuario`, `x1`, `y1`, `x2`, `y2`              | Crea una línea en el sketch XY           |
| `create_circle`            | `usuario`, `center_x`, `center_y`, `radius`     | Crea un círculo en el sketch XY          |

El parámetro `usuario` debe coincidir con el `_userId` configurado en el plugin.

---

## Flujo completo de una operación

```
1. Agente IA llama tool MCP → execute_in_inventor("jlmunoz", "list_parameters", {})
2. Servidor crea task_id y encola la tarea en user_queues["jlmunoz"]
3. Plugin (polling cada 2s) recoge la tarea en GET /api/poll/jlmunoz
4. Plugin ejecuta ListParameters() en Inventor via COM
5. Plugin envía resultado a POST /api/result/{task_id}
6. Servidor desbloquea el await y devuelve el resultado al agente
```

---

## Solución de problemas

| Problema | Causa probable | Solución |
|---|---|---|
| Add-in bloqueado en Inventor | Error en `Activate()` | Revisa el MessageBox de error al cargar |
| Botón no aparece en ribbon | Error al crear el tab | Desinstala el addin, borra el perfil de UI de Inventor y reinstala |
| `FileNotFoundException` al cargar | DLL de dependencia faltante | Copia todos los DLLs del output al folder de addins |
| Timeout en comandos MCP | Plugin no está en ON o no hay conexión | Verifica que el botón esté en ON y que la URL sea correcta |
| Error 401 en polling | API Key incorrecta | Verifica que `API_KEY` en servidor y plugin coincidan |
