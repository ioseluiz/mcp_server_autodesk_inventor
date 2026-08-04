"""Transporte stdio de main.py, igual que lo usa Claude Desktop.

Con PipeHost.exe escuchando, comprueba además la cadena completa.
El handshake solo funciona si stdout está limpio de cualquier print().
"""
import asyncio
import sys

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
        command=_paths.PYTHON,
        args=[str(_paths.MAIN_PY)],
        env={"INVENTOR_PIPE_NAME": pipe_name},
    )


async def run():
    print("== transporte stdio (como Claude Desktop) ==")
    async with stdio_client(params_for("PipeQueNoExiste")) as (read, write):
        async with ClientSession(read, write) as session:
            init = await session.initialize()
            check(init.serverInfo.name == "Autodesk Inventor 2026 Assistant",
                  "handshake initialize (stdout limpio de prints)", init.serverInfo.name)

            listed = await session.list_tools()
            check(len(listed.tools) == 52, "tools/list devuelve las 52 herramientas",
                  str(len(listed.tools)))

            names = {t.name for t in listed.tools}
            check("get_active_document_info" in names and "extrude_profile" in names,
                  "nombres de tools preservados")

            # Bridge apagado: la tool devuelve el mensaje accionable sin romper la sesión.
            result = await session.call_tool("get_active_document_info", {})
            text = result.content[0].text if result.content else ""
            check("no está activo en Inventor" in text,
                  "tool con bridge apagado -> mensaje accionable", text[:60])

            again = await session.list_tools()
            check(len(again.tools) == 52, "la sesión sobrevive al error de la tool")

    print("== cadena completa MCP -> stdio -> pipe -> C# ==")
    async with stdio_client(params_for(_paths.TEST_PIPE)) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()

            # Llega al add-in: el handler falla porque no hay Inventor, pero el error
            # viaja C# -> pipe -> Python -> MCP -> cliente.
            result = await session.call_tool("get_active_document_info", {})
            text = result.content[0].text if result.content else ""
            check("no está activo" not in text and text.startswith("Error:"),
                  "la tool alcanzó el add-in (error del handler, no del pipe)", text[:60])

            result2 = await session.call_tool(
                "update_parameter", {"name": "largo", "value": "50 mm"})
            text2 = result2.content[0].text if result2.content else ""
            check(text2.startswith("Error:"), "tool con payload alcanzó el add-in", text2[:60])

            result3 = await session.call_tool("close_sketch", {})
            text3 = result3.content[0].text if result3.content else ""
            check("No hay boceto activo" in text3,
                  "mensaje real de un handler, con acentos", text3[:50])

    print()
    print("TODO OK" if failed == 0 else f"{failed} COMPROBACIONES FALLIDAS")
    return failed


sys.exit(asyncio.run(run()))
