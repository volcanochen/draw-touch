#!/usr/bin/env python3
"""
SpenTablet 发布门禁（release gate）。

发布任何版本前必须跑通全部三道检查，全绿才允许发布：
  1. Android 单元测试（Protocol 帧编码 + UpdateChecker 版本比较）
  2. PC 端 Release 编译
  3. 协议级冒烟测试（模拟手机连 host，断言各帧端到端行为）

用法：python tests/release_gate.py [--skip-gradle] [--skip-smoke]
退出码：0=全绿可发布，1=有失败禁止发布。
"""
import argparse
import os
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GRADLE = Path(r"C:\VOLCANO\myws\myEnv\tools\gradle-8.9\bin\gradle.bat")
JDK = Path(r"C:\VOLCANO\myws\myEnv\tools\jdk-17.0.13")
SDK = Path(r"C:\VOLCANO\myws\myEnv\tools\android-sdk")
DOTNET = Path(r"C:\Users\adminlocal\.dotnet\dotnet.exe")
HOST_EXE = ROOT / "PcHost" / "bin" / "Release" / "net8.0-windows" / "win-x64" / "SpenTabletHost.exe"
PORT = 6000

results = []


def step(name, ok, detail=""):
    results.append((name, ok))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f"  ({detail})" if detail else ""))


def run(cmd, **kw):
    print(f"  $ {' '.join(str(c) for c in cmd)}")
    return subprocess.run([str(c) for c in cmd], **kw)


def gate_gradle():
    print("\n[1/3] Android 单元测试")
    env = dict(os.environ, JAVA_HOME=str(JDK), ANDROID_HOME=str(SDK))
    r = run([GRADLE, "-p", ROOT / "PhoneTablet", "testReleaseUnitTest", "--offline", "-q"], env=env)
    step("gradle testReleaseUnitTest", r.returncode == 0)


def kill_host():
    """结束残留的 SpenTabletHost 进程。

    必须在做 dotnet build **之前**调用：运行中的 host 会锁住
    bin/Release 下的 SpenTabletHost.dll，导致编译报「无法复制文件」。
    """
    subprocess.run(["taskkill", "/IM", "SpenTabletHost.exe", "/F"],
                   capture_output=True)
    time.sleep(1)


def gate_pcbuild():
    print("\n[2/3] PC 端 Release 编译")
    # 编译前先结束残留 host —— 运行中的进程会锁住输出 DLL
    kill_host()
    r = run([DOTNET, "build", ROOT / "PcHost" / "SpenTabletHost.csproj", "-c", "Release"])
    detail = "" if r.returncode == 0 else (r.stdout or b"").decode(errors="ignore")[-400:]
    step("dotnet build -c Release", r.returncode == 0, detail)


def gate_smoke():
    print("\n[3/3] 协议冒烟测试")
    if not HOST_EXE.exists():
        step("host 可执行文件存在", False, str(HOST_EXE))
        return
    # 杀掉残留 host，门禁自己拉起一个干净实例
    kill_host()
    env = dict(os.environ, DOTNET_ROOT=str(DOTNET))
    host = subprocess.Popen([str(HOST_EXE)], env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        # 等端口就绪
        import socket
        ok = False
        for _ in range(30):
            time.sleep(0.5)
            try:
                s = socket.create_connection(("127.0.0.1", PORT), timeout=1)
                s.close()
                ok = True
                break
            except OSError:
                pass
        step("host 启动并监听 6000", ok)
        if not ok:
            return
        r = run([sys.executable, ROOT / "tests" / "protocol_smoke.py", "--port", str(PORT)])
        # 不写死断言条数：用例会随回归项增加，写死容易与脚本实际不符
        step("protocol_smoke.py 全部断言", r.returncode == 0)
    finally:
        host.terminate()
        try:
            host.wait(timeout=5)
        except subprocess.TimeoutExpired:
            host.kill()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--skip-gradle", action="store_true", help="跳过 Android 单测（临时调试用）")
    ap.add_argument("--skip-smoke", action="store_true", help="跳过冒烟测试（临时调试用）")
    args = ap.parse_args()

    print("== SpenTablet 发布门禁 ==")
    if not args.skip_gradle:
        gate_gradle()
    gate_pcbuild()
    if not args.skip_smoke:
        gate_smoke()

    print("\n== 门禁结果 ==")
    failed = [n for n, ok in results if not ok]
    for n, ok in results:
        print(f"  [{'PASS' if ok else 'FAIL'}] {n}")
    if failed:
        print(f"\n⛔ 有 {len(failed)} 项失败，禁止发布：{'; '.join(failed)}")
        sys.exit(1)
    print("\n✅ 门禁全绿，允许发布。")


if __name__ == "__main__":
    main()
