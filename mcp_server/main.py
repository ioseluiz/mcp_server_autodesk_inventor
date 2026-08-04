"""Servidor MCP local para Autodesk Inventor.

    Claude Desktop --stdio--> este proceso --named pipe--> add-in .NET 8 --COM--> Inventor

El add-in es el servidor del pipe y este proceso su cliente: Claude Desktop relanza
este proceso en cada arranque o reconexión y puede dejar más de uno vivo, mientras
que Inventor permanece abierto. El extremo estable tiene que ser Inventor.

IMPORTANTE: con transporte stdio, stdout está reservado al protocolo JSON-RPC.
No usar print() en ningún sitio; los diagnósticos van a stderr.
"""

import io
import json
import logging
import os
import sys
import time
from functools import partial
from typing import Any, Dict, Optional

import anyio

# Debe ir antes de importar fastmcp: sus settings se leen del entorno al importar.
# Sin esto, cada arranque hace una petición a pypi.org para buscar actualizaciones.
os.environ.setdefault("FASTMCP_CHECK_FOR_UPDATES", "off")

from fastmcp import FastMCP  # noqa: E402

# =========================================================================
# CONFIGURACIÓN
# No se lee ningún archivo: Claude Desktop lanza este proceso con un directorio de
# trabajo arbitrario y un entorno mínimo, así que la configuración llega por
# variables de entorno (bloque "env" de claude_desktop_config.json).
# =========================================================================
PIPE_NAME = os.environ.get("INVENTOR_PIPE_NAME", "InventorMCPBridge")
PIPE_PATH = rf"\\.\pipe\{PIPE_NAME}"

logging.basicConfig(
    stream=sys.stderr,
    level=os.environ.get("INVENTOR_MCP_LOG_LEVEL", "INFO").upper(),
    format="%(asctime)s %(levelname)s %(message)s",
)
log = logging.getLogger("inventor-mcp")

BRIDGE_OFF_MESSAGE = (
    f"El MCP Bridge no está activo en Inventor (pipe '{PIPE_NAME}'). "
    "Abre Inventor y pulsa el botón 'MCP Bridge: OFF' en la pestaña MCP de la cinta."
)

# =========================================================================
# CLIENTE DEL NAMED PIPE
# Protocolo: JSON delimitado por líneas, una petición y una respuesta por línea.
#   ->  {"id": "1", "command": "get_active_doc_info", "payload": {}}
#   <-  {"id": "1", "result": {...}, "error": null}
# =========================================================================
ERROR_PIPE_BUSY = 231       # todas las instancias del pipe están ocupadas
_CONNECT_ATTEMPTS = 3


class _PipeConnection:
    """Un extremo del pipe. Se descarta completo en cuanto algo falla."""

    def __init__(self, path: str) -> None:
        self._raw = open(path, "r+b", buffering=0)
        # Las respuestas pueden pesar varios MB (capturas de pantalla en base64), así
        # que la lectura va con búfer: readline() sobre el FileIO crudo iría byte a byte.
        self._reader = io.BufferedReader(self._raw, buffer_size=65536)

    def roundtrip(self, request: bytes) -> bytes:
        self._raw.write(request)
        response = self._reader.readline()
        if not response:
            raise ConnectionError("El bridge cerró la conexión.")
        return response

    def close(self) -> None:
        for closeable in (self._reader, self._raw):
            try:
                closeable.close()
            except OSError:
                pass


class InventorBridge:
    """Conexión única y reutilizada al add-in, serializada con un lock.

    No hace falta un pool: el add-in devuelve cada comando a la hebra principal de
    Inventor, así que los comandos se ejecutan de uno en uno de todas formas.
    """

    def __init__(self, path: str) -> None:
        self._path = path
        self._conn: Optional[_PipeConnection] = None
        self._lock: Optional[anyio.Lock] = None
        self._next_id = 0

    def _get_lock(self) -> anyio.Lock:
        # Se crea al vuelo, ya dentro del bucle de eventos.
        if self._lock is None:
            self._lock = anyio.Lock()
        return self._lock

    def _connect(self) -> _PipeConnection:
        last_error: Optional[OSError] = None
        for attempt in range(_CONNECT_ATTEMPTS):
            try:
                return _PipeConnection(self._path)
            except FileNotFoundError:
                # El pipe no existe: el bridge está apagado en Inventor.
                raise RuntimeError(BRIDGE_OFF_MESSAGE) from None
            except OSError as exc:
                if getattr(exc, "winerror", None) != ERROR_PIPE_BUSY:
                    raise
                last_error = exc
                time.sleep(0.2 * (attempt + 1))
        raise RuntimeError(f"El bridge de Inventor está ocupado: {last_error}")

    def _drop(self) -> None:
        conn, self._conn = self._conn, None
        if conn is not None:
            conn.close()

    def _exchange(self, request: bytes) -> bytes:
        """Ida y vuelta bloqueante. Corre en un hilo, nunca en el bucle de eventos."""
        for attempt in (0, 1):
            conn = self._conn
            if conn is None:
                conn = self._conn = self._connect()
            try:
                return conn.roundtrip(request)
            except OSError:
                # Sesión muerta: el bridge se apagó, o el handle quedó obsoleto.
                # Se reconecta una sola vez.
                self._drop()
                if attempt == 1:
                    raise
        raise AssertionError("inalcanzable")

    async def call(
        self, command: str, payload: Optional[Dict[str, Any]], timeout: float
    ) -> Any:
        self._next_id += 1
        request = json.dumps(
            {"id": str(self._next_id), "command": command, "payload": payload or {}},
            ensure_ascii=False,
        ).encode("utf-8") + b"\n"

        log.debug("-> %s(%s)", command, ", ".join(payload or ()))

        async with self._get_lock():
            try:
                with anyio.fail_after(timeout):
                    raw = await anyio.to_thread.run_sync(
                        partial(self._exchange, request), abandon_on_cancel=True
                    )
            except TimeoutError:
                # El hilo abandonado sigue bloqueado en readline: al cerrar el handle
                # su lectura falla y termina, y la siguiente petición reconecta.
                self._drop()
                raise RuntimeError(
                    f"Timeout: Inventor no respondió en {timeout:g}s al comando '{command}'."
                ) from None

        try:
            message = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise RuntimeError(f"Respuesta ilegible del bridge: {exc}") from None

        error = message.get("error")
        if error:
            raise RuntimeError(error)
        return message.get("result")


bridge = InventorBridge(PIPE_PATH)

# Las 52 tools hacen `usuario = current_user_id.get(None)` y lo pasan como primer
# argumento a execute_in_inventor. Un ContextVar no sirve aquí: `get(None)` devuelve el
# None que se le pasa e ignora el default del propio ContextVar, así que las tools
# responderían "Error: sesión no autenticada". Este sustituto resuelve siempre al único
# usuario local y evita reescribir los 52 cuerpos.
class _LocalUser:
    @staticmethod
    def get(default: Optional[str] = None) -> str:
        return "local"


current_user_id = _LocalUser()

mcp = FastMCP("Autodesk Inventor 2026 Assistant")


async def execute_in_inventor(
    usuario: str, command: str, payload: Dict[str, Any], timeout_seconds: float = 60.0
) -> Any:
    """Ejecuta un comando en Inventor a través del add-in.

    `usuario` se ignora: venía del modo multiusuario del servidor en la nube y se
    mantiene en la firma para no tocar las 52 tools.
    """
    return await bridge.call(command, payload, timeout_seconds)

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
async def export_to_step(path: str = "") -> str:
    """Exporta el documento activo a un archivo STEP para fabricación o interoperabilidad.

    Args:
        path: Ruta completa donde guardar el archivo .step (incluir extensión).
              Si se omite, se guarda junto al documento activo con el mismo nombre.
              Ejemplo: 'C:/Users/User/Documents/pieza.step'
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"path": path} if path else {}
        data = await execute_in_inventor(usuario, "export_step", payload, timeout_seconds=60.0)
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
        plane: Plano donde crear el boceto: 'XY', 'XZ', 'YZ' (planos de origen),
               o el nombre/índice de un plano de trabajo creado con create_work_plane.
               Por defecto 'XY'.
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


# ── Grupo: Parámetros e iProperties ─────────────────────────────────────

@mcp.tool()
async def get_parameters() -> str:
    """Lista todos los parámetros del modelo activo, categorizados por tipo.

    Devuelve tres grupos separados:
      - ModelParameters: parámetros dimensionales creados por operaciones 3D (extrusión, etc.).
      - UserParameters: parámetros creados por el usuario para controlar el diseño.
      - ReferenceParameters: parámetros enlazados a otros documentos.

    Para cada parámetro devuelve: Name, Value, Expression, Units, ValueType
    (numeric / text / boolean) e InUse (solo parámetros de modelo).

    Nota: la tool 'list_parameters' también lista parámetros pero con menos contexto.
    Esta versión devuelve la estructura completa necesaria para razonamiento paramétrico.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(usuario, "get_parameters", {}, timeout_seconds=20.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def set_parameter_value(
    name: str,
    value: str = "",
    units: str = "",
    expression: str = "",
    value_type: str = "numeric",
) -> str:
    """Cambia el valor de un parámetro existente y fuerza la actualización del modelo.

    Puedes indicar el nuevo valor de dos formas:
      1. 'expression': expresión completa tal como aparece en Inventor (ej: '25 mm', 'largo / 2').
      2. 'value' + 'units': el agente construye la expresión automáticamente.

    Para parámetros de texto usa value_type='text' y value='el texto'.
    Para parámetros booleanos usa value_type='boolean' y value='true' o 'false'.

    Args:
        name:       Nombre exacto del parámetro (sensible a mayúsculas).
        value:      Valor numérico o texto (usa junto a 'units' o 'value_type').
        units:      Unidad del valor numérico: 'mm', 'cm', 'm', 'in', 'deg', etc.
        expression: Expresión completa (tiene prioridad sobre value+units si se provee).
        value_type: 'numeric' (default), 'text' o 'boolean'.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload: dict = {"name": name, "value_type": value_type}
        if expression:
            payload["expression"] = expression
        elif value:
            payload["value"] = value
            if units:
                payload["units"] = units
        data = await execute_in_inventor(usuario, "set_parameter_value", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def add_custom_parameter(
    name: str,
    value_type: str = "numeric",
    value: str = "0",
    units: str = "mm",
) -> str:
    """Crea un nuevo parámetro de usuario (UserParameter) en el documento activo.

    Los parámetros de usuario permiten definir variables de diseño globales que
    otras dimensiones y operaciones pueden referenciar por nombre.

    Args:
        name:       Nombre único del parámetro (sin espacios, sin caracteres especiales).
        value_type: Tipo del parámetro:
                    - 'numeric' (default): valor numérico con unidades.
                    - 'text': cadena de texto.
                    - 'boolean': valor lógico (True/False).
        value:      Valor inicial del parámetro:
                    - numeric: número (ej. '25', '3.14').
                    - text: cadena (ej. 'Acero inoxidable').
                    - boolean: 'true' o 'false'.
        units:      Unidades para parámetros numéricos: 'mm' (default), 'cm', 'm', 'in', 'deg'.
                    Ignorado para tipos text y boolean.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"name": name, "value_type": value_type, "value": value, "units": units}
        data = await execute_in_inventor(usuario, "add_custom_parameter", payload, timeout_seconds=20.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def update_iproperties(
    title: str = "",
    author: str = "",
    part_number: str = "",
    description: str = "",
    subject: str = "",
    keywords: str = "",
) -> str:
    """Modifica los metadatos (iProperties) del documento activo en Inventor.

    Solo actualiza los campos que se proporcionen (los campos vacíos se ignoran).

    Campos disponibles:
      - title:       Título del documento (iProperty 'Title').
      - author:      Autor o diseñador (iProperty 'Author').
      - part_number: Código de inventario o número de pieza (iProperty 'Part Number').
      - description: Descripción técnica de la pieza (iProperty 'Description').
      - subject:     Asunto o categoría del documento (iProperty 'Subject').
      - keywords:    Palabras clave separadas por comas (iProperty 'Keywords').

    Devuelve la lista de campos actualizados correctamente y los errores si los hay.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload: dict = {}
        if title:       payload["title"]       = title
        if author:      payload["author"]       = author
        if part_number: payload["part_number"]  = part_number
        if description: payload["description"]  = description
        if subject:     payload["subject"]      = subject
        if keywords:    payload["keywords"]     = keywords
        if not payload:
            return json.dumps({"Status": "error", "Error": "No se proporcionó ningún campo para actualizar."})
        data = await execute_in_inventor(usuario, "update_iproperties", payload, timeout_seconds=20.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


# ── Grupo: Geometría de trabajo ─────────────────────────────────────────

@mcp.tool()
async def create_work_plane(
    mode: str = "offset",
    plane: str = "XY",
    offset: float = 10.0,
    axis: str = "X",
    angle: float = 45.0,
    point1: int = 1,
    point2: int = 2,
    point3: int = 3,
    units: str = "mm",
) -> str:
    """Crea un plano de trabajo paramétrico en la pieza activa.

    Modos disponibles:
      - 'offset' (default): paralelo a un plano de origen, desplazado una distancia.
        Parámetros: plane ('XY'/'XZ'/'YZ' o nombre/índice), offset (distancia), units.
      - 'angle': inclinado respecto a un plano, rotado sobre un eje de trabajo.
        Parámetros: plane, axis ('X'/'Y'/'Z' o nombre/índice), angle (grados).
      - 'three_points': pasa por tres puntos de trabajo existentes.
        Parámetros: point1, point2, point3 (índices 1-based de WorkPoints).

    El campo 'WorkPlaneName' del resultado puede pasarse como parámetro 'plane'
    en create_sketch para crear bocetos sobre este plano de trabajo.

    Args:
        mode:   Tipo de construcción del plano: 'offset', 'angle' o 'three_points'.
        plane:  Plano de referencia: 'XY', 'XZ', 'YZ', nombre del plano o índice.
        offset: Distancia de desfase (solo modo 'offset').
        axis:   Eje de rotación (solo modo 'angle'): 'X', 'Y', 'Z', nombre o índice.
        angle:  Ángulo de inclinación en grados (solo modo 'angle').
        point1: Índice del 1er punto de trabajo (solo modo 'three_points').
        point2: Índice del 2do punto de trabajo (solo modo 'three_points').
        point3: Índice del 3er punto de trabajo (solo modo 'three_points').
        units:  Unidad del desfase: 'mm' (default), 'cm', 'm', 'in'.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload: dict = {"mode": mode, "units": units}
        if mode == "offset":
            payload["plane"] = plane
            payload["offset"] = offset
        elif mode == "angle":
            payload["plane"] = plane
            payload["axis"] = axis
            payload["angle"] = angle
        elif mode == "three_points":
            payload["point1"] = point1
            payload["point2"] = point2
            payload["point3"] = point3
        data = await execute_in_inventor(usuario, "create_work_plane", payload, timeout_seconds=20.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def create_work_axis(
    mode: str = "cylinder",
    face_index: int = 1,
    plane1: str = "XY",
    plane2: str = "YZ",
    point1: int = 1,
    point2: int = 2,
) -> str:
    """Crea un eje de trabajo paramétrico en la pieza activa.

    Modos disponibles:
      - 'cylinder' (default): extrae el eje central de una cara cilíndrica o cónica.
        Parámetros: face_index (índice 1-based de la cara cilíndrica).
      - 'two_planes': eje en la intersección de dos planos de trabajo.
        Parámetros: plane1, plane2 ('XY'/'XZ'/'YZ', nombre o índice).
      - 'two_points': eje que pasa por dos puntos de trabajo existentes.
        Parámetros: point1, point2 (índices 1-based de WorkPoints).

    El campo 'WorkAxisName' del resultado puede usarse como parámetro 'axis'
    en create_work_plane (modo 'angle') para construir planos inclinados.

    Args:
        mode:       Tipo de construcción: 'cylinder', 'two_planes' o 'two_points'.
        face_index: Índice de la cara cilíndrica/cónica (solo modo 'cylinder').
        plane1:     Primer plano de referencia (solo modo 'two_planes').
        plane2:     Segundo plano de referencia (solo modo 'two_planes').
        point1:     Índice del 1er punto de trabajo (solo modo 'two_points').
        point2:     Índice del 2do punto de trabajo (solo modo 'two_points').
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload: dict = {"mode": mode}
        if mode == "cylinder":
            payload["face_index"] = face_index
        elif mode == "two_planes":
            payload["plane1"] = plane1
            payload["plane2"] = plane2
        elif mode == "two_points":
            payload["point1"] = point1
            payload["point2"] = point2
        data = await execute_in_inventor(usuario, "create_work_axis", payload, timeout_seconds=20.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def create_work_point(
    mode: str = "fixed",
    x: float = 0.0,
    y: float = 0.0,
    z: float = 0.0,
    edge_index: int = 1,
    plane1: str = "XY",
    plane2: str = "XZ",
    plane3: str = "YZ",
    units: str = "mm",
) -> str:
    """Crea un punto de trabajo paramétrico de referencia en la pieza activa.

    Modos disponibles:
      - 'fixed' (default): punto fijo en coordenadas 3D absolutas.
        Parámetros: x, y, z (coordenadas), units.
      - 'midpoint': punto en el punto medio de una arista del sólido.
        Parámetros: edge_index (índice 1-based de la arista).
      - 'three_planes': punto en la intersección de tres planos de trabajo.
        Parámetros: plane1, plane2, plane3 ('XY'/'XZ'/'YZ', nombre o índice).

    El campo 'WorkPointIndex' del resultado puede pasarse como 'point1/point2/point3'
    en create_work_plane (modo 'three_points') o en create_work_axis (modo 'two_points').

    Args:
        mode:       Tipo de construcción: 'fixed', 'midpoint' o 'three_planes'.
        x, y, z:   Coordenadas del punto fijo (solo modo 'fixed').
        edge_index: Índice de la arista (solo modo 'midpoint').
        plane1:     Primer plano (solo modo 'three_planes').
        plane2:     Segundo plano (solo modo 'three_planes').
        plane3:     Tercer plano (solo modo 'three_planes').
        units:      Unidad de las coordenadas: 'mm' (default), 'cm', 'm', 'in'.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload: dict = {"mode": mode, "units": units}
        if mode == "fixed":
            payload["x"] = x
            payload["y"] = y
            payload["z"] = z
        elif mode == "midpoint":
            payload["edge_index"] = edge_index
        elif mode == "three_planes":
            payload["plane1"] = plane1
            payload["plane2"] = plane2
            payload["plane3"] = plane3
        data = await execute_in_inventor(usuario, "create_work_point", payload, timeout_seconds=20.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


# ── Grupo: Diagnóstico, consulta y exportación ──────────────────────────

@mcp.tool()
async def render_screenshot(width: int = 1280, height: int = 720) -> str:
    """Captura la vista actual de Inventor como imagen PNG codificada en base64.

    Esencial para retroalimentación visual multimodal: el campo 'base64_data' contiene
    la imagen PNG que puede ser interpretada por modelos con capacidad multimodal (Sonnet 4.6).

    Args:
        width:  Ancho de la imagen en píxeles (default 1280).
        height: Alto de la imagen en píxeles (default 720).
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"width": width, "height": height}
        data = await execute_in_inventor(usuario, "render_screenshot", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def export_to_stl(path: str = "") -> str:
    """Exporta el modelo activo (pieza o ensamble) a un archivo STL para impresión 3D.

    Args:
        path: Ruta completa donde guardar el archivo .stl (incluir extensión).
              Si se omite, se guarda junto al documento activo con el mismo nombre.
              Ejemplo: 'C:/Users/User/Documents/pieza.stl'
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"path": path} if path else {}
        data = await execute_in_inventor(usuario, "export_to_stl", payload, timeout_seconds=60.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def export_to_dxf(path: str = "") -> str:
    """Exporta la cara de una chapa metálica desplegada o un dibujo (drawing) a DXF.

    Útil para corte por láser o plasma. Para chapas metálicas exporta el flat pattern;
    para documentos de dibujo (.idw) exporta la primera hoja como DXF.

    Args:
        path: Ruta completa donde guardar el archivo .dxf (incluir extensión).
              Si se omite, se guarda junto al documento activo con el mismo nombre.
              Ejemplo: 'C:/Users/User/Documents/chapa.dxf'
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"path": path} if path else {}
        data = await execute_in_inventor(usuario, "export_to_dxf", payload, timeout_seconds=60.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def check_interference() -> str:
    """Detecta colisiones geométricas entre componentes del ensamble activo.

    Analiza todas las ocurrencias del ensamble en busca de interferencias.
    Devuelve los pares de componentes en colisión, el volumen de interferencia
    y el centroide de cada zona de colisión.

    Requiere que el documento activo sea un ensamble (.iam) con al menos 2 componentes.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(usuario, "check_interference", {}, timeout_seconds=60.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def get_mass_properties() -> str:
    """Calcula y devuelve las propiedades de masa del modelo activo.

    Retorna: masa (kg), volumen (cm³), área de superficie (cm²) y
    centro de gravedad (X, Y, Z) del primer cuerpo sólido o ensamble.

    Compatible con documentos de pieza (.ipt) y ensamble (.iam).
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(usuario, "get_mass_properties", {}, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


# ── Grupo: Ensambles ────────────────────────────────────────────────────

@mcp.tool()
async def insert_component(file_path: str) -> str:
    """Inserta una pieza (.ipt) o subensamble (.iam) en el ensamble activo.

    El componente se coloca en el origen (0,0,0) con orientación por defecto.
    Después de insertar, usa ground_component para fijar la base o
    add_assembly_constraint / add_assembly_joint para posicionarlo.

    Args:
        file_path: Ruta completa al archivo a insertar (.ipt o .iam).
                   Ejemplo: 'C:/Parts/bolt.ipt'

    Devuelve: OccurrenceName (nombre en el árbol del ensamble, ej. 'Bolt:1'),
              OccurrenceIndex (índice 1-based para usar en otras tools),
              FilePath, Grounded, TotalOccurrences.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(
            usuario, "insert_component", {"file_path": file_path}, timeout_seconds=30.0
        )
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def ground_component(occurrence: str, ground: bool = True) -> str:
    """Fija (o libera) un componente en el espacio para que sirva como base fija del ensamble.

    Un componente fijado (grounded) no puede moverse por restricciones o joints.
    Se recomienda fijar el primer componente base antes de añadir restricciones.

    Args:
        occurrence: Nombre (ej. 'Base:1') o índice 1-based del componente en el ensamble.
        ground:     True (default) = fijar el componente. False = liberar (desfijar).

    Devuelve: OccurrenceName, OccurrenceIndex, Grounded, TotalOccurrences.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(
            usuario, "ground_component",
            {"occurrence": occurrence, "ground": ground},
            timeout_seconds=15.0,
        )
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def add_assembly_constraint(
    occurrence1: str,
    occurrence2: str,
    constraint_type: str = "mate",
    face1: int = 1,
    face2: int = 1,
    value: float = 0.0,
    units: str = "mm",
    inside: bool = False,
    axes_opposed: bool = True,
) -> str:
    """Aplica una restricción de ensamble tradicional entre dos componentes.

    Las restricciones alinean caras, ejes o planos de dos componentes.
    Cada componente debe estar ya insertado (usa insert_component primero).

    Tipos de restricción disponibles:
      - 'mate':    Dos caras planas se tocan (o con offset). El tipo más común.
      - 'flush':   Dos caras planas quedan coplanares (o con offset).
      - 'angle':   Ángulo fijo entre dos caras. El parámetro 'value' es el ángulo en grados.
      - 'tangent': Una cara curva tangente a otra. 'inside=True' para tangencia interior.
      - 'insert':  Alinea eje cilíndrico y cara simultáneamente (para tornillos en agujeros).
                   'axes_opposed=True' para orientar los ejes en sentido contrario.

    Args:
        occurrence1:     Nombre o índice 1-based del primer componente.
        occurrence2:     Nombre o índice 1-based del segundo componente.
        constraint_type: Tipo: 'mate', 'flush', 'angle', 'tangent', 'insert'. Default 'mate'.
        face1:           Índice 1-based de la cara del primer componente. Default 1.
        face2:           Índice 1-based de la cara del segundo componente. Default 1.
        value:           Offset (mate/flush/tangent/insert en units) o ángulo (angle en grados).
        units:           Unidad del offset: 'mm' (default), 'cm', 'm', 'in'.
        inside:          Solo para 'tangent': True = tangencia interior.
        axes_opposed:    Solo para 'insert': True (default) = ejes opuestos.

    Devuelve: ConstraintName, EntityOne, EntityTwo, Value, Units, TotalConstraints.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload: dict = {
            "occurrence1":     occurrence1,
            "occurrence2":     occurrence2,
            "constraint_type": constraint_type,
            "face1":           face1,
            "face2":           face2,
            "value":           value,
            "units":           units,
            "inside":          inside,
            "axes_opposed":    axes_opposed,
        }
        data = await execute_in_inventor(usuario, "add_assembly_constraint", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def add_assembly_joint(
    occurrence1: str,
    occurrence2: str,
    joint_type: str = "rigid",
    face1: int = 1,
    face2: int = 1,
) -> str:
    """Conecta dos componentes con un Joint del sistema moderno de ensamble de Inventor.

    Los Joints definen la relación cinemática entre dos componentes: cuántos grados
    de libertad conservan (movimiento de simulación, análisis de mecanismos).

    Tipos de joint disponibles:
      - 'rigid':       Sin grados de libertad — los componentes se mueven juntos.
      - 'rotational':  Un grado de libertad de rotación (bisagra, pin).
      - 'sliding':     Un grado de libertad de traslación (guía lineal).
      - 'cylindrical': Rotación + traslación en el mismo eje (tornillo sin rosca modelada).
      - 'planar':      Traslación en dos ejes + rotación sobre el eje normal.
      - 'ball':        Tres grados de libertad de rotación (rótula).

    Args:
        occurrence1: Nombre o índice 1-based del primer componente.
        occurrence2: Nombre o índice 1-based del segundo componente.
        joint_type:  Tipo de joint (ver lista). Default 'rigid'.
        face1:       Índice 1-based de la cara del primer componente para el origen del joint.
        face2:       Índice 1-based de la cara del segundo componente para el origen del joint.

    Devuelve: JointName, JointType, ComponentOne, ComponentTwo, TotalJoints.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {
            "occurrence1": occurrence1,
            "occurrence2": occurrence2,
            "joint_type":  joint_type,
            "face1":       face1,
            "face2":       face2,
        }
        data = await execute_in_inventor(usuario, "add_assembly_joint", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def get_assembly_bom() -> str:
    """Extrae la lista de materiales (BOM) del ensamble activo.

    Devuelve la vista estructurada del BOM con número de ítem, cantidad y
    nombre de cada componente. Útil para verificar el contenido del ensamble
    y generar documentación o listas de compra.

    Requiere que el documento activo sea un ensamble (.iam).

    Devuelve: DocumentName, ViewType, TotalItems y BOMItems con:
              ItemNumber, Quantity, PartName, FileName.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        data = await execute_in_inventor(usuario, "get_assembly_bom", {}, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


# ── Grupo: Patrones y simetría ──────────────────────────────────────────

@mcp.tool()
async def create_rectangular_pattern(
    feature_names: str = "",
    x_axis: str = "X",
    x_count: int = 2,
    x_spacing: float = 10.0,
    y_count: int = 1,
    y_spacing: float = 10.0,
    y_axis: str = "",
    units: str = "mm",
) -> str:
    """Crea un patrón rectangular (arreglo de filas y columnas) de una o más operaciones.

    Duplica la operación indicada en una cuadrícula rectangular definida por dos ejes de trabajo.

    Args:
        feature_names: Nombre(s) o índice(s) de las operaciones a repetir, separados por coma.
                       Si se omite, toma la última operación del árbol del modelo.
                       Ejemplo: 'Extrusion1' o '3,4' (índices 1-based).
        x_axis:        Eje de trabajo para la dirección principal (X): 'X', 'Y', 'Z', nombre o índice.
                       Por defecto 'X'.
        x_count:       Número de instancias en la dirección X (incluyendo la original). Mínimo 2.
        x_spacing:     Espaciado entre instancias en la dirección X.
        y_count:       Número de instancias en la dirección Y. 1 = sin segunda dirección (default).
        y_spacing:     Espaciado entre instancias en la dirección Y.
        y_axis:        Eje de trabajo para la segunda dirección (Y). Si se omite, se elige
                       automáticamente un eje perpendicular al x_axis.
        units:         Unidad del espaciado: 'mm' (default), 'cm', 'm', 'in'.

    Devuelve: FeatureName, XAxis, XCount, XSpacing, YAxis, YCount, YSpacing,
              TotalInstances, BodyCount, FaceCount, EdgeCount.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload: dict = {
            "x_axis": x_axis,
            "x_count": x_count,
            "x_spacing": x_spacing,
            "y_count": y_count,
            "y_spacing": y_spacing,
            "units": units,
        }
        if feature_names:
            payload["feature_names"] = feature_names
        if y_axis:
            payload["y_axis"] = y_axis
        data = await execute_in_inventor(usuario, "create_rectangular_pattern", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def create_circular_pattern(
    feature_names: str = "",
    axis: str = "Z",
    count: int = 4,
    angle: float = 360.0,
    fit_within_angle: bool = True,
) -> str:
    """Crea un patrón circular de una operación alrededor de un eje de trabajo.

    Útil para espaciar agujeros, pasadores o salientes de forma uniforme alrededor de un eje,
    como los agujeros de perno en una brida.

    Args:
        feature_names:   Nombre(s) o índice(s) de la operación a repetir, separados por coma.
                         Si se omite, toma la última operación del árbol del modelo.
                         Ejemplo: 'Hole1' o '5'.
        axis:            Eje de trabajo de revolución: 'X', 'Y', 'Z', nombre o índice del eje.
                         Por defecto 'Z'.
        count:           Número total de instancias en el patrón (incluyendo la original).
        angle:           Ángulo total del patrón en grados (default 360 = círculo completo).
        fit_within_angle: True (default): el ángulo es el ángulo total y las instancias se
                          distribuyen dentro de él. False: el ángulo es el paso entre instancias.

    Devuelve: FeatureName, Axis, Count, TotalAngle, FitWithinAngle, BodyCount, FaceCount, EdgeCount.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload: dict = {
            "axis": axis,
            "count": count,
            "angle": angle,
            "fit_within_angle": fit_within_angle,
        }
        if feature_names:
            payload["feature_names"] = feature_names
        data = await execute_in_inventor(usuario, "create_circular_pattern", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def mirror_feature(
    feature_names: str = "",
    plane: str = "XZ",
) -> str:
    """Realiza la simetría (espejo) de una o más operaciones respecto a un plano de trabajo.

    Crea copias simétricas de las operaciones seleccionadas al otro lado del plano indicado.
    Las operaciones originales se conservan.

    Args:
        feature_names: Nombre(s) o índice(s) de la operación a espejar, separados por coma.
                       Si se omite, toma la última operación del árbol del modelo.
                       Ejemplo: 'Fillet1,Chamfer1' o '4'.
        plane:         Plano de simetría: 'XY', 'XZ' o 'YZ' (planos de origen),
                       o nombre/índice de cualquier plano de trabajo del documento.
                       Por defecto 'XZ'.

    Devuelve: FeatureName, Plane, FeaturesCount, BodyCount, FaceCount, EdgeCount.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload: dict = {"plane": plane}
        if feature_names:
            payload["feature_names"] = feature_names
        data = await execute_in_inventor(usuario, "mirror_feature", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


@mcp.tool()
async def mirror_solid(
    plane: str = "XZ",
    keep_original: bool = True,
) -> str:
    """Realiza la simetría (espejo) de todo el cuerpo sólido respecto a un plano de trabajo.

    Copia todo el sólido al otro lado del plano, lo que permite crear geometría simétrica
    compleja a partir de media pieza modelada.

    Args:
        plane:         Plano de simetría: 'XY', 'XZ' o 'YZ' (planos de origen),
                       o nombre/índice de cualquier plano de trabajo del documento.
                       Por defecto 'XZ'.
        keep_original: True (default) = mantiene el cuerpo original y añade el espejo.
                       False = elimina el cuerpo original y solo deja el espejo.

    Devuelve: FeatureName, Plane, MirroredFeatureCount, BodyCount, FaceCount, EdgeCount.
    """
    usuario = current_user_id.get(None)
    if not usuario:
        return "Error: sesión no autenticada."
    try:
        payload = {"plane": plane, "keep_original": keep_original}
        data = await execute_in_inventor(usuario, "mirror_solid", payload, timeout_seconds=30.0)
        return json.dumps(data, indent=2)
    except Exception as e:
        return f"Error: {str(e)}"


# =========================================================================
# ARRANQUE
# Claude Desktop lanza este proceso y habla JSON-RPC por stdin/stdout. No hay
# servidor HTTP, ni puertos, ni API keys: el único canal de salida es el named pipe
# hacia el add-in de Inventor.
# =========================================================================
def _run_cli(argv: list) -> Optional[int]:
    """Modos auxiliares que usa el instalador.

    Devuelve el código de salida, o None si hay que arrancar el servidor MCP. Aquí sí
    se puede escribir en stdout: no se está hablando JSON-RPC.
    """
    flags = {"--install-claude-config", "--remove-claude-config", "--print-claude-config"}
    requested = flags.intersection(argv)
    if not requested:
        return None

    import claude_config

    try:
        if "--print-claude-config" in requested:
            print(claude_config.snippet())
        elif "--install-claude-config" in requested:
            path, action = claude_config.install()
            print(f"Entrada '{claude_config.SERVER_KEY}' {action} en {path}")
            print("Reinicia Claude Desktop para que cargue el servidor.")
        else:
            path, action = claude_config.remove()
            print(f"Entrada '{claude_config.SERVER_KEY}' {action} en {path}")
    except (RuntimeError, OSError) as exc:
        print(f"Error: {exc}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    _code = _run_cli(sys.argv[1:])
    if _code is not None:
        sys.exit(_code)

    log.info("Servidor MCP de Inventor iniciado (pipe %s)", PIPE_PATH)
    # show_banner=False: el banner de FastMCP sale por stderr y solo ensucia el log
    # que muestra Claude Desktop.
    #
    # Nota: al cerrarse, el ejecutable de PyInstaller imprime en stderr un
    # "ValueError: I/O operation on closed file" que viene de la finalización de
    # FastMCP/docket, no de este código (no ocurre ejecutando main.py sin congelar, y
    # no se puede capturar desde aquí porque se lanza tras salir de mcp.run). Es ruido
    # de apagado, posterior al cierre de la sesión MCP, y no afecta a nada.
    mcp.run(transport="stdio", show_banner=False)
