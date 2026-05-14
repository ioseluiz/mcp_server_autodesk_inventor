import asyncio
import uuid
import os
from typing import Any, Dict, List, Optional
from fastapi import FastAPI, Depends, HTTPException, Header
from pydantic import BaseModel
from fastmcp import FastMCP
import json

# =========================================================================
# CONFIGURACIÓN
# =========================================================================
API_KEY = os.environ.get("API_KEY", "1234567890") 

# =========================================================================
# INICIALIZACIÓN DE APLICACIONES
# =========================================================================
mcp = FastMCP("Autodesk Inventor 2024 Assistant")
mcp_app = mcp.http_app(path="/")

app = FastAPI(title="Inventor MCP Azure Hub", description="Puente Inverso entre Copilot Studio e Inventor local", lifespan=mcp_app.lifespan)

# =========================================================================
# SISTEMA DE COLAS (POLLING)
# =========================================================================
pending_tasks: Dict[str, Dict[str, Any]] = {}
completed_tasks: Dict[str, Any] = {}
task_events: Dict[str, asyncio.Event] = {}
user_queues: Dict[str, Dict[str, Dict[str, Any]]] = {}

def verify_api_key(x_api_key: str = Header(None)):
    if not x_api_key or x_api_key != API_KEY:
        raise HTTPException(status_code=401, detail="Invalid API Key")
    return x_api_key

class TaskResult(BaseModel):
    task_id: str
    result: Any
    error: Optional[str] = None

# --- Endpoints para el Plugin de Inventor (C#) ---

@app.get("/api/poll/{user_id}")
async def poll_tasks(user_id: str, x_api_key: str = Depends(verify_api_key)):
    if user_id not in user_queues or not user_queues[user_id]:
        return {"task_id": None}
    
    task_id = next(iter(user_queues[user_id]))
    task_data = user_queues[user_id].pop(task_id)
    
    return {
        "task_id": task_id,
        "command": task_data["command"],
        "payload": task_data["payload"]
    }

@app.post("/api/result/{task_id}")
async def submit_result(task_id: str, result: TaskResult, x_api_key: str = Depends(verify_api_key)):
    if task_id in task_events:
        completed_tasks[task_id] = result.model_dump() # Usando model_dump() para Pydantic v2
        task_events[task_id].set()
        return {"status": "ok"}
    return {"status": "error", "message": "Task ID not found or expired"}

async def execute_in_inventor(usuario: str, command: str, payload: Dict[str, Any], timeout_seconds: float = 60.0) -> Any:
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
        raise Exception(f"Timeout: Inventor no respondió en {timeout_seconds} segundos al comando '{command}'.")
    finally:
        task_events.pop(task_id, None)

# =========================================================================
# TOOLS DE MCP
# =========================================================================

@mcp.tool()
async def get_active_document_info(usuario: str) -> str:
    """Obtiene información básica del documento activo en Inventor."""
    try:
        data = await execute_in_inventor(usuario, "get_active_doc_info", {}, timeout_seconds=15.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"

@mcp.tool()
async def list_parameters(usuario: str) -> str:
    """Lista todos los parámetros del documento activo."""
    try:
        data = await execute_in_inventor(usuario, "list_parameters", {}, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"

@mcp.tool()
async def update_parameter(usuario: str, name: str, value: str) -> str:
    """Actualiza el valor de un parámetro en el documento activo."""
    try:
        data = await execute_in_inventor(usuario, "update_parameter", {"name": name, "value": value}, timeout_seconds=30.0)
        return f"Resultado: {data}"
    except Exception as e:
        return f"Error: {str(e)}"

@mcp.tool()
async def export_to_step(usuario: str) -> str:
    """Exporta el documento activo a un archivo STEP."""
    try:
        data = await execute_in_inventor(usuario, "export_step", {}, timeout_seconds=60.0)
        return f"Exportación completada: {data}"
    except Exception as e:
        return f"Error: {str(e)}"

@mcp.tool()
async def create_line(usuario: str, x1: float, y1: float, x2: float, y2: float) -> str:
    """Crea una línea en un boceto (sketch) en el plano XY del documento activo."""
    try:
        payload = {"x1": x1, "y1": y1, "x2": x2, "y2": y2}
        data = await execute_in_inventor(usuario, "create_line", payload, timeout_seconds=30.0)
        return f"Resultado: {data}"
    except Exception as e:
        return f"Error: {str(e)}"

@mcp.tool()
async def create_circle(usuario: str, center_x: float, center_y: float, radius: float) -> str:
    """Crea un círculo en un boceto (sketch) en el plano XY del documento activo."""
    try:
        payload = {"center_x": center_x, "center_y": center_y, "radius": radius}
        data = await execute_in_inventor(usuario, "create_circle", payload, timeout_seconds=30.0)
        return f"Resultado: {data}"
    except Exception as e:
        return f"Error: {str(e)}"

# =========================================================================
# MONTAR FastMCP en FastAPI
# =========================================================================
app.mount("/sse", mcp_app)

@app.get("/")
async def root():
    return {"status": "online", "message": "Inventor MCP Azure Hub is running"}

@app.get("/api/health")
async def health(x_api_key: str = Depends(verify_api_key)):
    return {"status": "ok", "message": "Servidor MCP activo"}

if __name__ == "__main__":
    import uvicorn
    port = int(os.environ.get("PORT", 8000))
    uvicorn.run("main:app", host="0.0.0.0", port=port)
