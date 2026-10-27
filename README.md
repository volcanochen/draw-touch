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

帧类型：

| type | 含义 | payload |
|------|------|---------|
| 0x01~0x04 | 笔 按下/移动/抬起/悬停 | x,y,pressure,tiltX,tiltY,buttons |
| 0x05 | HELLO 握手 | surfaceW,surfaceH,aspectMode,workMode |
| 0x06 | PAN 平移（数位板模式拖框） | dx,dy |
| 0x07 | MOUSE 鼠标动作 | kind,dx,dy（0=移动 3=左键 4=右键 5=滚动 6=捏合缩放 7=中键 8=后退 9=前进） |
| 0x08 | MODE 模式切换 | mode（0=数位板 1=鼠标） |
| 0x09 | ZOOM 双指捏合缩放 | scale,focalX,focalY |
| 0x0A | RESET 操作框复位 | — |
| 0x0B | STATUS 状态查询（调试） | — |

HELLO 帧的 payload 是 28 字节（7 个 float）：
`surfaceW, surfaceH, aspectMode, workMode, frameL, frameT, frameScale`
—— 最后三个是手机端当前的**桌面范围框状态**，PC 据此在重连后还原出同形状同位置的框。

### 屏幕方向（横屏支持）

- App **默认跟随系统**旋转（`screenOrientation="fullUser"`）：你把手机横过来就变横屏。
  注意 Android 的语义——如果系统里**锁了自动旋转**，App 也不会跟着转（尊重用户习惯）。
- 设置面板里有一个「**屏幕方向**」按钮，三态循环：
  **跟随系统 → 强制横屏 → 强制竖屏**。
  「强制横/竖屏」不依赖传感器也不看系统的旋转锁，点了立刻生效。
  （写代码画的画如果总是正着走，切到「强制横屏」最省事。）
- 旋转时 **Activity 不会重建**（manifest 声明了 `configChanges`），
  所以 **TCP 连接不会断**；画布尺寸变化会触发 `onSizeChanged` →
  立刻重发 HELLO → PC 端按新的书写区宽高比重算桌面映射区域。
- 横屏时上下控制栏会自动压扁（状态/信息各 1 行、调试栏 1 行），
  把竖直空间尽量让给书写区（实测 1494×548，宽高比 2.726）。
- 设置详情在横屏时套了 `ScrollView`，否则 720 高的屏幕放不下全部选项。

### 鼠标模式 = 笔记本触摸板（多点触控）

鼠标模式下，手指被当成**触摸板**，支持完整的多点手势：

| 手势 | 动作 | 底层实现 |
|------|------|----------|
| 单指滑动 | 移动光标 | 相对移动 + 绝对定位 |
| 单指点击 | 左键 | — |
| 单指双击 | 右键 | — |
| **双指上下滑** | **平滑纵向滚动** | 高分辨率滚轮 `MOUSEEVENTF_WHEEL` |
| **双指左右滑** | **平滑横向滚动** | 高分辨率滚轮 `MOUSEEVENTF_HWHEEL` |
| **双指捏合** | **缩放** | 高分辨率 Ctrl+滚轮（PS/Krita 里就是缩放画布） |
| **双指点击** | **右键** | 同 Windows 精密触摸板 |
| **三指点击** | **中键** | 浏览器里=新标签打开链接，绘图软件里常用来平移画布 |
| **三指左右滑** | **后退 / 前进** | `VK_BROWSER_BACK` / `VK_BROWSER_FORWARD` |

实现要点：
- 手指事件是**整体处理**的（要拿到所有手指的坐标），所以在 `handleEvent` 里
  先把手指从逐指循环里摘出来，交给 `handleTrackpad`；S Pen 仍走原来的逐指路径。
- **只有单指才能移动光标**。多指手势进行中（≥2 根手指）时：
  - 忽略 S Pen 的所有鼠标事件（笔在屏幕上碰一下光标就会跑，非常干扰手势）；
  - 如果笔正按着左键，先把左键松开。
- 抬起一根手指时，那根手指在事件里**仍占一个位置**，必须排除，
  否则质心/跨度会算错，表现为「松手后光标突然跳一大段」。
- **滚动是像素级平滑滚动，不是鼠标滚轮的一格一格**：
  Windows 对「不是 120 倍数」的滚轮增量会按高分辨率设备处理（和真实触摸板的 pan 一样），
  应用会按像素平滑滚动。所以这里发的增量永远不是 120 的倍数。
- **捏合 vs 滚动的区分**：双指跨度的相对变化 > 12% 判定为捏合，否则判定为滑动滚动。
  这样双指滑动时手抖不会误触发缩放。
- 从多指回到单指时会重置单指基准点，**光标不会突然跳一下**。
- 滚动量：手指划过整个书写区高度 ≈ 360 像素；捏合放大 100% ≈ 220 像素的 Ctrl+滚轮。
- 手指上滑 = 滚轮向上（内容跟着手指走）。若觉得方向反了，改 `TcpPenServer` 里的负号即可。

### 操作范围框：桌面上的框 = 手机上的框

桌面上画一个橙色边框，标出 S Pen 实际能覆盖的桌面区域；手机上也画一个框，
**两者的宽高比始终一致**（桌面框的高由宽按手机书写区的比例推出来，不会各自取整变形）。

- **手机端的框撑满整个书写区**（屏幕上带蓝框的那一块），保证用户的可操作范围始终最大；
- **双指捏合**缩放的是**桌面上的那个框**：捏得越紧，桌面框越小，笔迹落点越集中，
  可以精准地只写进桌面上那一小块区域；捏合以手指中心为锚点，焦点处的内容不会跑位；
- **单指滑动**移动桌面框（手机端框不动，桌面框在桌面上移动）；
- 设置面板里有「复位操作框」和「模拟一次捏合」两个按钮，后者走的是和真实捏合
  **完全相同**的代码路径，方便单指也能验证链路。

> 手机端的书写区是**屏幕中间那一块**，不包含上下的控制栏（状态栏 / 调试栏）。
> 设置面板是**浮在书写区之上**的，展开收起不会改变书写区尺寸 —— 否则手机宽高比一变，
> 桌面上的框形状就会跟着突变（实测会被压成横条）。

### 两种工作模式（手指的用途互斥）

手机上点顶部状态栏展开设置即可切换，PC 端托盘右键菜单也能切：

| | 数位板模式（默认） | 鼠标模式 |
|---|---|---|
| 桌面范围框 | **显示** | **取消** |
| S Pen | 在框内作画（带压感/倾斜） | 绝对定位的鼠标笔，按下=左键拖动 |
| 手指滑动 | 移动桌面上的操作范围框 | 移动光标（当触摸板） |
| 手指单击 | — | 鼠标左键 |
| 手指双击 | — | 鼠标右键 |

关键点：两种模式**互斥**。手指在数位板模式下只发 PAN 帧、在鼠标模式下只发 MOUSE 帧，
不会出现「既拖框又控鼠标」。PC 端还留了一道保险：鼠标模式下即使收到 PAN 帧也会忽略并记 WARN。

单击会在 280ms 的双击窗口过后才发出（否则双击会先误触发一次左键）。

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
│       │   ├── MdnsDiscovery.kt   # mDNS/NSD 自动发现 PC
│       │   ├── NetworkScan.kt     # 兜底：扫描网段找 PC
│       │   ├── PenCanvasView.kt   # S Pen 捕获 + 手机屏画轨迹
│       │   └── MainActivity.kt    # 界面/连接/设置
│       └── res/values/strings.xml
└── PcHost/                 # PC 端（.NET 8，Win32 P/Invoke + mDNS 广播库）
    ├── SpenTabletHost.csproj
    ├── Program.cs          # 托盘常驻入口
    ├── TrayApp.cs          # 系统托盘 + 右键菜单
    ├── ConfigForm.cs       # 设置窗口（连接/输入/映射/桌面显示 四组 + 实时诊断框）
    ├── PenInjector.cs      # 合成笔注入 + 鼠标回退
    ├── MouseControl.cs     # 鼠标模式：光标移动/左键/右键
    ├── TcpPenServer.cs     # TCP 帧解析
    ├── MdnsAdvertiser.cs   # mDNS 广播 _spentablet._tcp（手机端自动发现）
    ├── MappingConfig.cs    # 坐标映射/等比/范围框/工作模式
    ├── RegionFrame.cs      # 桌面操作范围框（可穿透点击）
    ├── NetInfo.cs          # 网卡枚举 / USB网络共享网卡识别 / 防火墙规则
    ├── DebugOverlay.cs     # 调试浮窗(无绘画软件时验证笔数据)
    ├── appsettings.json    # 端口/模式/映射区域/Debug 开关
    └── publish.cmd         # 发布单文件 EXE
```

根目录的 `test_protocol.py` 是 PC 端协议自测脚本：直连 127.0.0.1:6000 模拟手机发帧，
可验证模式切换、范围框显隐、光标位移与左右键，排错时很有用。

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

### 3) 连接（推荐：mDNS 自动发现，零配置）

**方式 0 · mDNS 自动发现（推荐，不用填 IP）**

PC 端启动后会自动在局域网广播 `_spentablet._tcp` 服务，手机端用 Android 原生
NSD（mDNS/DNS-SD）自动搜索 —— 和 Weylus、ZeroLink 等成熟方案是同一套机制。

1. PC 上启动 `SpenTabletHost`（托盘图标出现即可）。
2. 手机上打开 App，**什么都不用填**，它会自动发现 PC 并连上；
   状态条变成「已连接 …」即成功。

多点几台 PC 时会弹出列表让你选；也可以在设置面板里点「**发现 PC（mDNS 自动，推荐）**」手动触发。

实现要点：
- PC 端 `PcHost/MdnsAdvertiser.cs`（基于 `Makaretu.Dns.Multicast`），只广告**真实网卡**地址 ——
  默认行为会把 VPN/隧道虚拟网卡也报出去，手机解析到虚拟地址就永远连不上。
- 全部候选地址写进 mDNS 的 **TXT 属性 `ips`**（如 `ips=10.136.80.209,192.168.50.73`），
  手机端逐个 TCP 探测，第一个能连上的就是真正走通的那条路
  （PC 可能同时开着 USB 共享和 WiFi，手机只能走其中一条）。
- 前提：**手机与 PC 必须在同一广播域**（同一路由器 / 手机开热点 / USB 网络共享）。
  某些企业 WiFi 禁止组播，mDNS 会发现不到，这时退回下面的方式 B / C。

**方式 A · USB + ADB 转发（延迟最低）**
```bat
adb reverse tcp:6000 tcp:6000
```
手机端保持默认 `127.0.0.1:6000` 即可（转发到 PC 本地）。

**方式 B · USB 网络共享**
手机开「设置 → 连接 → 移动网络共享 → USB 网络共享」，PC 会多出一张 RNDIS 网卡。

1. **先放行防火墙**（这一步最容易被漏掉）：PC 端右下角托盘图标 → 右键 →「配置…」→「允许防火墙」，
   弹出 UAC 时点「是」。不放开行的话，Windows 会**静默丢弃**入站连接，
   手机端表现为「连接超时」而不是「连接被拒绝」。
2. **抄 IP**：同一个配置窗口顶部「手机端要填的 PC IP」就是答案，
   下面还会写明它来自哪张网卡（本机实测：`以太网 2 / SAMSUNG Mobile USB Remote NDIS Network Device #2` → `10.136.80.209`）。
   也可以点「复制 IP」直接复制。
3. 手机端填这个 IP + 端口 6000 → 「连接」。
   懒得找 IP 的话，手机端直接点「**发现 PC（mDNS 自动）**」（自动）；或点「**扫描网段找 PC（备用）**」，
   它会扫本机各网卡的同网段、找出开着该端口的电脑并自动填好。

> 注意：各家手机的 USB 共享网段不一样（可能是 `10.136.80.x`、`192.168.42.x`、`192.168.43.x`……），
> 所以别照抄网上的固定 IP，以 PC 端显示的为准。

**方式 C · WiFi**
手机和 PC 连同一 WiFi，手机端填 PC 的局域网 IP（如 `192.168.1.50`），端口 6000。
注意别填成 VPN/虚拟网卡的 IP —— 配置窗口的诊断框会把每块网卡列出来，虚拟网卡标了「手机端不能用」。

手机端：打开 App → 点顶部状态条 → 输入 PC IP → 「连接」。状态变「已连接」后即可在 PC 画图软件里用 S Pen 作画。
连不上时，App 顶部会直接给出**可操作的排查建议**（区分「被拒绝」和「超时」两种情况）。

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
| **手机显示已连接、PC 却只收到悬停、收不到落笔**（最有迷惑性的一个） | 不是 S Pen 设置问题，也不是 `TOOL_TYPE_STYLUS` 过滤（实测 SM-S9080 上落笔事件会正常送达 `onTouchEvent`）。真凶是 **`NetClient` 在 UI 主线程上直接做阻塞式 socket 写**：笔事件 60~120Hz 突发时写入一抛异常，`catch` 就把 `out` 置 null 并关 socket，且**异常被静默吞掉**；之后所有帧走 `SEND skipped (out=null)` 被丢弃。悬停帧多，总有几帧撞上有效窗口；一次落笔只有几百毫秒，撞上断连窗口就整笔消失。现已改为 `send()` 只入队、独立写线程做 IO、异常全部打日志。自查：`adb logcat -s SpenTablet:D` 若刷出大量 `SEND skipped` 即为该问题 |
| **压感很弱（画线粗细几乎没变化）** | 内核 `ABS_PRESSURE` 声明量程 0~4095，但 S Pen 实际书写只到 ~500（`getPressure()` 仅 ~0.13）。App 内置**压感增益**（默认 3.0），点顶部状态条展开设置，用「增益 ±」调整，会记住在 prefs。可用 `adb shell getevent -l /dev/input/event14` 看 `ABS_PRESSURE` 原始值核对 |
| **USB 调试授权框死活不弹** | PC 上 `~/.android/adbkey` 与手机记录的授权状态不匹配。备份后删除 `adbkey` / `adbkey.pub` 让 adb 重建密钥，手机会立刻弹窗。操作时**保持手机亮屏解锁**，息屏时三星会收起授权框 |
| **adb daemon 活不过一条命令，`reverse` 规则总丢** | adb server 会随命令结束被回收，需起常驻后台任务保活（例如 `adb logcat > keepalive.log`）。另外**两个 adb 命令并发启动会撞车**（`could not read ok from ADB Server`），必须串行 |
| **PC 端收到数据，但屏幕上笔迹没反应 / 完全没有压感** | 两个已修复的坑，若自行改动 `PenInjector.cs` 请务必保持：① `POINTER_INFO` 的 `sizeof` 是 **96**（x64 对齐 8，字段排到 92 后还有 4 字节尾部填充），写成 92 会让后面所有 pen 字段错位 4 字节，**Windows 会直接拒绝整个注入**；② 主机进程**必须 DPI 感知**（`SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`），否则 `GetSystemMetrics` 返回虚拟化尺寸，200% 缩放下坐标会差整整 2 倍 |
| `adb install -r` 报 `INSTALL_FAILED_UPDATE_INCOMPATIBLE` | 旧包是别的机器签的名，先 `adb uninstall com.volcano.spentablet` 再装 |
| 手机显示已连接、PC 却收不到任何数据（浮窗一直"等待笔数据"） | **adb server 处于半坏状态**（连接能建立但中继不转发数据）。执行 `adb kill-server` 后重新 `adb reverse tcp:6000 tcp:6000`，并重启手机端 App。本机装有多个 adb（如刷机工具 PhoenixSuit 自带老版 adb）时尤其容易发生——版本不匹配的客户端会让 adb server 反复自杀重启 |
| `adb install -r` 重装后突然连不上 | **重装 APK 会清空 adb reverse 规则**，重跑一次 `adb reverse tcp:6000 tcp:6000` 即可 |
| PC 端报错 `CreateSyntheticPointerDevice 失败` | Windows 版本低于 1809；或其进程无桌面会话。程序会自动回退到鼠标模式 |
| 有移动但无压感 | 确认 `Mode` 为 `Pen`；软件是否支持 Windows Ink/指针输入；个别软件需在设置里开启笔压 |
| 笔迹位置偏 | 检查 `Mapping` 与多显示器布局是否匹配；手机横竖屏方向 |
| 延迟高 | 改用 USB（方式 A/B）而非 WiFi；关闭手机端其它网络负载 |
| **USB 网络共享连不上（方式 B）** | 99% 是 **Windows 防火墙**把入站连接静默丢了（表现为「连接超时」而不是「拒绝」）。PC 端托盘 → 配置 →「允许防火墙」并同意 UAC。另外确认手机端填的是 **PC 上那张 RNDIS 网卡**的 IP（配置窗口顶部会直接显示），不是 WiFi IP、更不是 VPN/虚拟网卡 IP |
| 手机端提示「连接超时」 | 说明请求发出去了但没人应答：① 先查防火墙；② 查 IP 填对没；③ 手机端点「自动查找 PC」让它自己扫 |
| 手机端提示「连接被拒绝」 | 说明 PC 上没有人在监听：PC 端程序没启动，或端口被改了（两边端口要对上） |
| PC 配置窗口内容被裁切 | 现在窗口尺寸由程序按**物理像素**控制并随 DPI 自适应（175% 屏上是 1310×1043 物理像素）。若仍异常，删掉 `publish/appsettings.json` 里多余的键再启动 |
| **找不到设置面板 / 点了没反应** | 设置面板的容器必须常驻可见，只有里面的正文可折叠。若把整个 `panel` 设为 `GONE`，「点标题展开」这个开关自己也被藏起来了，用户永远打不开 |
| 手机上手指滑动，PC 上框和光标一起动 | 不应发生。两种模式互斥：手机端按 `workMode` 二选一发帧；PC 端鼠标模式下收到 PAN 还会忽略并打 WARN。检查手机状态栏显示的当前模式 |
| 单击有延迟感 | 正常。单击要等 280ms 的双击窗口过去才发，否则双击会先误触发一次左键 |
| 旋转手机后桌面框形状变了 | 这是**正确行为**：书写区宽高比变了，PC 会按新比例重算等比映射（横屏 1494×548 → 桌面 3762×1380，比例一致不变形）。若桌面框跑出屏幕，把「复位操作框」点一下即可回到基准位置 |
| `adb devices` 突然列出空设备 | 有残留的 adb 进程（如刷机工具自带的老版 adb）占着设备。`taskkill //F //IM adb.exe` 全部清掉再用指定路径的 adb 重连 |

---

## 已知限制 / 后续可加

- 当前为单笔（单指 S Pen），未做多指/触摸作为第二输入。
- 鼠标模式暂时只做「移动 + 左键 + 右键」，没有滚轮 / 拖拽（长按拖动）。
- 手机端目前是「所见即所得」白板反馈；可改为仅传输、手机屏熄屏省电。
- 可加：S Pen 侧键映射为 Ctrl/Alt/橡皮、长按拖动、双指滚动、蓝牙低延迟模式、笔迹平滑滤波。
- 若你的绘画软件不吃合成笔（极少数），可在 `appsettings.json` 切 `"Mode":"Mouse"` 先用起来。

---

## 参考

- `CreateSyntheticPointerDevice` / `InjectSyntheticPointerInput` / `POINTER_PEN_INFO` —— Microsoft Learn (winuser.h)
- Android `MotionEvent`：`TOOL_TYPE_STYLUS`、`AXIS_TILT`、`AXIS_ORIENTATION`、`BUTTON_STYLUS_*`
