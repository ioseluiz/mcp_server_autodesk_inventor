# Inventor MCP Bridge

Conecta **Autodesk Inventor 2024** con agentes de IA (Copilot Studio, Claude) a través del protocolo MCP (Model Context Protocol). Permite controlar Inventor mediante lenguaje natural: leer documentos, listar y modificar parámetros, exportar STEP y crear geometría en sketches.

---

## Arquitectura

```
[Copilot Studio / Claude]
         │  MCP sobre SSE  (x-api-key por usuario)
         ▼
[Servidor MCP — Python/FastAPI]  ←  Azure Web App (Linux, Python 3.11)
         │  HTTP polling cada 2 s  (x-api-key del usuario)
         ▼
[Plugin de Inventor — C# .NET 4.8]
         │  COM API
         ▼
[Autodesk Inventor 2024]
```

El plugin hace polling al servidor cada 2 segundos. Cuando el agente de IA envía un comando, el servidor lo encola; el plugin lo recoge, lo ejecuta en Inventor y devuelve el resultado. **Cada usuario tiene su propia API key** que aísla sus colas de otros usuarios.

---

## Estructura del proyecto

```
mcp_server_autodesk_inventor/
├── .github/
│   └── workflows/
│       └── release.yml          # Pipeline CI/CD: tag → build → installer → GitHub Release
├── installer/
│   └── setup.iss                # Script Inno Setup (instala sin requerir admin)
├── lib/
│   └── Autodesk.Inventor.Interop.dll  # Referencia COM para compilar en CI
├── mcp_server/                  # Servidor Python (se despliega en Azure)
│   ├── main.py
│   ├── requirements.txt
│   └── .env.example
└── inventor_plugin/             # Plugin C# para Inventor 2024
    ├── Config/
    │   └── PluginConfig.cs      # Lee config.json desde la carpeta del plugin
    ├── Services/
    │   └── PollingService.cs    # Polling HTTP + ejecución de comandos COM
    ├── StandardAddInServer.cs   # Punto de entrada del add-in
    ├── InventorMCPBridge.addin  # Manifiesto XML del add-in
    └── InventorMCPBridge.csproj
```

---

## Prerrequisitos

| Componente | Requerido en |
|---|---|
| Azure subscription con permisos para crear Web Apps | Servidor |
| Python 3.11+ | Servidor (local) |
| Azure CLI (`az`) o VS Code + extensión Azure App Service | Despliegue |
| Autodesk Inventor 2024 | Cada máquina de usuario |
| Visual Studio 2022 + .NET Framework 4.8 Dev Pack | Solo si compilas el plugin manualmente |
| Inno Setup 6 | Solo si compilas el instalador manualmente |
| Cuenta en GitHub | Para usar el pipeline CI/CD |

---

## Guía de despliegue paso a paso

### Paso 1 — Desplegar el servidor en Azure

#### 1.1 Crear la Web App en Azure

En Azure Portal o con Azure CLI:

```bash
# Crear grupo de recursos (si no tienes uno)
az group create --name rg-inventor-mcp --location eastus

# Crear el plan de servicio (F1 = gratuito, B1 = básico recomendado para producción)
az appservice plan create \
  --name plan-inventor-mcp \
  --resource-group rg-inventor-mcp \
  --sku B1 \
  --is-linux

# Crear la Web App
az webapp create \
  --name tu-app-inventor-mcp \
  --resource-group rg-inventor-mcp \
  --plan plan-inventor-mcp \
  --runtime "PYTHON:3.11"
```

> El nombre de la app determina la URL: `https://tu-app-inventor-mcp.azurewebsites.net`

#### 1.2 Configurar el startup command

En **Azure Portal → tu Web App → Configuration → General Settings → Startup Command**:

```
gunicorn -w 1 -k uvicorn.workers.UvicornWorker main:app
```

> Se usa `-w 1` (un solo worker) porque el estado de las colas es en memoria. Con múltiples workers las tareas se perderían entre procesos.

#### 1.3 Desplegar el código

El servidor sólo necesita la carpeta `mcp_server/`. Despliega su contenido directamente:

```bash
cd mcp_server
az webapp up \
  --name tu-app-inventor-mcp \
  --resource-group rg-inventor-mcp \
  --runtime "PYTHON:3.11"
```

O desde VS Code: instala la extensión **Azure App Service**, haz clic derecho en la carpeta `mcp_server/` y elige **Deploy to Web App**.

#### 1.4 Verificar el despliegue

```bash
curl https://tu-app-inventor-mcp.azurewebsites.net/health
# → {"status":"ok"}
```

Si el endpoint responde, el servidor está corriendo. Si hay error 500, revisa los logs:

```bash
az webapp log tail --name tu-app-inventor-mcp --resource-group rg-inventor-mcp
```

---

### Paso 2 — Configurar los usuarios

El servidor usa un mapa `api_key → user_id` para aislar a cada usuario. Debes configurarlo **antes** de distribuir el instalador, porque cada usuario necesita su API key.

#### 2.1 Decidir las credenciales de cada usuario

Para cada persona que usará el sistema, define:

| Usuario | API Key (secreta, larga) | User ID (identificador corto) |
|---|---|---|
| Juan | `a8f3k2m9p1q7r4s6t0u5v2w8x1y3z9` | `juan` |
| María | `b2c5d8e1f4g7h0i3j6k9l2m5n8o1p4` | `maria` |

- La **API key** debe ser una cadena larga y aleatoria (usa un generador de contraseñas, mínimo 20 caracteres).
- El **User ID** puede ser cualquier string corto que identifique al usuario.

#### 2.2 Configurar `USERS_CONFIG` en Azure

En **Azure Portal → tu Web App → Configuration → Application Settings**, agrega:

| Nombre | Valor |
|---|---|
| `USERS_CONFIG` | `{"a8f3k2m9p1q7r4s6t0u5v2w8x1y3z9":"juan","b2c5d8e1f4g7h0i3j6k9l2m5n8o1p4":"maria"}` |

Haz clic en **Save** y espera a que la app se reinicie (~30 segundos).

Verifica con la API key de Juan:

```bash
curl -H "x-api-key: a8f3k2m9p1q7r4s6t0u5v2w8x1y3z9" \
  https://tu-app-inventor-mcp.azurewebsites.net/api/health
# → {"status":"ok","user_id":"juan","message":"Servidor MCP activo"}
```

> **Modo un solo usuario (alternativa simple):** Si sólo hay un usuario, puedes usar `API_KEY=mi_clave` y `USER_ID=mi_usuario` en lugar de `USERS_CONFIG`.

---

### Paso 3 — Generar el instalador del plugin

Tienes dos opciones: usar el pipeline CI/CD (recomendado) o compilar localmente.

#### Opción A — Pipeline CI/CD (GitHub Actions)

El pipeline genera automáticamente un instalador `.exe` cada vez que publicas un tag de versión:

```bash
git tag v1.0.0
git push origin v1.0.0
```

Esto dispara el pipeline en GitHub Actions que:
1. Compila el plugin en modo Release x64
2. Empaqueta el instalador con Inno Setup
3. Crea un GitHub Release con el archivo `InventorMCPBridgeSetup-1.0.0.exe`

Descarga el `.exe` desde la pestaña **Releases** de tu repositorio en GitHub.

> Para que el pipeline funcione, el repositorio debe estar en GitHub y tener Actions habilitado.

#### Opción B — Compilar localmente

Requiere Visual Studio 2022 e Inno Setup 6 instalados:

```powershell
# 1. Compilar el plugin en Release
dotnet build "inventor_plugin/InventorMCPBridge.sln" -c Release -p:Platform=x64

# 2. Compilar el instalador
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=1.0.0 installer\setup.iss

# El instalador queda en: installer\output\InventorMCPBridgeSetup-1.0.0.exe
```

---

### Paso 4 — Instalar el plugin en cada equipo

#### 4.1 Ejecutar el instalador

Comparte el archivo `InventorMCPBridgeSetup-X.X.X.exe` con cada usuario. El usuario lo ejecuta en su máquina:

1. Doble clic en el `.exe`.
2. El instalador **no requiere credenciales de administrador** — se instala en la carpeta del usuario.
3. Aparece una pantalla de configuración con tres campos:

   | Campo | Qué ingresar |
   |---|---|
   | **URL del servidor** | `https://tu-app-inventor-mcp.azurewebsites.net` |
   | **API Key** | La API key personal del usuario (ej. `a8f3k2m9p1q7r4s6t0u5v2w8x1y3z9`) |
   | **ID de usuario** | El user_id que corresponde a esa key (ej. `juan`) |

4. Clic en Siguiente → Instalar.

El instalador copia los archivos a:
```
%APPDATA%\Autodesk\Inventor 2024\Addins\InventorMCPBridge\
├── InventorMCPBridge.dll
├── InventorMCPBridge.addin
├── Newtonsoft.Json.dll
└── config.json          ← generado con los valores ingresados
```

#### 4.2 Activar el plugin en Inventor

1. Abre Autodesk Inventor 2024.
2. Ve a **Tools → Add-Ins**.
3. Busca **Inventor MCP Bridge** en la lista.
4. Marca **Load on Startup** y **Loaded Now** → OK.
5. Aparece la pestaña **MCP** en el ribbon.
6. Haz clic en **MCP Bridge: OFF** para conectar.
   - Si la conexión es exitosa, el botón cambia a **MCP Bridge: ON**.
   - Si falla, aparece un cuadro de error con el detalle (URL incorrecta, API key inválida, etc.).

> Si el add-in no aparece en la lista, reinicia Inventor. Si el error persiste, revisa la sección de Solución de problemas.

---

### Paso 5 — Configurar el agente en Copilot Studio

> **Importante:** En Copilot Studio, la API key del conector MCP es estática por agente. Para que cada usuario controle su propio Inventor, **cada usuario debe tener su propio agente** configurado con su API key personal. Un agente compartido con una sola key sólo puede controlar el Inventor del usuario al que pertenece esa key.

#### 5.1 Crear el agente

1. Accede a [Copilot Studio](https://copilotstudio.microsoft.com).
2. Crea un nuevo agente o abre uno existente.
3. En la descripción o instrucciones del sistema, explica el propósito:
   > "Eres un asistente de ingeniería que controla Autodesk Inventor. Puedes leer y modificar parámetros de diseño, exportar modelos y crear geometría."

#### 5.2 Agregar el tool MCP

1. En el panel del agente, ve a **Tools** (o **Actions**, según la versión) → **Add a tool**.
2. Selecciona **Model Context Protocol (MCP)**.
3. Ingresa la URL del servidor:
   ```
   https://tu-app-inventor-mcp.azurewebsites.net/sse
   ```
4. En **Authentication**, selecciona **API Key** y configura:
   | Campo | Valor |
   |---|---|
   | Header name | `x-api-key` |
   | API Key | La API key personal del usuario (ej. `a8f3k2m9p1q7r4s6t0u5v2w8x1y3z9`) |

5. Guarda. Copilot Studio descubrirá automáticamente los tools disponibles:
   - `get_active_document_info`
   - `list_parameters`
   - `update_parameter`
   - `export_to_step`
   - `create_line`
   - `create_circle`

#### 5.3 Habilitar los tools en el agente

Algunos tools requieren confirmación explícita del usuario antes de ejecutarse (especialmente los que modifican el modelo). Configura esto según tu caso de uso en **Tools → [nombre del tool] → Settings**.

---

### Paso 6 — Verificar que todo funciona

Con Inventor abierto, el plugin en ON, y el agente configurado en Copilot Studio, prueba el flujo completo:

1. **Verificar conectividad** — el botón del ribbon dice "MCP Bridge: ON".

2. **Probar un comando de lectura** — en el chat del agente de Copilot Studio:
   > "¿Qué documento tengo abierto en Inventor?"

   El agente debería devolver el nombre y ruta del archivo activo.

3. **Probar un comando de modificación:**
   > "Lista los parámetros del documento actual"

   Deberías ver la lista de parámetros con sus valores.

4. **Probar una modificación:**
   > "Cambia el parámetro Ancho a 150 mm"

---

## Gestión de usuarios

### Agregar un nuevo usuario

1. Genera una API key segura para el nuevo usuario (mínimo 20 caracteres aleatorios).
2. Actualiza `USERS_CONFIG` en Azure Application Settings añadiendo la nueva entrada:
   ```json
   {"clave_juan":"juan","clave_maria":"maria","clave_pedro":"pedro"}
   ```
3. Guarda — la app se reinicia automáticamente (~30 s).
4. Envía al nuevo usuario el instalador y sus credenciales (`URL`, `API Key`, `User ID`).

### Revocar acceso a un usuario

1. Elimina la entrada de ese usuario en `USERS_CONFIG`.
2. Guarda — el plugin del usuario recibirá 401 en el próximo ciclo de polling y dejará de funcionar.

### Cambiar la API key de un usuario

1. Actualiza la entrada en `USERS_CONFIG` con la nueva key (mismo `user_id`).
2. El usuario debe reinstalar el plugin o editar manualmente su `config.json`:
   ```
   %APPDATA%\Autodesk\Inventor 2024\Addins\InventorMCPBridge\config.json
   ```

---

## Generar releases (pipeline CI/CD)

El pipeline `.github/workflows/release.yml` genera un instalador firmado cada vez que se publica un tag:

```bash
# Publicar versión 1.2.0
git tag v1.2.0
git push origin v1.2.0
```

El pipeline:
1. Compila el plugin en modo **Release x64** con la versión del tag.
2. Compila el instalador `.exe` con Inno Setup.
3. Crea un **GitHub Release** y adjunta el instalador como asset descargable.

Los tags con guion se marcan como pre-release automáticamente (ej. `v1.2.0-beta`).

---

## Desarrollo local

### Servidor Python

```bash
cd mcp_server
python -m venv venv
venv\Scripts\activate      # Windows
pip install -r requirements.txt
```

Crea `.env` basado en `.env.example`:

```env
# Modo multi-usuario
USERS_CONFIG={"mi_clave_local":"mi_usuario"}

# O modo un usuario
# API_KEY=mi_clave_local
# USER_ID=mi_usuario
```

Inicia el servidor:

```bash
uvicorn main:app --reload --port 8000
```

El servidor queda en `http://localhost:8000`. En el instalador o en `config.json` del plugin, usa esta URL durante desarrollo.

### Plugin C#

Para compilar y depurar sin usar el instalador:

1. Abre `inventor_plugin/InventorMCPBridge.sln` en Visual Studio 2022.
2. Configura **Debug | x64**.
3. Compila (`Ctrl+Shift+B`).
4. Copia manualmente los archivos del output a:
   ```
   %APPDATA%\Autodesk\Inventor 2024\Addins\InventorMCPBridge\
   ```
5. Crea o edita `config.json` en esa carpeta:
   ```json
   {
     "ServerUrl": "http://localhost:8000",
     "ApiKey": "mi_clave_local",
     "UserId": "mi_usuario"
   }
   ```

---

## Referencia de API

### Endpoints

| Método | Ruta | Auth | Descripción |
|---|---|---|---|
| GET | `/health` | No | Sonda de salud para Azure (siempre pública) |
| GET | `/api/health` | Sí | Health check para el plugin; devuelve `user_id` autenticado |
| GET | `/api/poll/{user_id}` | Sí | El plugin recoge su siguiente tarea pendiente |
| POST | `/api/result/{task_id}` | Sí | El plugin entrega el resultado de una tarea |
| GET | `/sse` | Sí | Endpoint MCP (SSE) para Copilot Studio |

Todos los endpoints autenticados requieren el header `x-api-key: <api_key_del_usuario>`.

### Tools MCP disponibles

| Tool | Parámetros | Descripción |
|---|---|---|
| `get_active_document_info` | — | Info del documento activo (nombre, ruta, tipo, unidades) |
| `list_parameters` | — | Lista todos los parámetros del documento |
| `update_parameter` | `name`, `value` | Modifica el valor de un parámetro |
| `export_to_step` | — | Exporta el documento a STEP en la carpeta temporal |
| `create_line` | `x1`, `y1`, `x2`, `y2` | Crea una línea en el sketch XY |
| `create_circle` | `center_x`, `center_y`, `radius` | Crea un círculo en el sketch XY |

> El `user_id` ya **no es un parámetro** de los tools — se deriva automáticamente de la API key con la que Copilot Studio se conectó al servidor.

---

## Flujo completo de una operación

```
1. Juan habla con su agente en Copilot Studio
2. El agente llama al tool MCP list_parameters()
   → el servidor extrae user_id="juan" de su API key
3. Servidor crea un task_id (UUID) y encola la tarea en user_queues["juan"]
4. Plugin de Juan (polling /api/poll/juan cada 2s) recoge la tarea
   → el servidor valida que su API key corresponde a "juan"
5. Plugin ejecuta ListParameters() en Inventor via COM
6. Plugin envía resultado a POST /api/result/{task_id}
7. Servidor desbloquea el await y devuelve los parámetros al agente
8. El agente presenta la información a Juan en lenguaje natural
```

---

## Solución de problemas

### El plugin no aparece en el ribbon de Inventor

- Verifica que los archivos estén en la ruta correcta:
  `%APPDATA%\Autodesk\Inventor 2024\Addins\InventorMCPBridge\`
- Revisa que `InventorMCPBridge.addin` esté presente junto al `.dll`.
- Reinicia Inventor completamente.
- Si sigue sin aparecer, ve a **Tools → Add-Ins** y actívalo manualmente.

### El botón "MCP Bridge: OFF" no cambia a ON

El plugin no pudo conectar al servidor. Al hacer clic, aparece un MessageBox con el error. Causas comunes:

| Error | Causa | Solución |
|---|---|---|
| `No se pudo conectar` | URL incorrecta o servidor caído | Verifica la URL en `config.json` y que Azure esté corriendo (`/health`) |
| `401 Unauthorized` | API key inválida | Verifica que la API key en `config.json` coincida con `USERS_CONFIG` |
| `403 Forbidden` | `UserId` no corresponde a la API key | Verifica que `UserId` en `config.json` sea el valor correcto para esa key |
| Timeout | Servidor arrancando | Espera 30 s y vuelve a intentar |

### El plugin se conecta pero los comandos no llegan

- Verifica que el botón del ribbon esté en **ON** (no basta con que la conexión sea exitosa, hay que activarla explícitamente).
- Revisa los logs del servidor en Azure para ver si las solicitudes de polling llegan.

### Error al cargar el add-in en Inventor (MessageBox al abrir)

Un error en `Activate()` evita que el add-in cargue. El MessageBox muestra el tipo de excepción y el stack trace. Causas comunes:

| Error | Causa | Solución |
|---|---|---|
| `FileNotFoundException` | Falta `Newtonsoft.Json.dll` | Reinstala el plugin; asegúrate de que el `.dll` está en la carpeta |
| `BadImageFormatException` | DLL de 32 bits en sistema 64 bits | Asegúrate de usar el build `x64` |
| `config.json` mal formado | Error en el JSON | Edita el archivo o reinstala el plugin |

### Copilot Studio no descubre los tools MCP

- Verifica que la URL termine en `/sse` (no en `/` ni en `/api`).
- Verifica que la API key sea válida haciendo una prueba desde terminal:
  ```bash
  curl -H "x-api-key: tu_key" https://tu-app.azurewebsites.net/api/health
  ```
- Revisa que el servidor esté corriendo y no en estado `Stopped` en Azure.

### El servidor falla al arrancar en Azure (error 500)

Revisa los logs de arranque:

```bash
az webapp log tail --name tu-app --resource-group tu-rg
```

Causas comunes:
- `USERS_CONFIG` no está configurado → el servidor lanza `ValueError` y no arranca.
- El JSON de `USERS_CONFIG` está mal formado (comillas simples en lugar de dobles, comas sobrantes).
- El startup command está mal escrito.

### Un usuario recibe 403 al hacer polling

La API key en su `config.json` no corresponde al `user_id` en la URL de polling. Abre:
```
%APPDATA%\Autodesk\Inventor 2024\Addins\InventorMCPBridge\config.json
```
y verifica que `ApiKey` mapea exactamente a `UserId` según `USERS_CONFIG` del servidor.
