"""Comprueba que la tabla de tools del README coincide con las tools reales del servidor.

La documentación de 52 herramientas se desincroniza en cuanto alguien añade o renombra
una. Esta prueba lo detecta, y con --update reescribe la tabla:

    python test_readme_tools.py            # verifica
    python test_readme_tools.py --update   # regenera la tabla del README
"""
import asyncio
import sys

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

import _paths

HEADER = "| Tool | Parámetros | Descripción |"


def row_for(tool):
    schema = tool.inputSchema or {}
    properties = sorted(schema.get("properties", {}).keys())
    required = set(schema.get("required", []))
    signature = ", ".join(p if p in required else f"{p}?" for p in properties) or "—"
    description = (tool.description or "").strip().splitlines()[0]
    return f"| `{tool.name}` | {signature} | {description} |"


async def tool_rows():
    params = StdioServerParameters(
        command=_paths.PYTHON, args=[str(_paths.MAIN_PY)], env={}
    )
    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            listed = await session.list_tools()
            return [row_for(tool) for tool in listed.tools]


def readme_table():
    """Devuelve (lineas_del_readme, indice_inicio, indice_fin) de las filas de la tabla."""
    lines = _paths.README.read_text(encoding="utf-8").splitlines()
    try:
        header = lines.index(HEADER)
    except ValueError:
        raise SystemExit(f"No se encontró la cabecera de la tabla en {_paths.README}")

    start = header + 2          # cabecera + separador |---|---|---|
    end = start
    while end < len(lines) and lines[end].startswith("| "):
        end += 1
    return lines, start, end


def main():
    expected = asyncio.run(tool_rows())
    lines, start, end = readme_table()
    documented = lines[start:end]

    if "--update" in sys.argv:
        updated = lines[:start] + expected + lines[end:]
        _paths.README.write_text("\n".join(updated) + "\n", encoding="utf-8")
        print(f"README actualizado con {len(expected)} tools.")
        return 0

    print("== la tabla de tools del README coincide con el servidor ==")
    if documented == expected:
        print(f"  PASS  {len(expected)} tools documentadas exactamente")
        print()
        print("TODO OK")
        return 0

    faltan = [r for r in expected if r not in documented]
    sobran = [r for r in documented if r not in expected]
    print(f"  FAIL  documentadas {len(documented)}, reales {len(expected)}")
    for row in faltan:
        print(f"    falta en el README: {row.split('|')[1].strip()}")
    for row in sobran:
        print(f"    sobra en el README: {row.split('|')[1].strip()}")
    if not faltan and not sobran:
        print("    los nombres coinciden pero cambiaron parámetros o descripciones")
    print()
    print("Regenera con:  python test_readme_tools.py --update")
    return 1


sys.exit(main())
