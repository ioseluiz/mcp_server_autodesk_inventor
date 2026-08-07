"""inventor-mcp.exe (PyInstaller) como servidor MCP por stdio.

Requiere el ejecutable construido (pyinstaller inventor-mcp.spec) y PipeHost.exe
escuchando. Es la prueba que detecta los imports dinámicos que PyInstaller no ve:
sin burner_redis empaquetado, el exe muere al arrancar.
"""
import asyncio
import sys
import time

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

import _paths

failed = 0


def check(ok, label, detail=""):
    global failed
    print(("  PASS  " if ok else "  FAIL  ") + label + (f"  [{detail}]" if detail else ""))
    if not ok:
        failed += 1


def params_for(pipe_name):
    return StdioServerParameters(
        command=str(_paths.SERVER_EXE), args=[], env={"INVENTOR_PIPE_NAME": pipe_name}
    )


async def run():
    if not _paths.SERVER_EXE.exists():
        print(f"  FALTA  {_paths.SERVER_EXE}")
        print("  Construye el servidor:  pyinstaller inventor-mcp.spec")
        return 1

    print("== inventor-mcp.exe congelado, transporte stdio ==")
    started = time.monotonic()
    async with stdio_client(params_for("PipeQueNoExiste")) as (read, write):
        async with ClientSession(read, write) as session:
            init = await session.initialize()
            elapsed = time.monotonic() - started
            check(init.serverInfo.name == "Autodesk Inventor 2026 Assistant",
                  "handshake del exe (stdout limpio)", init.serverInfo.name)
            # --onefile descomprime en un temporal antes de arrancar; si esto crece
            # demasiado, Claude Desktop daría el servidor por caído.
            check(elapsed < 30, "handshake dentro del margen de Claude Desktop",
                  f"{elapsed:.1f}s")

            listed = await session.list_tools()
            check(len(listed.tools) == 52, "tools/list devuelve las 52 herramientas",
                  str(len(listed.tools)))

            result = await session.call_tool("get_active_document_info", {})
            text = result.content[0].text if result.content else ""
            check("no está activo en Inventor" in text,
                  "bridge apagado -> mensaje accionable desde el exe")

    print("== cadena completa desde el exe (bridge C# escuchando) ==")
    async with stdio_client(params_for(_paths.TEST_PIPE)) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            result = await session.call_tool("close_sketch", {})
            text = result.content[0].text if result.content else ""
            check("No hay boceto activo" in text,
                  "el exe alcanzó el add-in y devolvió su mensaje", text[:50])

            result2 = await session.call_tool(
                "draw_rectangle", {"x1": 0, "y1": 0, "x2": 10.5, "y2": -4})
            text2 = result2.content[0].text if result2.content else ""
            check(text2.startswith("Error:"),
                  "payload con decimales y negativos llegó al add-in", text2[:50])

    print()
    print("TODO OK" if failed == 0 else f"{failed} COMPROBACIONES FALLIDAS")
    return failed


sys.exit(asyncio.run(run()))
