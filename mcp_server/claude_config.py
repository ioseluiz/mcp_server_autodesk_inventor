"""Alta y baja del servidor en la configuración de Claude Desktop.

Vive en Python y no en el instalador porque hay que **fusionar** la entrada con los
mcpServers que el usuario ya tenga configurados, y eso exige un parser JSON de verdad.
El instalador solo invoca `inventor-mcp.exe --install-claude-config`.
"""

import json
import os
import shutil
import sys
from typing import Any, Dict, Tuple

SERVER_KEY = "inventor"


def config_path() -> str:
    appdata = os.environ.get("APPDATA")
    if not appdata:
        raise RuntimeError("No se pudo determinar %APPDATA%.")
    return os.path.join(appdata, "Claude", "claude_desktop_config.json")


def server_entry() -> Dict[str, Any]:
    """Cómo debe lanzar Claude Desktop este servidor.

    Congelado con PyInstaller, `sys.executable` ya es inventor-mcp.exe. Sin congelar,
    hay que pasarle el intérprete del venv y la ruta absoluta de main.py, porque Claude
    Desktop lanza el proceso con un directorio de trabajo arbitrario.
    """
    if getattr(sys, "frozen", False):
        return {"command": sys.executable, "args": []}

    main_script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "main.py")
    return {"command": sys.executable, "args": [main_script]}


def snippet() -> str:
    return json.dumps(
        {"mcpServers": {SERVER_KEY: server_entry()}}, indent=2, ensure_ascii=False
    )


def _load(path: str) -> Dict[str, Any]:
    try:
        with open(path, "r", encoding="utf-8-sig") as handle:
            loaded = json.load(handle)
    except json.JSONDecodeError as exc:
        raise RuntimeError(
            f"{path} no contiene JSON válido ({exc}). "
            "Añade la entrada a mano en lugar de sobrescribir el archivo."
        ) from None

    if not isinstance(loaded, dict):
        raise RuntimeError(f"{path} no contiene un objeto JSON en la raíz.")
    return loaded


def _save(path: str, data: Dict[str, Any]) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(data, handle, indent=2, ensure_ascii=False)
        handle.write("\n")


def install() -> Tuple[str, str]:
    """Añade o actualiza la entrada. Devuelve (ruta, acción realizada)."""
    path = config_path()
    os.makedirs(os.path.dirname(path), exist_ok=True)

    if os.path.exists(path):
        data = _load(path)
        shutil.copyfile(path, path + ".bak")
        action = "actualizada"
    else:
        data = {}
        action = "creada"

    servers = data.setdefault("mcpServers", {})
    if not isinstance(servers, dict):
        raise RuntimeError('La clave "mcpServers" existe y no es un objeto JSON.')

    servers[SERVER_KEY] = server_entry()
    _save(path, data)
    return path, action


def remove() -> Tuple[str, str]:
    """Quita la entrada dejando intactos los demás servidores."""
    path = config_path()
    if not os.path.exists(path):
        return path, "no existía"

    data = _load(path)
    servers = data.get("mcpServers")
    if not isinstance(servers, dict) or SERVER_KEY not in servers:
        return path, "no estaba configurado"

    shutil.copyfile(path, path + ".bak")
    del servers[SERVER_KEY]
    _save(path, data)
    return path, "eliminada"
