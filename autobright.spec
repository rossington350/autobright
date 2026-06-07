# -*- mode: python ; coding: utf-8 -*-

import os

try:
    import yoctopuce
    yocto_dir = os.path.dirname(yoctopuce.__file__)
    yocto_cdll_dir = os.path.join(yocto_dir, 'cdll')
    yocto_dlls = []
    if os.path.isdir(yocto_cdll_dir):
        for dll_name in os.listdir(yocto_cdll_dir):
            if dll_name.lower().endswith('.dll'):
                yocto_dlls.append((os.path.join(yocto_cdll_dir, dll_name), os.path.join('yoctopuce', 'cdll')))
except Exception:
    yocto_dlls = []


a = Analysis(
    ['autobright.py'],
    pathex=[],
    binaries=[],
    datas=[('icon.ico', '.')] + yocto_dlls,
    hiddenimports=['yocto_api', 'yocto_lightsensor', 'yoctopuce.yocto_api', 'yoctopuce.yocto_lightsensor'],
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
    name='autobright',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=['icon.ico'],
)
