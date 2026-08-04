"""Rutas compartidas por las pruebas. Todo relativo al repositorio, nada absoluto."""

import sys
from pathlib import Path

TESTS = Path(__file__).resolve().parent
REPO = TESTS.parent
MCP_SERVER = REPO / "mcp_server"
MAIN_PY = MCP_SERVER / "main.py"
SERVER_EXE = MCP_SERVER / "dist" / "inventor-mcp.exe"
README = REPO / "README.md"
PIPE_HOST = TESTS / "PipeHost" / "bin" / "Release" / "net8.0-windows" / "PipeHost.exe"

# El intérprete que ejecuta las pruebas es el mismo que debe lanzar el servidor.
PYTHON = sys.executable

# Pipe de pruebas: nunca el nombre real, para no interferir con un Inventor abierto.
TEST_PIPE = "InventorMCPBridgeTests"
