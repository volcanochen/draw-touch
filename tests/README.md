# SpenTablet 自动化测试套件

> 本文档描述 `SpenTablet/tests/` 目录及 `PhoneTablet/app/src/test/` 目录中**实际存在**的测试代码。
> 所有测试项、命令、断言内容均来源于仓库代码事实；未实现的测试能力在文中明确标注为「暂无」。

---

## 1. 测试简介

### 1.1 覆盖范围

测试套件覆盖 SpenTablet 项目的**手机端（Android/Kotlin）↔ PC 端（.NET 8 WinForms）之间的自定义二进制协议**，以及两端各自可独立验证的纯逻辑单元。

| 层次 | 被测对象 | 运行位置 | 是否需要设备 |
|---|---|---|---|
| 协议级集成测试 | `PcHost/TcpPenServer.cs`（帧解析、模式切换、鼠标动作、截图下发、备注、激光笔、平移/复位） | PC（真实 host 进程 + 真实 TCP socket） | 否 |
| 纯 JVM 单元测试 | `PhoneTablet/.../Protocol.kt`（帧编码）、`PhoneTablet/.../UpdateChecker.kt`（语义化版本比较） | PC（Gradle `testReleaseUnitTest`） | 否 |
| 系统 API 诊断探针 | Windows `InitializeTouchInjection` / `InjectTouchInput`（user32 P/Invoke） | PC（交互式桌面会话） | 否（但需要真实桌面会话） |

**核心功能验证链路**：`tests/protocol_smoke.py` 用 Python 直接构造二进制帧，通过 TCP 连到本机正在运行的 `SpenTabletHost.exe`，逐项发帧并**同时断言「协议回包」与「host.log 日志」**。它等价于一个「无 Android 设备、只依赖 PC 端」的伪手机端，用于在每次改动 PC 端后做快速回归。

### 1.2 被测对象的协议常量（测试脚本内硬编码，与 PC 端 `TcpPenServer.cs` 对齐）

| 帧类型 | 码 | 测试脚本常量 | 说明 |
|---|---|---|---|
| HELLO | 0x05 | `T_HELLO` | 握手（7 个 float：surfaceW/surfaceH/aspectMode/workMode/frameL/frameT/frameScale） |
| PAN | 0x06 | `T_PAN` | 手指平移操作范围框 |
| MOUSE | 0x07 | `T_MOUSE` | 触摸板模式的鼠标动作 |
| MODE | 0x08 | `T_MODE` | 数位板 / 触摸板模式切换 |
| ZOOM | 0x09 | `T_ZOOM` | 缩放操作范围框（**脚本目前未发此帧**，见 §7） |
| RESET | 0x0A | `T_RESET` | 操作框复位 |
| INFO | 0x0C | `T_INFO` | PC 主动下发的信息（URL-encoded） |
| NOTE | 0x0D | `T_NOTE` | 桌面笔迹叠加层控制 |
| SHOT_REQ | 0x0E | `T_SHOT_REQ` | 请求截图 |
| SHOT | 0x0F | `T_SHOT` | 截图分片（totalLen u32 + seq u16 + data ≤ 60000B） |
| HL | 0x10 | `T_HL` | 激光笔开关 + 颜色索引 |

MOUSE 动作码（`MOUSE_*`）：0 移动 / 1 左键按下 / 2 左键抬起 / 3 左键点击 / 4 右键 / 5 滚轮 / 6 捏合缩放 / 7 中键 / 8 后退 / 9 前进 / 10 音量+ / 11 音量- / 12 静音 / 13 下一任务 / 14 上一任务 / 15 手指落放。

### 1.3 当前**不覆盖**的范围（明确声明）

- 无 Android UI 自动化（无 Espresso / UIAutomator / Robolectric 依赖）
- 无真机端到端自动化（S Pen 压感、触摸板手势、悬浮面板拖拽等依赖真实硬件与人工观察）
- 无性能、压力、并发、长时间稳定性测试
- 无 CI 配置（仓库内**无 `.github/` 目录**）
- 无 Python 侧单元测试框架（`protocol_smoke.py` / `release_gate.py` 为自包含脚本，无 pytest 等依赖）

---

## 2. 安装与运行方式

### 2.1 依赖清单

| 依赖 | 本机实际路径 / 版本 | 用途 |
|---|---|---|
| Python 3 | `C:\Users\adminlocal\.workbuddy\binaries\python\versions\3.13.12\python.exe` | 跑冒烟测试与门禁脚本（仅用标准库 `socket/struct/argparse/pathlib`） |
| .NET SDK 8 | `C:\Users\adminlocal\.dotnet\dotnet.exe` | 编译 PC 端与 TouchProbe |
| .NET 8 运行时 | `C:\Users\adminlocal\.dotnet\shared\Microsoft.NETCore.App\8.0.33` | 运行 **框架依赖** 版 host（`bin/Release/.../SpenTabletHost.exe`） |
| JDK 17 | `C:\VOLCANO\myws\myEnv\tools\jdk-17.0.13` | Gradle 编译 Android 端 |
| Android SDK | `C:\VOLCANO\myws\myEnv\tools\android-sdk` | `compileSdk 34` / `buildTools` |
| Gradle 8.9 | `C:\VOLCANO\myws\myEnv\tools\gradle-8.9\bin\gradle.bat` | 运行 Android 单元测试与构建 |
| JUnit 4.13.2 | 由 Gradle 解析（`PhoneTablet/app/build.gradle` 的 `testImplementation 'junit:junit:4.13.2'`） | Android 单元测试 |

> **运行时坑**：`C:\Program Files\dotnet` 只有 6.0.25 / 9.0.3 / 9.0.5，**没有 8.0**。
> 直接双击 `bin/Release/.../SpenTabletHost.exe` 会报 “You must install or update .NET to run this application”。
> 解决：设置 `DOTNET_ROOT=C:\Users\adminlocal\.dotnet`（`release_gate.py` 已自动设置）。
> 若使用 `publish` 目录里的自包含单文件 exe，则无需任何 .NET 运行时。

### 2.2 环境变量

| 变量 | 值 | 何时需要 |
|---|---|---|
| `DOTNET_ROOT` | `C:\Users\adminlocal\.dotnet` | 运行框架依赖版 host / `dotnet` 命令行时 |
| `JAVA_HOME` | `C:\VOLCANO\myws\myEnv\tools\jdk-17.0.13` | 跑 Gradle 时 |
| `ANDROID_HOME` | `C:\VOLCANO\myws\myEnv\tools\android-sdk` | 跑 Gradle 时 |

### 2.3 运行命令

**① Android 单元测试（15 项，纯 JVM，无需设备）**

```bash
cd SpenTablet/PhoneTablet
JAVA_HOME=/c/VOLCANO/myws/myEnv/tools/jdk-17.0.13 \
ANDROID_HOME=/c/VOLCANO/myws/myEnv/tools/android-sdk \
/c/VOLCANO/myws/myEnv/tools/gradle-8.9/bin/gradle -p . testReleaseUnitTest --offline -q
```

> `--offline` 依赖本机 Gradle 缓存中已有 `junit:junit:4.13.2`；**首次使用需去掉 `--offline` 联网下载**。

**② 协议级冒烟测试（16 项断言）**

前置：PC 端 host 正在运行并监听 6000 端口。

```bash
# 方式 A：用框架依赖版（需 DOTNET_ROOT）
export DOTNET_ROOT="/c/Users/adminlocal/.dotnet"
./PcHost/bin/Release/net8.0-windows/win-x64/SpenTabletHost.exe   # 后台常驻

# 方式 B：用自包含单文件版（无需运行时）
./PcHost/bin/Release/net8.0-windows/win-x64/publish/SpenTabletHost.exe

# 跑测试
python tests/protocol_smoke.py --host 127.0.0.1 --port 6000
```

输出形如 `[PASS] HELLO → 收到 INFO 回包  (type=0xc)`，全部通过时末行打印 `ALL GREEN`，退出码 0；有失败则打印失败项列表并以退出码 1 结束。

**③ 发布门禁（推荐入口：一次跑完三道检查）**

```bash
python tests/release_gate.py
# 调试用：python tests/release_gate.py --skip-gradle / --skip-smoke
```

门禁会依次执行：Gradle 单元测试 → `dotnet build -c Release` → **自动 taskkill 残留 host、拉起干净 host、跑冒烟测试、最后关闭 host**，任一阶段失败即以退出码 1 结束并打印「⛔ 有 N 项失败，禁止发布」。

**④ 触摸注入诊断探针（打印参数矩阵结果，非断言式）**

```bash
cd tests/TouchProbe
/c/Users/adminlocal/.dotnet/dotnet.exe run -c Release
```

**⑤ 手动验证（需要真机 + 人工观察，无自动化脚本）**

- 手机端安装：`PhoneTablet/app/build/outputs/apk/debug/app-debug.apk`（debug 签名，可覆盖安装）
- PC 端运行：解压 `release/<版本>/assets/SpenTabletHost-win64-vX.Y.Z.zip` 双击 `SpenTabletHost.exe`（自包含，Win10 1809+）
- 手机与 PC 同一局域网；USB 调试场景可 `adb reverse tcp:6000 tcp:6000`
- 手机端连上后打开「设置」可查看 PC 主机信息与网络拓扑；调试日志见手机端 `DebugLog.kt` 的落盘目录

---

## 3. 目录结构说明

```
SpenTablet/
├── tests/                                   # 本测试项目
│   ├── protocol_smoke.py                    # 协议级冒烟测试（16 项断言，唯一“真断言”脚本）
│   ├── release_gate.py                      # 发布门禁（串起单测 + PC 编译 + 冒烟）
│   └── TouchProbe/                          # Windows 触摸注入参数诊断探针
│       ├── Program.cs                       #   探针主程序（11 组参数实验 + 环境信息打印）
│       ├── TouchProbe.csproj                #   net8.0-windows 控制台工程（UseWindowsForms=true）
│       ├── bin/  obj/                       #   构建产物（已加入 .gitignore，不入库）
│       └── (运行结果仅打印到控制台，无断言、无退出码语义)
└── PhoneTablet/app/src/test/java/com/volcano/spentablet/   # Android 端单元测试（与主源码同 module）
    ├── ProtocolTest.kt                      # 帧编码字节级断言 + 动作码契约（8 项）
    └── UpdateCheckerVersionTest.kt          # 语义化版本比较（7 项）
```

### 各文件职责

**`tests/protocol_smoke.py`**（235 行）
- `frame()` / `f32()`：构造小端二进制帧（magic `SP` + ver 1 + type + u16 长度 + payload）
- `recv_frame()`：按帧头长度精确收包，帧头非 `SP` 直接抛 `AssertionError`
- `find_host_log()`：在 4 个候选目录（publish / bin-Release / bin-Debug / 根 publish）中**按 mtime 选最新**的 `SpenTabletHost.log`（host 日志写在各自 EXE 同目录）
- `log_contains(log, needle, since_size, timeout)`：只检索「本次标记之后的日志增量」，避免匹配到历史日志造成假通过
- 分 7 段用例：握手 → 截图 → 模式 → 鼠标 → 捏合 → 激光笔 → 备注/平移/复位

**`tests/release_gate.py`**
- 三段式门禁，路径与端口为脚本内常量（`GRADLE`/`JDK`/`SDK`/`DOTNET`/`HOST_EXE`/`PORT=6000`）
- 自己负责 host 生命周期：`taskkill /IM SpenTabletHost.exe /F` → `subprocess.Popen` 启动 → 轮询 socket 探测 6000 端口（最多 15 秒）→ 跑冒烟 → `terminate()`
- `--skip-gradle` / `--skip-smoke` 仅用于本地调试，**正式发布不得使用**

**`tests/TouchProbe/Program.cs`**
- 自定义 `POINTER_INFO` / `TOUCH_INFO` 结构体（Sequential），打印 `Marshal.SizeOf` 与 `SM_REMOTESESSION`、`SM_MAXIMUMTOUCHES`
- 11 组实验：A 基线(hist=1+真实 QPC) / L frameId=0 / M 加 NEW 标志 / N feedback=DEFAULT / O dwTime=tick / P L+M 组合 / Q hover 起始 / R feedback=INDIRECT / S maxCount=256 / T 加压力与方向掩码
- 每组实验打印 `OK` 或 `FAIL err=<GetLastError>`，**没有断言与退出码判定**，属于排障工具而非测试

**`PhoneTablet/app/src/test/.../ProtocolTest.kt`**（8 项）
- 逐字节校验各帧的头部（`SP` + ver + type + u16 长度）与 payload 布局
- `动作码契约` 一项把 MOUSE/HELLO/HL 等码值钉死，防止破坏性协议变更 unnoticed

**`PhoneTablet/app/src/test/.../UpdateCheckerVersionTest.kt`**（7 项）
- 覆盖「更新 / 不更新 / 逐段数值比较（1.10.0 > 1.9.0）/ 当前版本为空 / v 前缀与后缀 / 段数补零 / parseVer 基础行为」
- 依赖 `UpdateChecker.isNewer`、`parseVer` 已在源码中由 `private` 改为 `internal` 以便同 module 测试

**仓库根目录的一次性排查脚本（未纳入 `tests/`，也未纳入 git 版本控制）**
`test_fixes.py`（触摸板微动方向 / S Pen 让位）、`test_protocol.py`（模式互斥）、`gesture_test.py`（滚轮/缩放/中键/前进后退）、`zoom_test.py`（手机端与 PC 端缩放算法一致性）、`pinch_test.py`（adb sendevent 造双指手势）、`repro_crash.py`（捕获→绘制→移框→再绘制的崩溃回放）、`shot.py`（ctypes 抓窗口截图）、`png_stat.py`（纯 Python 解 PNG 比对亮度）。
这些脚本为历史排障产物，**未做断言化改造，不构成回归门禁的一部分**。

---

## 4. 配置项说明

### 4.1 PC 端运行配置 `PcHost/appsettings.json`

由 `ConfigStore.cs` 读写，**始终存放在 EXE 同目录**（双击启动也能找到）。手机端设置面板的改动最终也会落到这里。

| 字段 | 类型 | 默认值 | 含义 |
|---|---|---|---|
| `ListenPort` | int | 6000 | TCP 监听端口（与冒烟测试 `--port` 默认值一致） |
| `Mode` | string | `"Pen"` | 笔注入方式：`Pen`=合成笔（带压感）/ `Mouse`=SendInput 回退 |
| `Debug` | bool | false | 是否弹出调试浮窗显示笔坐标/压感/倾斜 |
| `Aspect` | string | `"Follow"` | 比例模式：`Follow` 跟随手机 / `Keep` 保持等比居中 / `Stretch` 拉伸铺满 |
| `ShowRegion` | bool | true | 是否在桌面画出 S Pen 可操作范围框 |
| `WorkMode` | string | `"Tablet"` | 工作模式：`Tablet` 数位板 / `Mouse` 触摸板（与手机端 MODE 帧互斥） |
| `MouseSpeed` | double | 1.0 | 触摸板模式手指位移增益 |
| `Scale` | double | 1.0 | 操作范围框缩放倍数 |
| `FrameL` / `FrameT` | double | 0.0 | 操作框左上角相对位置（0..1） |
| `MappingX` / `MappingY` | double → int | 0 | 映射区域起点，0 表示整块虚拟屏幕 |
| `MappingW` / `MappingH` | int | 0 | 映射区域尺寸，0 表示整块虚拟屏幕 |
| `PinchMode` | string | `"Touch"` | 捏合缩放方式：`Touch` 优先真实触摸注入 / `Keys` 直接 Ctrl+±。**当前仓库内的 appsettings.json 尚未包含该键**（首次在设置界面保存后写入）；缺失时按默认值 `Touch` 生效 |

> 模板文件 `PcHost/appsettings.json` **已入库**（随源码分发）；运行时实际读取的是**各 EXE 同目录**下的副本
> （`PcHost/bin/.../SpenTabletHost.exe` 或 `publish/SpenTabletHost.exe` 同级目录），`bin/`、`publish/` 已被 `.gitignore` 忽略。
> 仓库根目录下没有该文件。

### 4.2 测试脚本参数

| 脚本 | 参数 | 默认 | 说明 |
|---|---|---|---|
| `protocol_smoke.py` | `--host` | `127.0.0.1` | PC host 地址 |
| | `--port` | `6000` | PC host 端口（须与 `ListenPort` 一致） |
| `release_gate.py` | `--skip-gradle` | 关 | 跳过 Android 单测（仅调试用） |
| | `--skip-smoke` | 关 | 跳过协议冒烟（仅调试用） |

退出码约定：`protocol_smoke.py` / `release_gate.py` 均以 `0` 表示通过、`1` 表示失败，可直接接入 CI 判定。

### 4.3 门禁脚本内的硬编码环境（`release_gate.py` 顶部常量）

| 常量 | 值 | 影响 |
|---|---|---|
| `GRADLE` | `C:\VOLCANO\myws\myEnv\tools\gradle-8.9\bin\gradle.bat` | 换机器需修改 |
| `JDK` / `SDK` | jdk-17.0.13 / android-sdk | 同上 |
| `DOTNET` | `C:\Users\adminlocal\.dotnet\dotnet.exe` | 同上 |
| `HOST_EXE` | `PcHost/bin/Release/net8.0-windows/win-x64/SpenTabletHost.exe` | 门禁启动的 host 位置 |
| `PORT` | 6000 | 门禁探测的端口 |

### 4.4 Android 构建配置（`PhoneTablet/app/build.gradle`）

| 项 | 当前值 | 说明 |
|---|---|---|
| `versionCode` / `versionName` | 7 / `1.3.1` | 版本号，与 GitHub Release 对齐 |
| `minSdk` / `targetSdk` / `compileSdk` | 21 / 34 / 34 | |
| `testImplementation` | `junit:junit:4.13.2` | 单元测试依赖（v1.3.1 新增） |

---

## 5. 测试覆盖

### 5.1 协议级冒烟测试（`tests/protocol_smoke.py`，16 项）

| # | 验证点 | 测试方式 | 断言依据 | 结果（2026-10-28 实测） |
|---|---|---|---|---|
| 1 | HELLO 握手收到 INFO 回包 | 接口测试（TCP） | 回包帧 type == 0x0C | 通过 |
| 2 | INFO 含版本号字段 | 接口测试 | payload 含 `ver=` | 通过 |
| 3 | SHOT_REQ 返回完整截图分片 | 接口测试 | 分片重组长度 ≥ totalLen 且 totalLen > 0（实测 283572B） | 通过 |
| 4 | 截图内容是合法 PNG | 接口测试 | 前 8 字节 == `89 50 4E 47 0D 0A 1A 0A` | 通过 |
| 5 | MODE=触摸板 生效 | 接口测试 + 日志断言 | host.log 出现「切换到【触摸板模式】」 | 通过 |
| 6 | MODE=数位板 生效 | 接口测试 + 日志断言 | host.log 出现「切换到【数位板模式】」 | 通过 |
| 7 | MOUSE 移动被处理 | 接口测试 + 日志断言 | 连发 25 帧 MOVE 后日志出现「[Mouse] 移动」（多帧是为攒够 1 像素残量） | 通过 |
| 8 | MOUSE 左键点击被处理 | 接口测试 + 日志断言 | 日志出现「单击 → 左键」 | 通过 |
| 9 | 捏合缩放帧被处理 | 接口测试 + 日志断言 | 日志出现「[Mouse] 捏合 ×」 | 通过 |
| 10 | 缩放路径生效（触摸注入 **或** Ctrl+/- 回退） | 接口测试 + 日志断言 | 出现「[TouchZoom] 开始捏合」或「Ctrl+ / Ctrl-」 | 通过（本机走回退路径） |
| 11 | 触摸注入路径下接触点自动抬起 | 条件断言 | 仅当 #10 走注入路径时断言「[TouchZoom] 捏合结束（抬起接触点）」 | 本机不适用（本机注入不可用） |
| 11' | 注入不可用时熔断并回退 | 条件断言 | 走回退路径时全文检索「已熔断」 | 通过 |
| 12 | 激光笔开 | 接口测试 + 日志断言 | 日志出现「激光笔已开启」 | 通过 |
| 13 | 激光笔关 | 接口测试 + 日志断言 | 日志出现「激光笔已关闭」 | 通过 |
| 14 | 桌面笔迹开 | 接口测试 + 日志断言 | 日志出现「[Note] action=1」 | 通过 |
| 15 | 桌面笔迹换色 | 接口测试 + 日志断言 | 日志出现「[Note] 笔迹颜色」 | 通过 |
| 16 | 操作范围框平移 | 接口测试 + 日志断言 | 连发 13 帧 PAN 后出现「[Pan] 平移」（host 每 12 帧抽样记一次日志） | 通过 |

> 第 11/11' 为互斥分支，每次运行只会执行其中一条，因此单次运行的断言数为 16。

### 5.2 Android 单元测试（`PhoneTablet/app/src/test/`，15 项）

| # | 测试方法 | 验证点 | 结果 |
|---|---|---|---|
| 1 | `pen 帧 - 头部与 24 字节负载` | PEN 帧头、长度、x/y/pressure/tilt/buttons/reserved 布局 | 通过 |
| 2 | `mouse 帧 - kind 放在 payload0 且小端` | MOUSE 帧 kind/dx/dy 小端编码 | 通过 |
| 3 | `zoom 帧 - scale 与焦点坐标` | ZOOM 帧 scale/focusX/focusY 布局 | 通过 |
| 4 | `hello 帧 - 28 字节 7 个 float` | HELLO 帧长度 28 与 7 个 float 顺序 | 通过 |
| 5 | `note 帧 - action 与 arg` | NOTE 帧 action/arg 布局 | 通过 |
| 6 | `激光笔帧 - 开关与颜色索引` | HL 帧 on/colorIdx 布局 | 通过 |
| 7 | `shot 请求与 noop 与 mode 与 reset 帧` | SHOT_REQ/NOOP/MODE/RESET 帧头与类型 | 通过 |
| 8 | `动作码契约 - 与 README 及 PC 端约定一致` | MOUSE_* 与 TYPE_HELLO/MOUSE/HL 码值钉死 | 通过 |
| 9 | `更新版本大于当前版本` | 1.3.1 > 1.3.0、1.4.0 > 1.3.9、2.0.0 > 1.9.9 | 通过 |
| 10 | `同版本或更旧不更新` | 相等与更旧均判为不更新 | 通过 |
| 11 | `逐段比较不按字符串` | 1.10.0 > 1.9.0（字符串比较会误判） | 通过 |
| 12 | `当前版本为空视为可更新` | 当前版本为空/空白 | 通过 |
| 13 | `带 v 前缀与后缀的版本号也能解析` | `v1.3.1`、`1.3.1-beta` | 通过 |
| 14 | `段数不足补零` | `1.4` 视为 1.4.0 | 通过 |
| 15 | `parseVer 基础行为` | 解析结果与非法输入兜底 | 通过 |

### 5.3 诊断探针（`tests/TouchProbe/Program.cs`）—— 打印结果，非断言

| 项 | 观测到的结果（本机 Win11 Build 26300） | 结论 |
|---|---|---|
| `Marshal.SizeOf(POINTER_INFO/TOUCH_INFO)` | 96 / 144 字节 | 与 SDK 一致 |
| `SM_REMOTESESSION` / `SM_MAXIMUMTOUCHES` | 0（非 RDP）/ 2（有触摸硬件） | 排除远程会话与无触摸硬件两种可能 |
| A/L/M/O/P/Q/R/S/T 共 10 组注入实验 | 全部 `FAIL err=87` | 排除参数组合类原因 |
| N 组（`TOUCH_FEEDBACK_DEFAULT`） | 连 `InitializeTouchInjection` 都 `FAIL err=87` | 佐证为本机系统级限制，非调用参数问题 |

该探针的结论已固化为产品行为：`PcHost/MouseControl.cs` 连续注入失败 3 次即**熔断**并永久回退 Ctrl+/-。

### 5.4 手动验证项（需真机 + 人工观察，无自动化脚本）

| 验证点 | 手动步骤 | 结果 |
|---|---|---|
| 数位板 ⇄ 触摸板来回切换后鼠标可控 | 手机端切换模式 → 手指在板上移动 → 观察 PC 光标 | **待验证**（需真机；PC 侧状态复位已由 `resetGestures` / `EndTouchZoomIfActive` / `ReleaseAll` 实现） |
| 捕获桌面（截图设为画布背景） | 手机端点「捕获」→ 桌面范围框截图回传 → 手机画布显示 | **待验证**（PC 端链路已由冒烟 #3/#4 覆盖；手机端重组逻辑无自动化断言） |
| 悬浮按钮转屏不越界 | 手机横竖屏切换 → 观察浮动面板位置 | **待验证**（代码含 `onLayout` 钳制与转屏按比例还原，无 UI 自动化） |
| 激光笔光斑造型与换色 | 触摸板模式开启激光笔 → 观察指针形状 → 长按换色 | **待验证**（仅验证了开/关指令链路，造型需肉眼） |
| 触摸板手势（滚动/三指/四指/动作槽） | 手指在板上做各手势 | **待验证**（脚本化手势需 adb evvent，仓库仅有未入库的 `pinch_test.py` 辅助脚本） |
| 检查更新 → 下载 → 安装 APK | 触发「检查更新」→ 系统安装弹窗 | **待验证**（版本比较逻辑已单测覆盖 #9~#15；下载与安装流程无自动化） |

---

## 6. 常见问题（FAQ）

**Q1：跑冒烟测试报 `ConnectionRefusedError: [WinError 10061]`**
host 没起来或端口不是 6000。先确认进程：`tasklist | findstr SpenTabletHost`；启动前用 `taskkill /IM SpenTabletHost.exe /F` 清掉占用旧实例的残留进程。

**Q2：启动 `bin/Release/.../SpenTabletHost.exe` 报 “You must install or update .NET to run this application”**
本机 `C:\Program Files\dotnet` 没有 .NET 8 运行时。设置 `DOTNET_ROOT=C:\Users\adminlocal\.dotnet` 后再启动，或改用 `publish` 目录的自包含单文件 exe（无需运行时）。

**Q3：冒烟测试跑完一堆「日志确认」全 FAIL，但协议回包正常**
host 日志写在**它自己 EXE 所在目录**。若你从 `bin/Release/.../` 启动 host，而脚本此前指向 `publish` 的日志，就会断言不到。`find_host_log()` 已按「mtime 最新的日志」自动选择，但同一台机器上**同时存在两个 host 实例**时仍会错判——确保只有一个 host 在跑。

**Q4：`publish` 目录里只有 exe 没有 dll，是不是发布坏了？**
不是。`PcHost/SpenTabletHost.csproj` 启用了 `PublishSingleFile` + `SelfContained`，托管程序集被打包进 exe，目录里看不到 `SpenTabletHost.dll` 属正常。**反而不要给 `dotnet publish` 加 `--self-contained false`**——那会退化成依赖框架模式，产物在没装 .NET 8 的机器上直接打不开。

**Q5：NOTE 开/关、PAN 平移的断言偶尔失败**
host 对这两类日志做了抽样节流（NOTE 原为每 10 条、PAN 每 12 条记 1 条）。当前版本已把 NOTE 开关改为恒记日志；PAN 在冒烟里连发 13 帧以保证命中节流窗口。手工只发 1 帧时看不到日志属正常。

**Q6：捏合缩放走的是「Ctrl+ / Ctrl-」而不是「触摸板真实缩放」，是 bug 吗**
不是。本机 Windows 11 Build 26300 的触摸注入被系统限制（`InjectTouchInput` 恒返回 err=87，见 §5.3），程序按设计熔断并回退 Ctrl+/-。若日志里连「已熔断」都没有，说明跑的是旧版本二进制。

**Q7：Gradle 报找不到 junit 依赖**
`--offline` 需要本地缓存已有 `junit:junit:4.13.2`。首次运行去掉 `--offline` 联网下载，之后可继续离线。

**Q8：Gradle 报 SDK / JDK 找不到**
必须同时提供 `JAVA_HOME`（jdk-17.0.13）与 `ANDROID_HOME`（android-sdk），且 `local.properties` 未被提交，换机器需重新生成。

**Q9：手机连不上 PC**
确认 PC 端在监听（托盘图标显示连接状态）；同一局域网；USB 调试场景执行 `adb reverse tcp:6000 tcp:6000`；防火墙放行 6000 端口。

**Q10：能不能在没开 PC host 的情况下跑冒烟测试**
不能。冒烟测试是**真实进程 + 真实日志断言**的黑盒测试，host 必须先运行。直接用 `python tests/release_gate.py` 最省事，它会自己管理 host 生命周期。

**Q11：测试会不会真的动我的鼠标、点我的窗口**
会。冒烟测试发送的是真实鼠标动作帧（移动、左键单击），PC 上会真的移动光标并点击当前前台窗口；截图请求会真的截取当前范围框。**运行前请先关闭正在编辑的重要窗口。**

**Q12：手机上安装的 APK 提示签名冲突**
发布用的 APK 是 `assembleDebug` 产物（release 构建未配置签名，会产出 unsigned apk）。debug 签名与历史版本一致可覆盖安装；若换过机器的 debug keystore 则需先卸载旧版。

---

## 7. 后续计划

### 7.1 协议层尚未断言的帧与动作（当前 `protocol_smoke.py` 覆盖缺口）

| 缺口 | 说明 |
|---|---|
| `TYPE_ZOOM`（0x09） | 缩放操作范围框的帧完全未发；`TYPE_HELLO` 只发了 1 次，未断言 PC 回包里的 `surfaceW/surfaceH` 处理结果 |
| MOUSE 右键 / 滚轮 / 中键 / 后退 / 前进 / 音量± / 静音 / 任务切换 | 动作码已在 Kotlin 单测中钉死，但**协议侧未验证 PC 是否真的执行** |
| NOTE 撤销（2）/ 清空（3） | 只断言了「开」与「换色」 |
| INFO 字段完整性 | 只断言含 `ver=`，未断言 `name=` / `ips=` |
| SHOT 分片异常路径 | 未覆盖丢片、乱序、分片数超限 |
| 断线重连 / 多客户端并发 | 完全未覆盖（`TcpPenServer` 的重连与 mDNS 发现路径无自动化） |

### 7.2 端到端与设备侧

- 真机端到端脚本（adb 模拟触摸板手势 + 截图比对）——仓库内仅有未入库的 `pinch_test.py`/`gesture_test.py` 等一次性脚本，尚未断言化
- 手机端截图重组逻辑（`NetClient.kt` 的 SHOT 分片重组）无单元测试，PC 侧已覆盖、手机侧未覆盖
- 手机端 `UpdateChecker` 的网络下载与 `PackageInstaller` 安装流程无自动化（仅版本比较已单测）

### 7.3 工程质量

- **无 CI**：仓库内没有 `.github/workflows`，门禁脚本目前只能本地手动跑；建议接入 GitHub Actions（Windows runner + JDK 17 + .NET 8）
- **无覆盖率统计**：Kotlin 侧可接 JaCoCo，PC 侧未引入任何覆盖率工具
- **门禁路径硬编码**：`release_gate.py` 的工具链路径写死在本机，换机器需改代码；建议改为读环境变量 + 目录探测
- **协议一致性防漂移**：手机端 Kotlin 与 Python 冒烟脚本各自硬编码了一组帧类型/动作码；`ProtocolTest` 的「动作码契约」目前只锁手机端一侧，建议再对 Python 脚本做同样的契约断言
- **日志节流影响断言稳定性**：NOTE/PAN 的抽样日志让测试必须「多发几帧」才能命中，后续可考虑给测试增加专用的详细日志开关

### 7.4 明确暂无的部分

- 无 Android UI 自动化框架与用例（Espresso / UIAutomator / Robolectric 均未引入）
- 无性能、压力、并发、长稳测试
- 无跨版本兼容矩阵（仅对当前协议版本 1 做了断言）
