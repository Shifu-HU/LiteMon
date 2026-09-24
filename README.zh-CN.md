# LiteMon — 轻量 Windows 硬件悬浮监视器

[English](README.md) | [简体中文](README.zh-CN.md)

> 不能关进程的"任务管理器"：CPU / GPU / 内存 / 显存 / 磁盘实时占用 + 每应用占用明细，
> 任务管理器式主窗口，MIT 开源。

**MIT License · C# .NET 8 + WPF · Windows 10/11 x64**

## 下载安装

去 [Releases](https://github.com/Shifu-HU/LiteMon/releases/latest) 页面：

| 文件 | 说明 |
|---|---|
| `LiteMon-Setup-v*.exe` | **直装版**：双击安装，带中文向导 / 桌面快捷方式 / 卸载器，可选开机自启 |
| `LiteMon-v*-win-x64.zip` | **绿色便携版**：解压即用，不写注册表 |
| 源码压缩包 | 由 GitHub 自动生成 |

系统要求：Windows 10/11 x64。自包含发布，**无需安装 .NET 运行时**。

---

## 功能

### 主窗口（任务管理器式布局）
- **顶栏状态 Chip**：CPU 占用+速度（GHz） / GPU 占用 / VRAM 占用 / RAM 占用+已用/总量
- **导航**：CPU / GPU / RAM / DISK1 / DISK2…（插入 U 盘自动出现）/ 右侧独立齿轮设置按钮

| 页签 | 内容 |
|---|---|
| **CPU** | 占用率 / 速度（GHz）/ 温度 / 功耗 / 进程数摘要卡 + **每核水平长条**（从左向右填充，超 60% 变橙、85% 变红）+ **应用列表**（应用+PID / CPU% / 内存 / GPU%，按 CPU 排序） |
| **GPU** | 占用率 / 显存 / 温度 / 功耗 / 核心频率（GHz）+ 显存占用条 + **GPU 占用最高的应用**（GPU% / 专用显存 / 共享显存 / 内存） |
| **RAM** | 占用 / 已用总量 / 提交 / 网速 + 物理内存条 + **内存占用最高的应用** |
| **DISK1** | 所有内置盘：容量占用进度条 / 剩余空间 / 读写速度 |
| **DISK2 / DISK3…** | **外接盘自动出现页签，拔出自动消失**（紧跟 DISK1 依次排列） |
| **⚙ 设置** | 刷新率（0.5/1/2/5 秒）、开机自启、主题（深/浅/跟随系统）、9 套配色（三等分圆选择器）、**语言下拉（7 种）**、暂停、托盘驻留 |

### 语言 / Language / 言語
设置页语言下拉内置 **7 种语言**：简体中文、English、日本語、Français、Русский、Español、العربية
（覆盖联合国全部 6 种官方语言 + 日语）。切换立即生效并**停留在当前页**；阿拉伯语自动切换为 RTL 镜像布局。
新增语言只需在 `Services/Loc.cs` 里补一份字典（键集与 `Zh` 对齐）并登记到 `Loc.Available`。

### 外接盘识别（为什么你的移动硬盘能被认出来）

USB 移动硬盘在 Windows 里**经常被报成内置盘**，这不是 LiteMon 的问题，是系统层面的历史包袱：

| 情况 | DriveInfo 报告 | PNPDeviceID | MediaType |
|---|---|---|---|
| 优盘 / 部分移动硬盘 | `Removable` | `USBSTOR\...` | Removable Media |
| **硬盘盒（USB 桥接 SATA/NVMe）** | ⚠️ **`Fixed`** | ⚠️ `SCSI\...` | ✅ `External hard disk media` |
| 读卡器空槽位 | `Removable`（容量 0、无就绪） | `USBSTOR\...` | — |
| 内置 NVMe / SATA | `Fixed` | `SCSI\...` | Fixed hard disk media |

中间那一行就是坑：**硬盘盒里的桥接芯片（JMicron / RTL9210 / ASMedia 等）把硬盘通过 SCSI 通道呈现给系统**，
于是 Windows 的 `DriveInfo.DriveType` 返回 `Fixed`，`PNPDeviceID` 也不以 `USBSTOR` 开头 ——
只看这两个字段会把移动硬盘误判成内置盘（并因此不生成 DISK2 页签）。

LiteMon 因此**同时检查三个字段**，命中任一即视为外接盘：

```
PNPDeviceID 以 USBSTOR / USB 开头        → 优盘、部分移动硬盘
InterfaceType 含 USB                     → 同上，另一种表述
MediaType 含 External                    → ★ USB 桥接硬盘盒的唯一可靠信号
MediaType 含 Removable                   → 读卡器 / 可换介质
```

判定逻辑抽成了纯函数 `DiskCollector.IsExternalBus(pnp, iface, media)`，**不依赖 WMI、可在任意机器与 CI 上单元测试**
（见 `tests/LiteMon.Core.Tests/SmokeTests.cs`，覆盖上述四类真实设备字符串）。
若你的设备仍然识别不对，请提 issue 并附上：

```powershell
Get-CimInstance Win32_DiskDrive | Select-Object Model, InterfaceType, MediaType, PNPDeviceID
```

另外，**容量为 0 且未就绪的读卡器空槽位不会生成页签**（避免出现一个空的 DISK2）。

> 关于"活动时间"：任务管理器里的磁盘使用率来自 `% Idle Time`。该计数器在不少平台上长期返回 0
> （包括本项目开发机），会把活动时间永远显示成 100%，**误导性大于参考价值**，因此 LiteMon 不展示它。
> 读写速度是独立且可靠的链路，不受影响。

### 采集与降级链（绝不崩溃）
| 数据 | 主来源 | 降级 1 | 降级 2 | 全不可用时 |
|---|---|---|---|---|
| CPU 总/每核占用 | PDH 英文计数器 `Processor Information(*)` | PDH `Processor(*)` | — | 显示 0 |
| CPU 频率 | LHb 时钟传感器 | 标称频率 × `% Processor Performance` | — | N/A |
| CPU 温度/功耗 | LibreHardwareMonitor（需管理员） | — | — | N/A（提示） |
| 内存 | GlobalMemoryStatusEx | — | — | — |
| GPU 占用/显存/温度/功耗/频率 | **NVML**（NVIDIA 官方，无需管理员） | LibreHardwareMonitor | PDH GPU 计数器（仅显存） | "未检测到 GPU"遮罩 |
| 网络速度 | PDH `Network Interface(*)` 求和（排除环环/虚拟网卡） | — | — | 显示 0 |
| 每应用 CPU/内存 | `Process` API（两帧差分，任务管理器口径） | — | — | 空列表 |
| 每应用 GPU%/显存 | PDH `GPU Engine` / `GPU Process Memory` 按 pid 求和 | — | — | 0 |
| 磁盘容量/剩余 | DriveInfo（GetDiskFreeSpaceEx） | — | — | 空列表 |
| 磁盘读写速度 | PDH `LogicalDisk(X:)` 英文计数器（`Disk Read/Write Bytes/sec`） | — | — | 显示 0 |
| 可移动盘插入/拔出 | 每帧枚举盘符对比（DriveInfo），1 秒内出现/消失 Disk2+ 页签 | — | — | — |
| **外接盘判定** | Win32_DiskDrive 的 PNPDeviceID / InterfaceType / **MediaType** 三字段联合判定 | — | — | 按内置盘处理 |

所有采集器都有独立的 try/catch + 一次性降级标记：**任何一条链路坏了只影响对应指标，程序本体不受影响**（无独显机器、未装驱动、无管理员权限均可正常运行）。

---

## 实测数据（开发机：Intel Core Ultra 9 275HX + RTX 5060 Laptop / 32GB / Win11）

| 指标 | 实测 | 目标 |
|---|---|---|
| CPU 占用（全机占比） | **0.13%** | < 1%~2% ✅ |
| 内存（Private Bytes） | **~102MB** | < 50MB ⚠️ |
| 内存（Working Set） | ~160MB | — |

> 内存说明：50MB 目标对 **.NET 8 + WPF** 框架底座（空 WPF 窗口应用即约 70~80MB）不现实。
> 已做优化：GC 堆硬限制（32MB）、ListView 每 2s 才重建、LHb 传感器 3s 节流、
> 进程枚举 2s 节流。若把"50MB"理解为**净增内存**，实际增量约 30~40MB 达标。
> 若必须 <50MB 绝对值，可考虑 Win32/WinUI 原生方案（roadmap）。

### 数据准确性（与任务管理器对比口径）
- CPU：PDH `% Processor Time` = 任务管理器同源数据
- 每应用 CPU：`ΔTotalProcessorTime / ΔWallTime / 逻辑核数`，与任务管理器一致（单核满载在 24 核上显示 ~4.2%）
- 每应用显存：`GPU Process Memory` 计数器 = 任务管理器"专用 GPU 内存"同源
- GPU/显存/温度：NVML = NVIDIA 官方精确值

---

## 构建说明

### 环境
- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（winget: `winget install Microsoft.DotNet.SDK.8`）

### 一键构建
```bat
build.bat
```
产物：`src\LiteMon.Wpf\bin\Release\net8.0-windows\LiteMon.exe`

### 手动构建
```bat
dotnet restore LiteMon.sln
dotnet build LiteMon.sln -c Release
dotnet test tests\LiteMon.Core.Tests\LiteMon.Core.Tests.csproj
```

### 发布单文件 exe（免安装绿色版，~65MB，自带 .NET 运行时）
```bat
dotnet publish src\LiteMon.Wpf\LiteMon.Wpf.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```
产物：`publish\LiteMon.exe` —— 单文件、双击即用、无需安装 .NET。

### 依赖框架版（更小，需目标机装 .NET 8）
```bat
dotnet publish src\LiteMon.Wpf\LiteMon.Wpf.csproj -c Release -r win-x64 --self-contained false -o publish-lite
``` 

---

## 依赖说明

| 依赖 | 版本 | 用途 | 许可证 |
|---|---|---|---|
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | 0.9.6 | CPU/GPU 温度、功耗、频率传感器（需要管理员才出温度） | MPL-2.0 |
| NVML（系统自带） | 驱动内置 | NVIDIA GPU 占用/显存/温度/功耗（P/Invoke，零依赖） | NVIDIA 驱动随附 |
| Windows PDH | 系统 API | CPU/网络/GPU 计数器（P/Invoke，零依赖） | 系统 |
| .NET 8 | 8.0.x | 运行时 | MIT |

> LibreHardwareMonitorLib 为 MPL-2.0，以 NuGet 包方式引用（独立进程内程序集），与 MIT 主程序许可兼容。

---

## 目录结构

```
LiteMon/
├── LiteMon.sln
├── build.bat                    # 一键构建脚本
├── LICENSE                      # MIT
├── src/
│   ├── LiteMon.Core/            # 无 UI 采集核心（可独立测试）
│   │   ├── Models/Metrics.cs    # 快照模型（不可变）
│   │   ├── Collectors/
│   │   │   ├── CpuCollector.cs      # PDH 每核 + LHb 温度/频率
│   │   │   ├── GpuCollector.cs       # NVML → LHb → PDH 降级链
│   │   │   ├── MemoryCollector.cs   # GlobalMemoryStatusEx
│   │   │   ├── NetworkCollector.cs  # PDH 网卡求和
│   │   │   └── ProcessCollector.cs  # 每应用 CPU/GPU/显存/内存
│   │   ├── Interop/             # PDH / NVML / Win32 P/Invoke
│   │   ├── Scheduling/MonitorService.cs  # 单后台线程采集调度
│   │   ├── Settings/AppSettings.cs       # JSON 持久化
│   │   └── Formatting.cs        # 单位格式化
│   └── LiteMon.Wpf/             # UI
│       ├── App.xaml(.cs)        # 启动流程 + 托盘 + 全局异常兜底
│       ├── Views/
│       │   ├── MainWindow.xaml(.cs)      # 主窗口（顶栏 Chip + 导航）
│       │   ├── MainWindow.xaml(.cs)      # 主窗口（导航 + 状态 Chip）
│       │   └── Pages/            # CPU/GPU/内存/应用/设置 五页
│       ├── Controls/            # AppListPanel（应用列表）/ ToggleSwitch
│       ├── Themes/              # 主题令牌 + 控件样式（深/浅/9 配色）
│       └── Services/            # AppRuntime / ThemeManager / TrayIcon / Log
└── tests/
    └── LiteMon.Core.Tests/       # xUnit 冒烟测试（8 项全过）
```

---

## 设计要点（为什么快）

1. **单采集线程**：PDH 有线程亲和限制，且单线程免锁；快照不可变、引用原子交换，UI 单向拉取
2. **PDH 英文计数器**（PdhAddEnglishCounter）：中文 Windows 上照样可用（避开 PerformanceCounter 本地化坑）
3. **NVML 优先于 LHb**：无需管理员、精度官方、开销最小
4. **UI 节流**：应用列表 2s 重建一次、LHb 传感器 3s 刷一次、进程枚举 2s 一次
5. **无第三方 UI/托盘库**：托盘 Shell_NotifyIcon P/Invoke 手写，图表自绘 120 点环形缓冲
6. **优雅降级**：每条采集链路独立 try/catch + 一次性降级标记，异常风暴不可能发生

## 稳定性设计
- 全局异常兜底：UI 线程 DispatcherUnhandledException + AppDomain 均不崩溃，只记日志
- 所有采集器失败返回上帧数据或 0，绝不抛出
- 日志轮换：%LOCALAPPDATA%\LiteMon\log.txt 超 2MB 清空
- 设置写入失败静默（不因磁盘问题退出）

---

## Roadmap
- [ ] 第四步：AMD（ADLX）/ Intel（IGCL）GPU 支持（架构已预留 GpuVendor 分支）
- [ ] Linux 移植（Core 层已与 UI 分离）
- [ ] 内存占用进一步压缩（如需绝对值 <50MB 考虑原生方案）

## License
MIT — 详见 [LICENSE](LICENSE)
