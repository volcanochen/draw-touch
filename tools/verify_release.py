# -*- coding: utf-8 -*-
"""
校验发布 zip：文件齐全、**解压后 exe 真能独立跑起来并自报正确版本**。

为什么必须实跑：
    - v1.4.8 的包里 exe 是 `dotnet build` 的 apphost，**依赖同目录 60 多个 dll**，
      zip 里没带 → 用户下载解压后双击就报
      "The application to execute does not exist: DrawTouchHost.dll"。
      当时只做了「明文搜版本号」这种检查，而单文件发布会把程序集压缩进 exe，
      **搜不到字符串** —— 那个检查一直是假的（v1.3.1 的包也搜不到）。
    - 现在改成：解压到临时目录 → 直接运行 `exe --version` → 必须输出正确版本号。
      跑不起来 = 包是坏的，当场暴露。

`--version` 分支在提权逻辑之前，不会弹 UAC、不会占 6000 端口。
"""
import hashlib
import os
import subprocess
import sys
import tempfile
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VER = sys.argv[1] if len(sys.argv) > 1 else '1.4.9'
ZIP = os.path.join(ROOT, 'release', 'v' + VER, 'assets', 'DrawTouchHost-win64-v%s.zip' % VER)

fail = []


def md5(path):
    h = hashlib.md5()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


if not os.path.exists(ZIP):
    sys.exit('[FAIL] 找不到发布包: %s' % ZIP)

z = zipfile.ZipFile(ZIP)
bad = z.testzip()
print('zip 完整性:', 'OK' if bad is None else '损坏 ' + str(bad))
if bad is not None:
    fail.append('zip 损坏')

names = z.namelist()
print('文件数: %d  %s' % (len(names), names))
if 'DrawTouchHost.exe' not in names:
    sys.exit('[FAIL] zip 里没有 DrawTouchHost.exe')
if 'appsettings.json' not in names:
    fail.append('zip 里没有 appsettings.json')

# 1) 解压到空目录（**空目录**是关键：旁边没有 dll 时还能跑 = 真自包含）
d = tempfile.mkdtemp(prefix='spen_release_check_')
z.extractall(d)
exe_path = os.path.join(d, 'DrawTouchHost.exe')
print('解压到空目录 ->', d)

# 2) 实跑：必须自报正确版本
try:
    out = subprocess.run([exe_path, '--version'], capture_output=True, text=True, timeout=180)
    reported = (out.stdout or '').strip()
    err = (out.stderr or '').strip()
except Exception as e:
    reported, err = '', '运行异常: %s' % e
print('exe --version 输出:', reported or '(空)')
if err:
    print('exe stderr        :', err[:300])
if VER not in reported:
    fail.append('exe 自报版本不含 %s（解压后跑不起来 = 包是坏的）' % VER)
else:
    print('exe 自报版本号 %s: OK' % VER)

# 3) 与 publish 产物一致性（确认打的是最新编译的）
src = os.path.join(ROOT, 'PcHost', 'publish', 'win-x64', 'DrawTouchHost.exe')
if os.path.exists(src):
    same = md5(src) == md5(exe_path)
    print('与 publish 编译产物一致:', 'OK' if same else '不一致')
    if not same:
        fail.append('zip 里的 exe 不是最新 publish 产物')

for n in sorted(os.listdir(d)):
    print('   %-34s %d' % (n, os.path.getsize(os.path.join(d, n))))

if fail:
    print('\n[FAIL] ' + '；'.join(fail))
    sys.exit(1)
print('\n[PASS] 发布产物校验通过（解压后 exe 可独立运行，版本正确）')
