# -*- mode: python ; coding: utf-8 -*-
#
# Empaquetado del servidor MCP local:  pyinstaller inventor-mcp.spec
#
# Cada inclusión responde a un fallo real detectado al ejecutar el exe:
#  - copy_metadata: fastmcp y mcp leen su propia versión con importlib.metadata.
#  - collect_submodules(fastmcp/mcp): cargan middleware y proveedores por nombre.
#  - burner_redis: FastMCP arranca siempre "docket" (su worker de tareas) y con el
#    backend por defecto memory:// éste importa burner_redis con import_module. Sin
#    esto el exe muere al iniciar con ModuleNotFoundError: No module named
#    'burner_redis'. No usamos tareas, pero el lifespan de FastMCP 2.14 no es opcional.
from PyInstaller.utils.hooks import collect_submodules
from PyInstaller.utils.hooks import collect_all
from PyInstaller.utils.hooks import copy_metadata

datas = []
binaries = []
hiddenimports = []
datas += copy_metadata('fastmcp')
datas += copy_metadata('mcp')
datas += copy_metadata('pydocket')
hiddenimports += collect_submodules('fastmcp')
hiddenimports += collect_submodules('mcp')
hiddenimports += collect_submodules('docket')
tmp_ret = collect_all('burner_redis')
datas += tmp_ret[0]; binaries += tmp_ret[1]; hiddenimports += tmp_ret[2]


a = Analysis(
    ['main.py'],
    pathex=[],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='inventor-mcp',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=True,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
