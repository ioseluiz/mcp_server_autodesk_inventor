import asyncio
import uuid
import os
import json
from contextlib import asynccontextmanager
from contextvars import ContextVar
from typing import Any, Dict, Optional
from fastapi import FastAPI, HTTPException, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse, Response
from pydantic import BaseModel
from fastmcp import FastMCP
from mcp.server.streamable_http_manager import StreamableHTTPSessionManager
from fastmcp.server.http import StreamableHTTPASGIApp
from dotenv import load_dotenv

load_dotenv()

# =========================================================================
# MAPA DE USUARIOS  api_key → user_id
# =========================================================================
USERS: Dict[str, str] = {}

_users_config = os.environ.get("USERS_CONFIG", "")
if _users_config:
    try:
        USERS = json.loads(_users_config)
    except json.JSONDecodeError as e:
        raise ValueError(f"USERS_CONFIG no es JSON válido: {e}")
else:
    _api_key = os.environ.get("API_KEY", "")
    _user_id = os.environ.get("USER_ID", "default")
    if not _api_key:
        raise ValueError(
            "Configura USERS_CONFIG (multi-usuario) o API_KEY + USER_ID (un usuario)."
        )
    USERS[_api_key] = _user_id

if not USERS:
    raise ValueError("USERS_CONFIG está vacío. Debe contener al menos un usuario.")

current_user_id: ContextVar[str] = ContextVar("current_user_id")

# =========================================================================
# MCP — Streamable HTTP transport (MCP spec 2025-03-26).
# Copilot Studio uses Streamable HTTP transport (MCP 2025-03-26):
#   POST /sse  — send MCP request, receive SSE or JSON response
#   GET  /sse  — open SSE stream for server-pushed events
#   DELETE /sse — close session
# All three methods are handled by StreamableHTTPASGIApp via a single
# @app.api_route so FastAPI creates a Route (Match.FULL, no redirects).
# =========================================================================
mcp = FastMCP("Autodesk Inventor 2024 Assistant")

_session_manager = StreamableHTTPSessionManager(
    app=mcp._mcp_server,
    event_store=None,
    json_response=False,
    stateless=False,
)
_streamable_app = StreamableHTTPASGIApp(_session_manager)


@asynccontextmanager
async def lifespan(app: FastAPI):
    async with mcp._lifespan_manager(), _session_manager.run():
        yield


# =========================================================================
# FASTAPI APP
# =========================================================================
app = FastAPI(
    title="Inventor MCP Azure Hub",
    description="Puente entre Copilot Studio e Inventor local vía MCP",
    lifespan=lifespan,
)

# =========================================================================
# MIDDLEWARE
# =========================================================================
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.middleware("http")
async def auth_middleware(request: Request, call_next):
    if request.url.path in ["/", "/health"] or request.method == "OPTIONS":
        return await call_next(request)

    # /messages/ is authenticated implicitly by the session_id query param.
    if request.url.path.startswith("/messages"):
        return await call_next(request)

    # Capture headers on /sse to diagnose Copilot Studio auth format
    if request.url.path == "/sse":
        _last_sse_request.clear()
        _last_sse_request.update({
            "method": request.method,
            "headers": dict(request.headers),
            "query_params": dict(request.query_params),
        })

    # Accept API key from multiple locations for Copilot Studio compatibility:
    # 1. x-api-key header (C# plugin)
    # 2. Authorization: Bearer <key>
    # 3. Authorization: <key>
    # 4. api-key query param
    def extract_api_key() -> Optional[str]:
        k = request.headers.get("x-api-key")
        if k:
            return k
        auth = request.headers.get("authorization", "")
        if auth.lower().startswith("bearer "):
            return auth[7:]
        if auth:
            return auth
        return request.query_params.get("api-key") or request.query_params.get("api_key")

    api_key = extract_api_key()
    user_id = USERS.get(api_key) if api_key else None
    if not user_id:
        return JSONResponse(
            status_code=401,
            content={"detail": "API Key inválida o no asociada a ningún usuario."},
        )

    token = current_user_id.set(user_id)
    try:
        return await call_next(request)
    finally:
        current_user_id.reset(token)


# =========================================================================
# COLA DE TAREAS (polling inverso plugin ↔ servidor)
# =========================================================================
pending_tasks: Dict[str, Dict[str, Any]] = {}
completed_tasks: Dict[str, Any] = {}
task_events: Dict[str, asyncio.Event] = {}
user_queues: Dict[str, Dict[str, Dict[str, Any]]] = {}

# Captures the last request seen on /sse for auth diagnostics
_last_sse_request: Dict[str, Any] = {}


class TaskResult(BaseModel):
    task_id: str
    result: Any
    error: Optional[str] = None


# =========================================================================
# ENDPOINTS PARA EL PLUGIN C#
# =========================================================================
@app.get("/api/poll/{user_id}")
async def poll_tasks(user_id: str):
    caller = current_user_id.get(None)
    if caller != user_id:
        raise HTTPException(
            status_code=403,
            detail=f"Tu API key no está autorizada para el usuario '{user_id}'.",
        )

    if user_id not in user_queues or not user_queues[user_id]:
        return {"task_id": None}

    task_id = next(iter(user_queues[user_id]))
    task_data = user_queues[user_id].pop(task_id)
    return {
        "task_id": task_id,
        "command": task_data["command"],
        "payload": task_data["payload"],
    }


@app.post("/api/result/{task_id}")
async def submit_result(task_id: str, result: TaskResult):
    if task_id in task_events:
        completed_tasks[task_id] = result.model_dump()
        task_events[task_id].set()
        return {"status": "ok"}
    return {"status": "error", "message": "Task ID no encontrado o expirado"}


async def execute_in_inventor(
    usuario: str, command: str, payload: Dict[str, Any], timeout_seconds: float = 60.0
) -> Any:
    task_id = str(uuid.uuid4())
    event = asyncio.Event()
    task_events[task_id] = event

    if usuario not in user_queues:
        user_queues[usuario] = {}

    user_queues[usuario][task_id] = {"command": command, "payload": payload}
    pending_tasks[task_id] = {"command": command, "payload": payload}

    try:
        await asyncio.wait_for(event.wait(), timeout=timeout_seconds)
        res = completed_tasks.pop(task_id, None)
        if res and res.get("error"):
            raise Exception(res["error"])
        return res.get("result") if res else None
    except asyncio.TimeoutError:
        pending_tasks.pop(task_id, None)
        if usuario in user_queues and task_id in user_queues[usuario]:
            user_queues[usuario].pop(task_id)
        raise Exception(
            f"Timeout: Inventor no respondió en {timeout_seconds}s al comando '{command}'."
        )
    finally:
        task_events.pop(task_id, None)


# =========================================================================
# TOOLS MCP
# =========================================================================
@mcp.tool()
async def get_active_document_info() -> str:
    """Obtiene información básica del documento activo en Inventor."""
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(usuario, "get_active_doc_info", {}, timeout_seconds=15.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def list_parameters() -> str:
    """Lista todos los parámetros del documento activo."""
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(usuario, "list_parameters", {}, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def update_parameter(name: str, value: str) -> str:
    """Actualiza el valor de un parámetro en el documento activo."""
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(
            usuario, "update_parameter", {"name": name, "value": value}, timeout_seconds=30.0
        )
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def export_to_step() -> str:
    """Exporta el documento activo a un archivo STEP."""
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(usuario, "export_step", {}, timeout_seconds=60.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def create_line(x1: float, y1: float, x2: float, y2: float) -> str:
    """Crea una línea en un boceto (sketch) en el plano XY del documento activo."""
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"x1": x1, "y1": y1, "x2": x2, "y2": y2}
        data = await execute_in_inventor(usuario, "create_line", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def create_circle(center_x: float, center_y: float, radius: float) -> str:
    """Crea un círculo en un boceto (sketch) en el plano XY del documento activo."""
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"center_x": center_x, "center_y": center_y, "radius": radius}
        data = await execute_in_inventor(usuario, "create_circle", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


# ── Grupo: Inicialización y gestión de entorno ────────────────────────────

@mcp.tool()
async def create_new_part(units: str = "metric") -> str:
    """Crea un nuevo documento de pieza (.ipt) en Inventor a partir de una plantilla.

    Args:
        units: Sistema de unidades de la plantilla: 'metric' (mm) o 'imperial' (pulgadas).
               Por defecto 'metric'.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(
            usuario, "create_new_part", {"units": units}, timeout_seconds=30.0
        )
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def create_new_assembly(units: str = "metric") -> str:
    """Crea un nuevo documento de ensamble (.iam) en Inventor a partir de una plantilla.

    Args:
        units: Sistema de unidades de la plantilla: 'metric' (mm) o 'imperial' (pulgadas).
               Por defecto 'metric'.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(
            usuario, "create_new_assembly", {"units": units}, timeout_seconds=30.0
        )
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def open_document(path: str) -> str:
    """Abre un documento de Inventor existente desde una ruta de archivo completa.

    Args:
        path: Ruta completa al archivo a abrir (.ipt, .iam, .idw, etc.).
              Ejemplo: 'C:/Users/User/Documents/pieza.ipt'
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(
            usuario, "open_document", {"path": path}, timeout_seconds=30.0
        )
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def save_document(path: str = "") -> str:
    """Guarda el documento activo en Inventor.

    Args:
        path: Ruta completa donde guardar el archivo. Si se omite, guarda en la ubicación
              actual del documento. Requerido si el documento es nuevo y nunca ha sido guardado.
              Ejemplo: 'C:/Users/User/Documents/pieza.ipt'
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"path": path} if path else {}
        data = await execute_in_inventor(usuario, "save_document", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def change_units(units: str) -> str:
    """Cambia las unidades de longitud del documento activo en Inventor.

    Args:
        units: Unidad de longitud a aplicar. Valores válidos: 'mm', 'cm', 'm', 'in', 'ft'.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(
            usuario, "change_units", {"units": units}, timeout_seconds=15.0
        )
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def set_material(material_name: str) -> str:
    """Asigna un material de la biblioteca de Inventor a la pieza activa.

    Args:
        material_name: Nombre exacto del material en la biblioteca de Inventor.
                       Ejemplos: 'Steel', 'Aluminum 6061', 'Copper', 'ABS Plastic', 'Acero'.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(
            usuario, "set_material", {"material_name": material_name}, timeout_seconds=30.0
        )
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


# ── Grupo: Bocetado 2D / Sketching ───────────────────────────────────────

@mcp.tool()
async def create_sketch(plane: str = "XY", name: str = "") -> str:
    """Crea un nuevo boceto 2D en el plano de origen especificado.

    Args:
        plane: Plano de origen donde crear el boceto: 'XY', 'XZ' o 'YZ'. Por defecto 'XY'.
        name:  Nombre opcional para el boceto. Si se omite, Inventor asigna uno automático.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"plane": plane}
        if name:
            payload["name"] = name
        data = await execute_in_inventor(usuario, "create_sketch", payload, timeout_seconds=20.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def draw_rectangle(
    x1: float = 0, y1: float = 0, x2: float = 0, y2: float = 0,
    mode: str = "twopoint",
    cx: float = 0, cy: float = 0, px: float = 0, py: float = 0,
) -> str:
    """Dibuja un rectángulo en el boceto activo.

    Modos disponibles:
      - 'twopoint' (default): dos esquinas opuestas. Parámetros: x1,y1 (esquina 1) y x2,y2 (esquina 2).
      - 'centered': centro y punto de esquina. Parámetros: cx,cy (centro) y px,py (esquina).

    Retorna los índices de entidad de las 4 líneas creadas.
    Los valores de coordenadas están en las unidades del documento (normalmente cm internamente en Inventor).
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        if mode == "centered":
            payload = {"mode": "centered", "cx": cx, "cy": cy, "px": px, "py": py}
        else:
            payload = {"mode": "twopoint", "x1": x1, "y1": y1, "x2": x2, "y2": y2}
        data = await execute_in_inventor(usuario, "draw_rectangle", payload, timeout_seconds=20.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def draw_arc(
    mode: str = "threepoints",
    x1: float = 0, y1: float = 0,
    x2: float = 0, y2: float = 0,
    x3: float = 0, y3: float = 0,
    cx: float = 0, cy: float = 0,
    clockwise: bool = False,
) -> str:
    """Dibuja un arco en el boceto activo.

    Modos disponibles:
      - 'threepoints' (default): arco que pasa por tres puntos.
        Parámetros: x1,y1 (inicio), x2,y2 (punto intermedio), x3,y3 (fin).
      - 'center': arco por centro y extremos.
        Parámetros: cx,cy (centro), x1,y1 (inicio), x2,y2 (fin), clockwise (sentido).

    Retorna el índice de entidad del arco.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        if mode == "center":
            payload = {"mode": "center", "cx": cx, "cy": cy,
                       "x1": x1, "y1": y1, "x2": x2, "y2": y2, "clockwise": clockwise}
        else:
            payload = {"mode": "threepoints",
                       "x1": x1, "y1": y1, "x2": x2, "y2": y2, "x3": x3, "y3": y3}
        data = await execute_in_inventor(usuario, "draw_arc", payload, timeout_seconds=20.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def draw_slot(
    cx1: float, cy1: float,
    cx2: float, cy2: float,
    width: float,
) -> str:
    """Dibuja una ranura (slot) recta en el boceto activo por dos centros y ancho.

    Args:
        cx1, cy1: Coordenadas del centro del primer semicírculo.
        cx2, cy2: Coordenadas del centro del segundo semicírculo.
        width:    Ancho total de la ranura (diámetro de los semicírculos).

    Retorna los índices de las entidades creadas.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"cx1": cx1, "cy1": cy1, "cx2": cx2, "cy2": cy2, "width": width}
        data = await execute_in_inventor(usuario, "draw_slot", payload, timeout_seconds=20.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def add_sketch_dimension(
    type: str,
    entity_index: int = -1,
    entity1: int = -1,
    entity2: int = -1,
    value: float = 0,
    units: str = "mm",
    orientation: str = "aligned",
    text_x: float = 1.0,
    text_y: float = 1.0,
    driven: bool = False,
) -> str:
    """Agrega una cota paramétrica a una entidad del boceto activo.

    Args:
        type:         Tipo de cota: 'line' (longitud de línea), 'radius', 'diameter',
                      'distance' (entre dos líneas).
        entity_index: Índice de la entidad (para line, radius, diameter).
        entity1:      Índice de la primera entidad (para distance).
        entity2:      Índice de la segunda entidad (para distance).
        value:        Valor numérico de la cota (0 = mantener tamaño actual).
        units:        Unidad del valor: 'mm', 'cm', 'm', 'in'. Por defecto 'mm'.
        orientation:  Para 'distance': 'aligned', 'horizontal' o 'vertical'.
        text_x, text_y: Posición del texto de la cota en el boceto.
        driven:       True = cota de referencia (no controla geometría).
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload: dict = {"type": type, "units": units, "orientation": orientation,
                         "text_x": text_x, "text_y": text_y, "driven": driven}
        if value != 0:
            payload["value"] = value
        if entity_index >= 0:
            payload["entity_index"] = entity_index
        if entity1 >= 0:
            payload["entity1"] = entity1
        if entity2 >= 0:
            payload["entity2"] = entity2
        data = await execute_in_inventor(usuario, "add_sketch_dimension", payload, timeout_seconds=20.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def add_sketch_constraint(
    type: str,
    entity_index: int = -1,
    entity1: int = -1,
    entity2: int = -1,
) -> str:
    """Aplica una restricción geométrica a entidades del boceto activo.

    Args:
        type: Tipo de restricción:
              - 'horizontal' o 'vertical': requiere entity_index (línea).
              - 'tangent', 'coincident', 'parallel', 'perpendicular',
                'equal_length', 'concentric': requieren entity1 y entity2.
        entity_index: Índice de la entidad (para horizontal/vertical).
        entity1:      Índice de la primera entidad.
        entity2:      Índice de la segunda entidad.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload: dict = {"type": type}
        if entity_index >= 0:
            payload["entity_index"] = entity_index
        if entity1 >= 0:
            payload["entity1"] = entity1
        if entity2 >= 0:
            payload["entity2"] = entity2
        data = await execute_in_inventor(usuario, "add_sketch_constraint", payload, timeout_seconds=20.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def project_geometry(source: str = "origin") -> str:
    """Proyecta geometría existente al boceto activo como referencias.

    Args:
        source: Fuente de la geometría a proyectar:
                - 'origin' (default): proyecta el punto de origen y los ejes X e Y.
                - 'model': proyecta todas las aristas del primer cuerpo sólido.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(
            usuario, "project_geometry", {"source": source}, timeout_seconds=30.0
        )
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def close_sketch() -> str:
    """Cierra el boceto activo y lo deja listo para operaciones 3D (extrusión, revolución, etc.)."""
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(usuario, "close_sketch", {}, timeout_seconds=15.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


# ── Grupo: Sólidos base ──────────────────────────────────────────────────

@mcp.tool()
async def extrude_profile(
    distance: float,
    operation: str = "join",
    direction: str = "positive",
    units: str = "mm",
) -> str:
    """Extruye el perfil cerrado del boceto activo para crear o modificar un sólido.

    Args:
        distance:  Distancia de extrusión en las unidades indicadas.
        operation: 'join' (unión, default), 'cut' (corte) o 'intersect' (intersección).
        direction: 'positive' (default), 'negative' o 'symmetric'.
        units:     Unidad de la distancia: 'mm' (default), 'cm', 'm', 'in'.

    Nota: debe haber un boceto activo con un perfil cerrado (usa create_sketch + draw_*).
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"distance": distance, "operation": operation,
                   "direction": direction, "units": units}
        data = await execute_in_inventor(usuario, "extrude_profile", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def revolve_profile(
    axis: str = "X",
    angle: float = 360.0,
    operation: str = "join",
    direction: str = "positive",
) -> str:
    """Crea un sólido de revolución girando el perfil del boceto activo alrededor de un eje.

    Args:
        axis:      Eje de revolución: 'X', 'Y' o 'Z' (ejes de origen del documento).
        angle:     Ángulo de revolución en grados (360 = revolución completa, default).
        operation: 'join' (default), 'cut' o 'intersect'.
        direction: 'positive' (default) o 'negative' (para ángulos parciales).

    Nota: el perfil del boceto activo debe estar a un lado del eje seleccionado.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"axis": axis, "angle": angle, "operation": operation, "direction": direction}
        data = await execute_in_inventor(usuario, "revolve_profile", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def sweep_profile(
    path_sketch: str,
    operation: str = "join",
) -> str:
    """Crea un sólido de barrido moviendo el perfil del boceto activo a lo largo de una trayectoria.

    Args:
        path_sketch: Nombre del boceto que contiene la trayectoria (debe ser un boceto
                     diferente al del perfil, con una curva abierta o cerrada continua).
        operation:   'join' (default), 'cut' o 'intersect'.

    Flujo típico:
      1. create_sketch('XY') + draw línea/arco → boceto de trayectoria.
      2. close_sketch().
      3. create_sketch('YZ') + draw_rectangle o círculo → perfil.
      4. sweep_profile(path_sketch='nombre del boceto de trayectoria').
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"path_sketch": path_sketch, "operation": operation}
        data = await execute_in_inventor(usuario, "sweep_profile", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def loft_profiles(
    sketches: str,
    operation: str = "join",
) -> str:
    """Crea un sólido de transición (loft) entre dos o más perfiles en bocetos distintos.

    Args:
        sketches:  Nombres de los bocetos separados por coma, en orden de transición.
                   Ejemplo: 'Sketch1,Sketch2,Sketch3'. Mínimo 2 bocetos.
        operation: 'join' (default), 'cut' o 'intersect'.

    Flujo típico:
      1. create_sketch('XY', name='Perfil1') + draw forma + close_sketch.
      2. create_sketch('XZ', name='Perfil2') + draw forma + close_sketch.
         (cada boceto debe estar en un plano diferente para que el loft tenga profundidad).
      3. loft_profiles(sketches='Perfil1,Perfil2').
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"sketches": sketches, "operation": operation}
        data = await execute_in_inventor(usuario, "loft_profiles", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def create_hole(
    diameter: float,
    depth: float = 0,
    through: bool = False,
    hole_type: str = "drilled",
    cbore_diameter: float = 0,
    cbore_depth: float = 0,
    csink_diameter: float = 0,
    csink_angle: float = 90,
    units: str = "mm",
) -> str:
    """Coloca agujeros en el sólido usando las posiciones del boceto activo.

    El boceto activo debe contener círculos (sus centros serán los centros de los agujeros)
    o puntos de boceto. El boceto debe estar sobre una cara plana del sólido.

    Args:
        diameter:      Diámetro del agujero.
        depth:         Profundidad del agujero (ignorado si through=True).
        through:       True = agujero pasante, False = profundidad fija.
        hole_type:     'drilled' (simple, default), 'cbore' (avellanado plano),
                       'csink' (avellanado cónico).
        cbore_diameter: Diámetro del avellanado plano (solo para hole_type='cbore').
        cbore_depth:    Profundidad del avellanado plano.
        csink_diameter: Diámetro del avellanado cónico (solo para hole_type='csink').
        csink_angle:    Ángulo del cono del avellanado cónico en grados (default 90°).
        units:          Unidad de medida: 'mm' (default), 'cm', 'in'.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload: dict = {"diameter": diameter, "through": through,
                         "hole_type": hole_type, "units": units}
        if not through and depth > 0:
            payload["depth"] = depth
        if hole_type == "cbore" and cbore_diameter > 0:
            payload["cbore_diameter"] = cbore_diameter
            payload["cbore_depth"] = cbore_depth
        if hole_type == "csink" and csink_diameter > 0:
            payload["csink_diameter"] = csink_diameter
            payload["csink_angle"] = csink_angle
        data = await execute_in_inventor(usuario, "create_hole", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


# ── Grupo: Modificación de sólidos ──────────────────────────────────────

@mcp.tool()
async def add_fillet(
    radius: float,
    edge_indices: str,
    units: str = "mm",
) -> str:
    """Aplica un redondeo (fillet) de radio constante a una o más aristas del sólido activo.

    Args:
        radius:       Radio del redondeo.
        edge_indices: Índices de las aristas a redondear, separados por coma (ej. '1,3,5').
                      Los índices son base 1, referenciados al primer cuerpo sólido de la pieza.
        units:        Unidad del radio: 'mm' (default), 'cm', 'in'.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"radius": radius, "edge_indices": edge_indices, "units": units}
        data = await execute_in_inventor(usuario, "add_fillet", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def add_chamfer(
    distance: float,
    edge_indices: str,
    angle: float = 0.0,
    units: str = "mm",
) -> str:
    """Aplica un chaflán (chamfer) a una o más aristas del sólido activo.

    Args:
        distance:     Distancia del chaflán.
        edge_indices: Índices de las aristas a chaflanar, separados por coma (ej. '1,3').
                      Los índices son base 1, referenciados al primer cuerpo sólido de la pieza.
        angle:        Ángulo del chaflán en grados. 0 (default) usa modo distancia simétrica;
                      si se especifica, usa modo distancia + ángulo.
        units:        Unidad de la distancia: 'mm' (default), 'cm', 'in'.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload: dict = {"distance": distance, "edge_indices": edge_indices, "units": units}
        if angle > 0:
            payload["angle"] = angle
        data = await execute_in_inventor(usuario, "add_chamfer", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def shell_solid(
    thickness: float,
    face_indices: str = "",
    direction: str = "inside",
    units: str = "mm",
) -> str:
    """Vacía el sólido activo dejando una pared delgada de espesor constante (Shell).

    Args:
        thickness:    Espesor de la pared resultante.
        face_indices: Índices de las caras a eliminar (abrir), separados por coma (ej. '1,2').
                      Los índices son base 1, referenciados al primer cuerpo sólido.
                      Si se omite, todas las caras se conservan (cuerpo cerrado hueco).
        direction:    'inside' (default, espesor hacia el interior) o 'outside' (hacia afuera).
        units:        Unidad del espesor: 'mm' (default), 'cm', 'in'.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload: dict = {"thickness": thickness, "direction": direction, "units": units}
        if face_indices:
            payload["face_indices"] = face_indices
        data = await execute_in_inventor(usuario, "shell_solid", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def thread_feature(
    face_index: int,
    designation: str = "",
    thread_type: str = "ANSI Metric M Profile",
    full_length: bool = True,
    right_handed: bool = True,
    cosmetic: bool = True,
    length: float = 0.0,
    units: str = "mm",
) -> str:
    """Aplica una rosca cosmética o física a una cara cilíndrica del sólido activo.

    Args:
        face_index:   Índice de la cara cilíndrica donde aplicar la rosca (base 1).
        designation:  Designación de la rosca según el estándar. Ej: 'M8x1.25', 'M10x1.5'.
                      Por defecto 'M6x1'.
        thread_type:  Tipo de estándar de rosca. Ej: 'ANSI Metric M Profile',
                      'ANSI Unified Screw Threads'. Por defecto 'ANSI Metric M Profile'.
        full_length:  True (default) = rosca en toda la longitud de la cara.
                      False = longitud controlada por el parámetro 'length'.
        right_handed: True (default) = rosca a derechas. False = rosca a izquierdas.
        cosmetic:     True (default) = rosca cosmética (solo visual).
                      False = rosca física (modifica la geometría real del sólido).
        length:       Longitud de la rosca en 'units' (solo si full_length=False).
        units:        Unidad de longitud: 'mm' (default), 'cm', 'in'.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload: dict = {
            "face_index": face_index,
            "thread_type": thread_type,
            "full_length": full_length,
            "right_handed": right_handed,
            "cosmetic": cosmetic,
            "units": units,
        }
        if designation:
            payload["designation"] = designation
        if not full_length and length > 0:
            payload["length"] = length
        data = await execute_in_inventor(usuario, "thread_feature", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def split_body(
    plane: str = "XY",
    keep_both: bool = True,
) -> str:
    """Divide el cuerpo sólido activo usando un plano de trabajo.

    Args:
        plane:      Plano de división: 'XY', 'XZ' o 'YZ' (planos de origen), o el nombre
                    exacto de cualquier plano de trabajo del documento.
        keep_both:  True (default) = mantiene ambas partes como cuerpos sólidos separados.
                    False = elimina la mitad del lado negativo del plano (recorte).
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"plane": plane, "keep_both": keep_both}
        data = await execute_in_inventor(usuario, "split_body", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def combine_bodies(
    operation: str = "join",
    base_body: int = 1,
    tool_bodies: str = "2",
) -> str:
    """Realiza una operación booleana entre cuerpos sólidos independientes de la pieza activa.

    Args:
        operation:   Operación booleana: 'join' (unión, default), 'cut' (resta),
                     'intersect' (intersección).
        base_body:   Índice del cuerpo sólido base (base 1). Por defecto 1.
        tool_bodies: Índices de los cuerpos herramienta separados por coma (ej. '2,3').
                     Por defecto '2'. Los cuerpos herramienta se consumen en la operación.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"operation": operation, "base_body": base_body, "tool_bodies": tool_bodies}
        data = await execute_in_inventor(usuario, "combine_bodies", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


# =========================================================================
# ENDPOINTS DE INFRAESTRUCTURA + SSE
# All explicit routes must be registered BEFORE app.mount() calls so that
# Starlette's ordered route resolution finds them first.
# =========================================================================
@app.get("/")
async def root():
    return {"status": "online", "service": "Inventor MCP Azure Hub"}


@app.get("/health")
async def health_public():
    """Sonda de salud pública para Azure App Service (sin autenticación)."""
    return {"status": "ok"}


@app.get("/api/health")
async def health():
    """Health check para el plugin de Inventor (requiere API key)."""
    caller = current_user_id.get(None)
    return {"status": "ok", "user_id": caller, "message": "Servidor MCP activo"}


@app.get("/api/debug")
async def debug_last_sse():
    """Devuelve los headers de la última request recibida en /sse."""
    return _last_sse_request or {"info": "Ninguna request a /sse capturada aún."}


@app.api_route("/sse", methods=["GET", "POST", "DELETE"])
async def mcp_endpoint(request: Request):
    """MCP Streamable HTTP endpoint — Copilot Studio uses POST to call tools."""
    await _streamable_app(request.scope, request.receive, request._send)
    return Response()


if __name__ == "__main__":
    import uvicorn

    port = int(os.environ.get("PORT", 8000))
    uvicorn.run("main:app", host="0.0.0.0", port=port)
