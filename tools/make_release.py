# -*- coding: utf-8 -*-
"""打包 PC 端发布产物：只取运行必需的文件，与 v1.3.1 的 zip 结构保持一致。"""
import os, sys, zipfile, shutil

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VER = sys.argv[1] if len(sys.argv) > 1 else '1.4.8'
BIN = os.path.join(ROOT, 'PcHost', 'bin', 'Release', 'net8.0-windows', 'win-x64')
REL = os.path.join(ROOT, 'release', 'v' + VER)
ASSETS = os.path.join(REL, 'assets')
os.makedirs(ASSETS, exist_ok=True)

FILES = [
    'appsettings.json',
    'D3DCompiler_47_cor3.dll',
    'PenImc_cor3.dll',
    'PresentationNative_cor3.dll',
    'SpenTabletHost.exe',
    'vcruntime140_cor3.dll',
    'wpfgfx_cor3.dll',
]

missing = [f for f in FILES if not os.path.exists(os.path.join(BIN, f))]
if missing:
    print('缺少文件:', missing)
    sys.exit(1)

zip_path = os.path.join(ASSETS, 'SpenTabletHost-win64-v%s.zip' % VER)
if os.path.exists(zip_path):
    os.remove(zip_path)
with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for f in FILES:
        z.write(os.path.join(BIN, f), f)

print('PC zip :', zip_path, os.path.getsize(zip_path), 'bytes')

apk_src = os.path.join(ROOT, 'PhoneTablet', 'app', 'build', 'outputs', 'apk', 'debug', 'app-debug.apk')
apk_dst = os.path.join(ASSETS, 'SpenTabletPhone-v%s.apk' % VER)
shutil.copyfile(apk_src, apk_dst)
print('APK    :', apk_dst, os.path.getsize(apk_dst), 'bytes')
