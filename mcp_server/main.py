import asyncio
import uuid
import os
import json
from contextvars import ContextVar
from typing import Any, Dict, Optional
from fastapi import FastAPI, HTTPException, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from pydantic import BaseModel
from fastmcp import FastMCP
from dotenv import load_dotenv

load_dotenv()

# =========================================================================
# MAPA DE USUARIOS  api_key → user_id
# =========================================================================
# Modo multi-usuario: USERS_CONFIG='{"clave_a":"userA","clave_b":"userB"}'
# Modo un usuario:    API_KEY=mi_clave  +  USER_ID=mi_usuario (retrocompat)
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

# Transporta el user_id autenticado desde el middleware hasta los tools MCP.
# ContextVar es seguro para asyncio: cada tarea hereda su propio contexto.
current_user_id: ContextVar[str] = ContextVar("current_user_id")

# =========================================================================
# MCP
# =========================================================================
mcp = FastMCP("Autodesk Inventor 2024 Assistant")
mcp_app = mcp.http_app(path="/")

app = FastAPI(
    title="Inventor MCP Azure Hub",
    description="Puente entre Copilot Studio e Inventor local vía MCP",
    lifespan=mcp_app.lifespan,
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
    # Sonda de salud de Azure y raíz son públicos.
    # OPTIONS pasa para que el middleware de CORS gestione preflight.
    if request.url.path in ["/", "/health"] or request.method == "OPTIONS":
        return await call_next(request)

    api_key = request.headers.get("x-api-key")
    user_id = USERS.get(api_key) if api_key else None
    if not user_id:
        return JSONResponse(
            status_code=401,
            content={"detail": "API Key inválida o no asociada a ningún usuario."},
        )

    # Inyecta el user_id en el contexto de esta request para que los tools
    # MCP puedan leerlo sin necesidad de parámetro explícito.
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


class TaskResult(BaseModel):
    task_id: str
    result: Any
    error: Optional[str] = None


# =========================================================================
# ENDPOINTS PARA EL PLUGIN C#
# =========================================================================
@app.get("/api/poll/{user_id}")
async def poll_tasks(user_id: str):
    # El plugin sólo puede leer su propia cola: la API key debe corresponder
    # exactamente al user_id que solicita.
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
# TOOLS MCP — sin parámetro 'usuario': se obtiene del contexto autenticado
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
        return f"Resultado: {data}"
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
        return f"Exportación completada: {data}"
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
        return f"Resultado: {data}"
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
        return f"Resultado: {data}"
    except Exception as e:
        return f"Error: {str(e)}"


# =========================================================================
# MONTAJE Y ENDPOINTS DE INFRAESTRUCTURA
# =========================================================================
app.mount("/sse", mcp_app)


@app.get("/")
async def root():
    return {"status": "online", "service": "Inventor MCP Azure Hub"}


@app.get("/health")
async def health_public():
    """Sonda de salud pública para Azure App Service (sin autenticación)."""
    return {"status": "ok"}


@app.get("/api/health")
async def health():
    """Health check para el plugin de Inventor (requiere API key vía middleware)."""
    caller = current_user_id.get(None)
    return {"status": "ok", "user_id": caller, "message": "Servidor MCP activo"}


if __name__ == "__main__":
    import uvicorn

    port = int(os.environ.get("PORT", 8000))
    uvicorn.run("main:app", host="0.0.0.0", port=port)
