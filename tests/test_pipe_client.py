"""Cliente de pipe de main.py contra el BridgeService real del add-in.

Requiere PipeHost.exe escuchando (lo arranca run_all.ps1).
"""
import asyncio
import os
import sys

import _paths

os.environ["INVENTOR_PIPE_NAME"] = _paths.TEST_PIPE
sys.path.insert(0, str(_paths.MCP_SERVER))

import main  # noqa: E402

failed = 0


def check(ok, label, detail=""):
    global failed
    print(("  PASS  " if ok else "  FAIL  ") + label + (f"  [{detail}]" if detail else ""))
    if not ok:
        failed += 1


async def run():
    print("== cliente Python -> named pipe -> BridgeService (C#) ==")

    # 1. Error del add-in propagado como excepción.
    try:
        await main.execute_in_inventor("local", "__desconocido__", {}, 10.0)
        check(False, "comando desconocido debería lanzar")
    except RuntimeError as exc:
        check("Comando no reconocido: __desconocido__" in str(exc),
              "error del add-in propagado al llamante", str(exc)[:50])

    # 2. UTF-8 en ambas direcciones: el mensaje de error devuelve el comando recibido.
    try:
        await main.execute_in_inventor("local", "acción_señal_ñá", {}, 10.0)
        check(False, "debería lanzar")
    except RuntimeError as exc:
        check("acción_señal_ñá" in str(exc), "acentos y ñ intactos ida y vuelta")

    # 3. Payload grande: 3 MB en una sola línea de petición.
    try:
        await main.execute_in_inventor("local", "__grande__", {"blob": "A" * 3_000_000}, 30.0)
        check(False, "debería lanzar")
    except RuntimeError as exc:
        check("__grande__" in str(exc), "petición de 3 MB en una línea")

    # 4. Respuesta grande: el nombre del comando vuelve dentro del mensaje de error.
    #    Es el caso de render_screenshot, que devuelve un PNG en base64.
    try:
        await main.execute_in_inventor("local", "B" * 3_000_000, {}, 30.0)
        check(False, "debería lanzar")
    except RuntimeError as exc:
        check(len(str(exc)) > 3_000_000, "respuesta de 3 MB en una línea",
              f"{len(str(exc))} chars")

    # 5. Excepción dentro del handler (inventorApp es null en el host de pruebas).
    try:
        await main.execute_in_inventor("local", "get_active_doc_info", {}, 10.0)
        check(False, "debería lanzar")
    except RuntimeError as exc:
        check(len(str(exc)) > 0, "excepción del handler llega como error", str(exc)[:60])

    # 6. La conexión se reutiliza: sobrevive a todo lo anterior.
    try:
        await main.execute_in_inventor("local", "__ultimo__", {}, 10.0)
        check(False, "debería lanzar")
    except RuntimeError as exc:
        check("__ultimo__" in str(exc), "la conexión sigue viva tras 5 peticiones")

    # 7. Bridge apagado: mensaje accionable en lugar de un traceback.
    apagado = main.InventorBridge(r"\\.\pipe\NoExisteEsteBridge")
    try:
        await apagado.call("get_active_doc_info", {}, 5.0)
        check(False, "debería lanzar")
    except RuntimeError as exc:
        check("no está activo en Inventor" in str(exc),
              "pipe inexistente -> mensaje accionable")

    print()
    print("TODO OK" if failed == 0 else f"{failed} COMPROBACIONES FALLIDAS")
    return failed


sys.exit(asyncio.run(run()))
