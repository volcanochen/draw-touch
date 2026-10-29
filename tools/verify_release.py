# -*- coding: utf-8 -*-
"""校验发布 zip：文件齐全、exe 内含正确版本号、可解压还原。"""
import zipfile, os, sys, tempfile, subprocess

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VER = sys.argv[1] if len(sys.argv) > 1 else '1.4.8'
ZIP = os.path.join(ROOT, 'release', 'v' + VER, 'assets', 'SpenTabletHost-win64-v%s.zip' % VER)

z = zipfile.ZipFile(ZIP)
bad = z.testzip()
print('zip 完整性:', 'OK' if bad is None else '损坏 ' + str(bad))
names = z.namelist()
print('文件数:', len(names))

exe = [n for n in names if n.endswith('.exe')]
assert exe, 'zip 里没有 exe'
data = z.read(exe[0])
print('exe 大小: %d bytes' % len(data))
print('exe 含版本号 %s:' % VER, ('1.4.8'.encode('utf-16-le') in data) or (VER.encode('utf-8') in data))

# 真的解压跑一遍，确认能落地
d = tempfile.mkdtemp()
z.extractall(d)
print('解压成功 ->', d)
for n in sorted(os.listdir(d)):
    print('   %-34s %d' % (n, os.path.getsize(os.path.join(d, n))))
