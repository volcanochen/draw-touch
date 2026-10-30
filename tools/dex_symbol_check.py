#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
装机核验：扫描 APK 里的 classes*.dex，确认新符号确实进了包。

为什么要扫 dex（而不是只看编译通过）：
- 编译通过 ≠ 装到手机上跑的是这份代码（gradle 用的是旧 apk / 装错变体 / dex 分片）。
- 中文串在 dex 里是 **MUTF-8**（非 ASCII 3 字节），不能按 UTF-8 / UTF-16 比对，
  否则会误报 MISS。

用法：
    python tools/dex_symbol_check.py <apk路径> [符号1 符号2 ...]
"""
import re
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

# 符号 → 期望出现的 dex 文件（None = 任意）
# 中文串在 dex 里是 MUTF-8 编码的「非 ASCII 按 3 字节存」的变体，
# 所以用这个正则去匹配（等价于把串当 UTF-16 变体再转字节，这里直接按 MUTF-8 规则构造）。
def utf8_mutf8(text: str) -> bytes:
    out = bytearray()
    for ch in text:
        cp = ord(ch)
        if cp == 0:
            out += b"\x00\x00"
        elif cp < 0x80:
            out.append(cp)
        elif cp < 0x800:
            out.append(0xC0 | (cp >> 6))
            out.append(0x80 | (cp & 0x3F))
        elif cp < 0x10000:
            out.append(0xE0 | (cp >> 12))
            out.append(0x80 | ((cp >> 6) & 0x3F))
            out.append(0x80 | (cp & 0x3F))
        else:
            # 代理对：MUTF-8 用 3 字节的 CESU-8 形式
            cp -= 0x10000
            hi = 0xD800 + (cp >> 10)
            lo = 0xDC00 + (cp & 0x3FF)
            for c in (hi, lo):
                out.append(0xE0 | (c >> 12))
                out.append(0x80 | ((c >> 6) & 0x3F))
                out.append(0x80 | (c & 0x3F))
    return bytes(out)


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    apk = Path(sys.argv[1])
    symbols = sys.argv[2:]
    if not apk.exists():
        print(f"[FAIL] APK 不存在: {apk}")
        return 1

    with tempfile.TemporaryDirectory() as td:
        with zipfile.ZipFile(apk) as z:
            dex_names = [n for n in z.namelist()
                         if re.fullmatch(r"classes\d*\.dex", n)]
            if not dex_names:
                print("[FAIL] APK 里没有 classes*.dex")
                return 1
            blobs = {}
            for n in dex_names:
                data = z.read(n)
                blobs[n] = data
                print(f"  {n}: {len(data)} 字节")

        # 符号只可能以「UTF-8（ASCII 部分）」「MUTF-8（中文部分）」两种形式出现，
        # 这里两种都试一遍。
        miss = []
        for sym in symbols:
            hit = None
            for enc_name, pat in (("utf8", sym.encode("utf-8")),
                                  ("mutf8", utf8_mutf8(sym))):
                for n, data in blobs.items():
                    if pat in data:
                        hit = f"{n}({enc_name})"
                        break
                if hit:
                    break
            if hit:
                print(f"[HIT ] {sym} -> {hit}")
            else:
                print(f"[MISS] {sym}")
                miss.append(sym)

        if miss:
            print(f"\n[FAIL] {len(miss)} 个符号没找到: {', '.join(miss)}")
            return 1
        print(f"\n[PASS] {len(symbols)} 个符号全部命中（已扫全部 {len(blobs)} 个 dex）")
        return 0


if __name__ == "__main__":
    sys.exit(main())
