# -*- coding: utf-8 -*-
"""
打包 PC 端发布产物（zip）+ 手机端 APK。

⚠️ 关键点：PC 端的 exe **必须取 `dotnet publish` 的单文件产物**，不能取 `dotnet build` 的产物。

    dotnet build   → bin/Release/.../DrawTouchHost.exe 是 apphost（约 9.6MB），
                      它**依赖同目录的 60 多个 dll 和运行时**才能启动；
                      单独拷走 → 启动就报
                      "The application to execute does not exist: DrawTouchHost.dll"。
    dotnet publish → publish/win-x64/DrawTouchHost.exe 是**自包含单文件**（约 162MB），
                      .NET 运行时 + WPF 原生库全内嵌，**解压即用、零依赖**。

    v1.4.8 的 Release 包就是踩了这个坑（拿的是 build 产物），zip 里缺 dll，
    用户下载解压后根本跑不起来。本脚本 v1.4.9 起改为 publish 产物 + 打包后实测。

zip 里只放 2 个文件：exe（自包含单文件）+ appsettings.json。

用法：
    python tools/make_release.py [版本号]
"""
import os
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VER = sys.argv[1] if len(sys.argv) > 1 else '1.4.9'
PCHOST = os.path.join(ROOT, 'PcHost')
PUBLISH = os.path.join(PCHOST, 'publish', 'win-x64')
REL = os.path.join(ROOT, 'release', 'v' + VER)
ASSETS = os.path.join(REL, 'assets')
os.makedirs(ASSETS, exist_ok=True)


def find_dotnet():
    """本机 dotnet 不在 PATH 上（PATH 上的那个找不到 SDK），优先用 ~/.dotnet/dotnet.exe。"""
    for c in (os.path.expanduser('~/.dotnet/dotnet.exe'),
              r'C:\Users\adminlocal\.dotnet\dotnet.exe'):
        if os.path.exists(c):
            return c
    return 'dotnet'


def publish():
    print('[1/2] dotnet publish（自包含单文件）…')
    cmd = [find_dotnet(), 'publish', '-c', 'Release', '-r', 'win-x64',
           '--self-contained', 'true',
           '-p:PublishSingleFile=true',
           '-p:IncludeNativeLibrariesForSelfExtract=true',
           '-o', PUBLISH]
    r = subprocess.run(cmd, cwd=PCHOST, capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-3000:])
        print(r.stderr[-3000:])
        sys.exit('publish 失败')
    print('      ok ->', PUBLISH)


def main():
    publish()

    exe = os.path.join(PUBLISH, 'DrawTouchHost.exe')
    cfg = os.path.join(PUBLISH, 'appsettings.json')
    for f in (exe, cfg):
        if not os.path.exists(f):
            sys.exit('publish 产物缺失: %s' % f)

    # 发布前自检：exe 必须能独立报版本（证明是真单文件、不是缺 dll 的 apphost）
    out = subprocess.run([exe, '--version'], capture_output=True, text=True, timeout=120)
    reported = (out.stdout or '').strip()
    if VER not in reported:
        sys.exit('publish 产物的 exe 自报版本异常（期望含 %s，实际 %r）' % (VER, reported))
    print('      exe 自报版本:', reported)

    print('[2/2] 打 zip…')
    zip_path = os.path.join(ASSETS, 'DrawTouchHost-win64-v%s.zip' % VER)
    if os.path.exists(zip_path):
        os.remove(zip_path)
    with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        z.write(exe, 'DrawTouchHost.exe')
        z.write(cfg, 'appsettings.json')
    print('PC zip :', zip_path, os.path.getsize(zip_path), 'bytes')

    apk_src = os.path.join(ROOT, 'PhoneTablet', 'app', 'build', 'outputs', 'apk', 'debug', 'app-debug.apk')
    apk_dst = os.path.join(ASSETS, 'DrawTouchPhone-v%s.apk' % VER)
    shutil.copyfile(apk_src, apk_dst)
    print('APK    :', apk_dst, os.path.getsize(apk_dst), 'bytes')


if __name__ == '__main__':
    main()
