# SpenTablet —— 把旧三星 Note + S Pen 变成 PC 数位板

把吃灰的三星 Note 系列手机（带 S Pen）改造成 PC 的数位绘图板：在手机上用 S Pen 书写/绘画，
PC 端实时收到**带压感、带倾斜**的笔信号，可在 Photoshop / Krita / Clip Studio 等软件里直接作画。

- 手机端（Android/Kotlin）：全屏画布捕获 S Pen 的 `MotionEvent`，提取坐标、压感、倾斜、按键，经 TCP 以二进制帧发给 PC。
- PC 端（C# / .NET 8）：TCP 接收 → 坐标映射到屏幕区域 → 调用 **Win32 合成笔注入**（`CreateSyntheticPointerDevice` + `InjectSyntheticPointerInput`，`PT_PEN`）产生真实笔指针。**无需驱动、无需鼠标模拟、压感原生识别。**
- 连接：默认 **USB + ADB 转发**（最低延迟、免 WiFi），也支持 USB 网络共享 / WiFi（填 PC 的 IP 即可）。

---

## 架构

```
 三星 Note (S Pen)                PC (Windows 10 1809+)
 ┌──────────────┐   TCP 二进制帧   ┌─────────────────────────┐
 │ PenCanvasView│ ───────────────► │ TcpPenServer            │
 │ 捕获压感/倾斜 │   127.0.0.1:6000│  解析 → 映射坐标         │
 │  + NetClient │  (adb reverse)   │        │                │
 └──────────────┘                  │        ▼                │
                                    │  PenInjector           │
                                    │  (合成 PT_PEN 指针)     │
                                    │        ▼                │
                                    │  Photoshop/Krita/...    │
                                    └─────────────────────────┘
```

通信协议（小端）：`magic('S','P') + version(1) + type(1) + length(2) + payload(24)`
笔事件 payload：`x(float) y(float) pressure(float) tiltX(float) tiltY(float) buttons(uint16) reserved(uint16)`，
坐标均为 0..1 归一化，压感 0..1，倾斜角度 -90..90。

---

## 目录结构

```
SpenTablet/
├── PhoneTablet/            # Android 端（Android Studio 打开此目录）
│   ├── build.gradle / settings.gradle / gradle.properties
│   └── app/src/main/
│       ├── AndroidManifest.xml
│       ├── java/com/volcano/spentablet/
│       │   ├── Protocol.kt        # 二进制帧编解码
│       │   ├── NetClient.kt       # TCP 长连接 + 自动重连
│       │   ├── PenCanvasView.kt   # S Pen 捕获 + 手机屏画轨迹
│       │   └── MainActivity.kt    # 界面/连接/设置
│       └── res/values/strings.xml
└── PcHost/                 # PC 端（.NET 8，纯 Win32 P/Invoke，无 NuGet 依赖）
    ├── SpenTabletHost.csproj
    ├── Program.cs
    ├── PenInjector.cs      # 合成笔注入 + 鼠标回退
    ├── TcpPenServer.cs     # TCP 帧解析
    ├── MappingConfig.cs    # 坐标映射/虚拟屏幕
    ├── DebugOverlay.cs     # 调试浮窗(无绘画软件时验证笔数据)
    ├── appsettings.json    # 端口/模式/映射区域/Debug 开关
    └── publish.cmd         # 发布单文件 EXE
```

---

## 前置条件

- **手机**：Android 5.0+ 的三星 Note（S Pen）。需开启「开发者选项 → USB 调试」。
- **PC**：Windows 10 版本 1809 及以上（合成笔注入所需）。
  - 方式 A（推荐）：装好 **Android SDK 平台工具**（`adb`），用于在 USB 下做端口转发。
  - 方式 B：直接装 **.NET 8 运行时**（或运行自包含 EXE，无需运行时）。

---

## 快速开始

### 1) 构建并运行 PC 端

```bat
cd PcHost
rem 方式一：本机已装 .NET 8 运行时，直接运行
dotnet run -c Release

rem 方式二：发布成单文件 EXE（含运行时，双击即用）
publish.cmd
rem 产物在 PcHost\publish\SpenTabletHost.exe
```

PC 端启动后监听 `6000` 端口，并提示你执行 `adb reverse`。

### 2) 构建并安装手机端

用 **Android Studio** 打开 `SpenTablet/PhoneTablet` 目录，连接手机，点 Run（或生成 APK 安装）。
命令行亦可：

```bat
cd PhoneTablet
gradlew.bat assembleDebug        # 需要先有 gradle wrapper；没有就直接用 Android Studio
```

> 没有 `gradlew`？在 Android Studio 里打开工程会自动生成；或用 `gradle wrapper` 生成。

### 3) 连接（三种方式）

**方式 A · USB + ADB 转发（默认，最稳）**
```bat
adb reverse tcp:6000 tcp:6000
```
手机端保持默认 `127.0.0.1:6000` 即可（转发到 PC 本地）。

**方式 B · USB 网络共享**
手机开「设置 → 连接 → 移动网络共享 → USB 网络共享」，PC 会多一块网卡。
查 PC 在该网卡下的网关 IP（通常是 `192.168.42.129` 或 `192.168.43.1`），手机端设置里填这个 IP，端口 6000。

**方式 C · WiFi**
手机和 PC 连同一 WiFi，手机端填 PC 的局域网 IP（如 `192.168.1.50`），端口 6000。

手机端：打开 App → 点顶部状态条 → 输入 PC IP → 「连接」。状态变「已连接」后即可在 PC 画图软件里用 S Pen 作画。

---

## 校准 / 配置（PC 端 `appsettings.json`）

```json
{
  "ListenPort": 6000,
  "Mode": "Pen",          // "Pen"=合成笔(带压感) / "Mouse"=鼠标回退(无压感)
  "Debug": false,         // true=弹出调试浮窗(实时显示坐标/压感/倾斜/按键)，不装绘画软件也能验证
  "MappingX": 0, "MappingY": 0, "MappingW": 0, "MappingH": 0
}
```
- `Debug: true`：**最省事的验证方式**。双击 EXE 后右上角弹出一个常驻浮窗，实时显示 S Pen 的坐标、压感(0~1)、倾斜(tiltX/tiltY)和按键状态。只要浮窗里的数字随你在手机上运笔而变化，就证明整条链路（手机捕获 → TCP → PC 注入）是通的，**完全不需要安装 Photoshop / Krita**。验证完把 `Debug` 改回 `false` 再重启即可。
- 四个 Mapping 全为 `0`：笔迹铺满整块虚拟屏幕（多显示器合并范围）。
- 想只映射到某块显示器/某个窗口区域：填对应像素坐标（基于虚拟屏幕左上角）。
  例如只映射主显示器（假设 1920×1080，位于虚拟屏幕 0,0）：`"MappingX":0,"MappingY":0,"MappingW":1920,"MappingH":1080`。
- 改完重启 PC 端生效。

---

## 在绘画软件里验证压感

> **没有 Photoshop 也没关系**，两条路任选：
> - **零安装验证**：把 `appsettings.json` 的 `"Debug": true`，双击 EXE，看右上角浮窗数字是否随 S Pen 跳动（见上方 Debug 说明）。
> - **免费专业软件**：装 **Krita**（开源免费，官网 krita.org）即可获得完整压感/倾斜体验，无需 Photoshop。

1. PC 打开 Krita / Photoshop，新建画布，选「笔刷」工具。
2. 手机上用 S Pen 用力/轻画，观察笔迹粗细变化 → 说明压感已生效。
3. Krita：菜单「设置 → 平板电脑设置」若能看到「Windows Pointer Input」，即为正确通道。
4. 倾斜：把笔倾斜，看笔锋变化（需软件支持倾斜笔刷）。

---

## 排错

| 现象 | 排查 |
|------|------|
| 手机一直「未连接」 | PC 端是否已启动；IP/端口是否正确；防火墙是否放行 6000；USB 方式是否忘了 `adb reverse` |
| 手机显示已连接、PC 却收不到任何数据（浮窗一直"等待笔数据"） | **adb server 处于半坏状态**（连接能建立但中继不转发数据）。执行 `adb kill-server` 后重新 `adb reverse tcp:6000 tcp:6000`，并重启手机端 App。本机装有多个 adb（如刷机工具 PhoenixSuit 自带老版 adb）时尤其容易发生——版本不匹配的客户端会让 adb server 反复自杀重启 |
| `adb install -r` 重装后突然连不上 | **重装 APK 会清空 adb reverse 规则**，重跑一次 `adb reverse tcp:6000 tcp:6000` 即可 |
| PC 端报错 `CreateSyntheticPointerDevice 失败` | Windows 版本低于 1809；或其进程无桌面会话。程序会自动回退到鼠标模式 |
| 有移动但无压感 | 确认 `Mode` 为 `Pen`；软件是否支持 Windows Ink/指针输入；个别软件需在设置里开启笔压 |
| 笔迹位置偏 | 检查 `Mapping` 与多显示器布局是否匹配；手机横竖屏方向 |
| 延迟高 | 改用 USB（方式 A/B）而非 WiFi；关闭手机端其它网络负载 |

---

## 已知限制 / 后续可加

- 当前为单笔（单指 S Pen），未做多指/触摸作为第二输入。
- 手机端目前是「所见即所得」白板反馈；可改为仅传输、手机屏熄屏省电。
- 可加：快捷键（S Pen 按键映射为 Ctrl/Alt/右键）、配置文件 UI、蓝牙低延迟模式、笔迹平滑滤波。
- 若你的绘画软件不吃合成笔（极少数），可在 `appsettings.json` 切 `"Mode":"Mouse"` 先用起来。

---

## 参考

- `CreateSyntheticPointerDevice` / `InjectSyntheticPointerInput` / `POINTER_PEN_INFO` —— Microsoft Learn (winuser.h)
- Android `MotionEvent`：`TOOL_TYPE_STYLUS`、`AXIS_TILT`、`AXIS_ORIENTATION`、`BUTTON_STYLUS_*`
