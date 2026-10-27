# SpenTablet —— 把旧三星 Note + S Pen 变成 PC 数位板 & 触摸板

把闲置的三星 Note 手写笔手机，变成 Windows PC 上的**压感数位板**和**多点触摸板**。
手机与 PC 通过局域网 / USB 实时通信，打开即用，零配置。

> **零操作上手**：PC 端启动后，打开手机 App 就会**自动连上**（mDNS 自动发现），
> 左上角**绿点 = 已连接**。默认就是鼠标模式——手指当触摸板，立刻能用。

| | |
|---|---|
| ![竖屏主界面](docs/img/main_portrait.png) | ![横屏主界面](docs/img/main_landscape.png) |
| 主界面只有一行：绿点 + 四个图标，书写区最大化 | 横竖屏都支持，陀螺仪自动翻转 |

---

## ✨ 功能亮点

- **压感数位板**：S Pen 带压感 / 倾斜，直接在 Photoshop / Krita / SAI 里作画
- **鼠标模式（默认）**：手指 = 触摸板，双指滚动、捏合缩放、三指前进后退，一套手势全都有
- **主界面一键切换**：图标即状态——鼠标图标 = 鼠标模式，笔图标 = 数位板模式，点了就切
- **零配置连接**：mDNS 自动发现，连 IP 都不用填；USB / 热点 / WiFi / VPN 各种网络环境都能连
- **网络拓扑可视化**：点绿点看拓扑图——连接方式、PC IP、客户端版本号、手机 IP，一目了然；
  局域网里有多台 PC 时点击即可切换
- **跨屏范围框**：桌面上橙色框=可作画区域，可以拖到**任意显示器**（多屏），双指捏合缩放
- **陀螺仪方向**：跟随（四向）/ 横屏双朝向 / 竖屏双朝向，三种模式一键循环
- **独立设置页**：IP / 比例 / 模式 / 增益 / 版本信息 / 检查更新，按返回键即关

## 🖼 功能实机一览

### 网络拓扑（点左上角绿点）

拓扑图直观显示「手机 ↔ 桌面 PC」与连接方式；下方是 IP、版本号等详细信息；
局域网里的其它桌面**点击即切换连接**。

![网络拓扑](docs/img/topology.png)

### 使用指南（右上角问号）

内置操作指南：自动连接说明、两种模式的手势、PC 端下载链接（可点击直接打开浏览器）。

![使用指南](docs/img/guide.png)

### 设置页（右上角齿轮）

连接地址、mDNS 发现、比例映射、模式切换、压感增益……底部有**版本信息**与
**检查更新 / 项目主页**快捷入口，按返回键关闭。

![设置页](docs/img/settings.png)

---

## 🚀 快速开始（三步）

1. **PC 端**：从 [Releases](https://github.com/volcanochen/drawing-phone/releases/latest)
   下载 `SpenTabletHost.exe` 运行（单文件、免安装，托盘常驻）；
   或自行编译：`cd PcHost && dotnet publish -c Release` → `publish/SpenTabletHost.exe`
2. **手机端**：安装 `PhoneTablet` 的 APK（Android Studio 打开 `PhoneTablet/` 目录构建）
3. **连上就用**：PC 和手机连同一网络 → 打开 App → 绿点亮起 → 完成！
   手机横过来当触摸板，掏出 S Pen 直接画画

> 手机没有和 PC 在同一网络？用 **USB 数据线**连接 + `adb forward tcp:6000 tcp:6000`，
> App 里填 `127.0.0.1` 即可（usb0/RNDIS 网卡会自动识别为 USB 网络共享）。

## ✋ 两种操作方式（主界面一键切换）

主界面右侧第一个图标就是**当前模式的实时状态**：

- 🖱 **鼠标图标 = 鼠标模式（默认）**：手指是笔记本触摸板
- ✏️ **笔图标 = 数位板模式**：S Pen 在桌面橙色框内作画，手指负责移动 / 缩放框

| 手势 | 鼠标模式 | 数位板模式 |
|------|----------|------------|
| 单指滑动 | 移动光标 | 移动桌面范围框 |
| 单指单击 | 左键 | — |
| 单指双击 | 右键 | — |
| 双指滑动 | 平滑滚动（像素级，横竖向） | — |
| 双指捏合 | 缩放（Ctrl+滚轮） | 缩放范围框 |
| 双指点击 | 右键 | — |
| 三指点击 | 中键 | — |
| 三指左右滑 | 后退 / 前进 | — |
| S Pen | 绝对定位的鼠标笔 | 压感 / 倾斜作画 |

切换会**实时同步 PC**：数位板模式桌面显示范围框，鼠标模式自动取消范围框。

## 🖥 桌面上的范围框

- 橙色框 = S Pen 能覆盖的桌面区域，与手机书写区**宽高比恒定一致**（笔迹不变形）
- **单指滑动 = 移动框，双指捏合 = 缩放框**（以手指为中心锚点，焦点内容不跑位）
- 框可以移动到**屏幕的任何位置**，包括第二块、第三块显示器（按整个虚拟屏幕钳制）
- 手机端书写区永远撑满画布，可操作范围最大

## 📡 连接与拓扑

- 打开 App **自动连接**：先试上次地址，同时静默 mDNS 发现，找到唯一 PC 直接连上
- **绿点 / 红点**：主界面左上角圆点即连接状态；点击弹出**网络拓扑**：

  - 拓扑图：「手机」↔「桌面 PC」，连线上标注连接方式（USB 网络共享 / WiFi 局域网 / 局域网 / USB+adb）
  - 详细信息：桌面 PC IP、**客户端版本号**、连接方式、手机 IP、手机名
    （版本号 / 计算机名 / 候选 IP 由 PC 在握手后经 `TYPE_INFO (0x0C)` 帧下发）
  - 「局域网里的桌面」：mDNS 发现的其它 PC，**点击切换连接**

- 连不上？设置页有「发现 PC（mDNS）」和「扫描网段找 PC」两道保险；手机 IP 与连接方式自动识别

## 🧭 屏幕方向

主界面循环按钮三态，全部**依赖陀螺仪、无视系统旋转锁**：

1. **跟随**（`fullSensor`）：四个朝向随陀螺仪实时翻转
2. **横屏**（`sensorLandscape`）：两个横屏朝向自动翻转，不会转成竖屏
3. **竖屏**（`sensorPortrait`）：两个竖屏朝向自动翻转

旋转时连接不断（Activity 不重建），PC 端按新的书写区宽高比自动重算映射区域。

## ⚙️ 设置页

- **连接**：PC IP + 端口（支持手动 / mDNS 发现 / 扫描网段三种方式）
- **比例映射**：保持手机比例（推荐，笔迹不变形）/ 拉伸铺满
- **模式**：数位板 / 鼠标（与主界面按钮联动）
- **复位操作框**、**悬停转发开关**、**压感增益**（0.5~8）
- **版本信息**：手机端版本 + PC 端版本（拓扑页也可看）；「检查更新」「项目主页」直达浏览器

## 🔍 调试

主界面不再显示调试文字（保持极简），全部改为**后台定量循环日志**：

- 文件：`Android/data/com.volcano.spentablet/files/spen_debug.log`
- 每 2 秒一条统计（连接 / 书写区 / 范围框 / 发送丢弃帧数），事件按 300ms 节流
- 单文件超 256KB 自动轮转 `.old`，总占用恒定 ≤ ~512KB

---

## 架构

```
手机 (Android, Kotlin)                      PC (Windows, .NET 8)
┌──────────────────────┐   TCP 6000        ┌──────────────────────┐
│ PenCanvasView        │ ──PEN/MOUSE帧──▶  │ TcpPenServer          │
│  (S Pen + 手指手势)   │ ──HELLO/RESET──▶  │  ↓ 坐标映射            │
│ NetClient (自动重连)  │                   │ MappingConfig         │
│ MdnsDiscovery (NSD)  │ ◀─INFO(版本/IP)── │  ↓                    │
│ TopologyDialog       │                   │ PenInjector (WTI压感)  │
└──────────────────────┘                   │ MouseControl / mDNS   │
                                           └──────────────────────┘
```

二进制协议：每帧 = 2 字节 magic + 1 版本 + 1 类型 + 2 长度（大端）。
上行帧：HELLO(0x01) / PEN(0x02) / MOUSE(0x03) / PAN(0x04) / MODE(0x05) / RESET(0x06)…
下行帧：INFO(0x0C) = `ver=…&name=…&ips=…`（URL-encoded UTF-8）。
HELLO payload 28 字节（7 个 float）：`surfaceW, surfaceH, aspectMode, workMode, frameL, frameT, frameScale`
——最后三个是范围框状态，PC 断线重连后还原同形状同位置的框。

### 鼠标模式实现要点（触摸板手感）

- 多指事件整体处理；**只有单指移动光标**；多指期间忽略 S Pen（防干扰）并先松开按着的键
- 抬指后需在事件里排除残留手指，否则质心算错 →「松手光标跳一大段」
- 滚动发**非 120 倍数**的高分辨率滚轮增量 → Windows 按像素平滑滚动
- 双指跨度相对变化 > 12% 判定为捏合，否则滚动（手抖不误触）；回单指时重置基准点

## 目录结构

```
SpenTablet/
├── docs/img/               # README 实机截图
├── PhoneTablet/            # Android 端（Android Studio 打开此目录）
│   └── app/src/main/java/com/volcano/spentablet/
│       ├── Protocol.kt        # 二进制帧编解码（含 TYPE_INFO 下行帧）
│       ├── NetClient.kt       # TCP 长连接 + 自动重连 + 接收 PC 下行帧
│       ├── MdnsDiscovery.kt   # mDNS/NSD 自动发现 PC
│       ├── NetworkScan.kt     # 兜底：扫描网段找 PC / 识别连接方式与手机IP
│       ├── PenCanvasView.kt   # S Pen 捕获 + 手机屏画轨迹
│       ├── DebugLog.kt        # 后台调试日志（定量落盘 + 循环轮转）
│       ├── TopologyDialog.kt  # 连接状态 / 网络拓扑对话框
│       └── MainActivity.kt    # 界面/连接/设置/拓扑/指南
└── PcHost/                 # PC 端（.NET 8，Win32 P/Invoke + mDNS 广播）
    ├── Program.cs / TrayApp.cs      # 托盘常驻入口
    ├── ConfigForm.cs                # 设置窗口（连接/输入/映射/显示 + 实时诊断）
    ├── PenInjector.cs               # 合成笔注入（压感/倾斜）+ 鼠标回退
    ├── MouseControl.cs              # 鼠标模式：光标/滚轮/按键
    ├── TcpPenServer.cs              # TCP 帧解析 + INFO 帧下发
    ├── MdnsAdvertiser.cs            # mDNS 广播 _spentablet._tcp
    ├── MappingConfig.cs             # 坐标映射 / 虚拟屏幕钳制 / 模式
    ├── RegionFrame.cs               # 桌面操作范围框（可穿透点击）
    ├── NetInfo.cs                   # 网卡枚举 / USB共享识别 / 防火墙
    └── publish.cmd                  # 发布单文件 EXE
```

根目录 `test_protocol.py`：直连 127.0.0.1:6000 模拟手机发帧，可验证模式切换、
范围框显隐、光标位移与左右键。

## 前置条件

- PC：Windows 10/11 + .NET 8（单文件发布版自带运行时，免装）
- 手机：Android 8.0+，三星 Note 系列最佳（S Pen 压感依赖）

## 排错

| 现象 | 处理 |
|------|------|
| 红点连不上 | 设置页点「发现 PC（mDNS）」；不行再「扫描网段找 PC」 |
| USB 连接 | `adb forward tcp:6000 tcp:6000`，IP 填 127.0.0.1 |
| PC 收到帧但没笔迹 | 确认绘画软件窗口在前台；用托盘菜单的调试浮窗验证笔数据 |
| 防火墙拦截 | PC 端首次运行会自动添加 6000 端口规则，被安全软件拦时手动放行 |
| VPN / 多网卡导致连错 IP | PC 端日志会列出全部候选 IP；拓扑页可核对实际连接地址 |

## 已知限制 / 后续可加

- 压感依赖 Windows RealTimeStylus/WTI，个别绘图软件需切换 Wintab 模式
- 后续可加：二维码配对、多手机同时连、安卓端压感曲线调节

## 参考

- [Windows Pen Injection (WTI)](https://learn.microsoft.com/windows/win32/tablet/) · [NSD/mDNS](https://developer.android.com/develop/connectivity/wifi/nsd) · 虚拟屏幕坐标 `SM_XVIRTUALSCREEN`
