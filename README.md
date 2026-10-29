# MonitorSwitch 显示器助手

一款 Windows 11 风格的显示器配置切换工具：把当前的显示器排列与参数（分辨率、刷新率、方向、HDR、拓扑）保存为**模板**，之后通过**托盘面板**或**全局快捷键**一键切换。

[![Build](https://github.com/Introl-ljl/monitor-switch/actions/workflows/build.yml/badge.svg)](https://github.com/Introl-ljl/monitor-switch/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/Introl-ljl/monitor-switch)](https://github.com/Introl-ljl/monitor-switch/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)](https://github.com/Introl-ljl/monitor-switch)
![.NET](https://img.shields.io/badge/.NET-8.0--WPF-512BD4)

多显示器用户经常需要在几种固定场景之间来回切换 —— 比如「笔记本单屏办公」「外接双屏」「竖屏写代码」「投影观影」。Windows 自带的 `Win+P` 只能改拓扑，分辨率和排列每次都要手动重设。MonitorSwitch 把整套配置存成模板，一次点击或一个快捷键就能全部恢复。

---

## 安装

**直接下载**：前往 [Releases](https://github.com/Introl-ljl/monitor-switch/releases/latest) 下载 `MonitorSwitch-v*-win-x64.exe`。

- 自包含单文件，**无需安装 .NET 运行时**，双击即用
- 首次启动会解压到临时目录，约需数秒；之后启动更快
- 未做代码签名，SmartScreen 可能提示未知发布者 → 「更多信息 → 仍要运行」

**从源码构建**：见[构建](#构建)。

---

## 截图

### 显示器页

排列画布支持拖拽、边缘吸附、防重叠；下方是选中显示器的参数。

![显示器页](docs/display.png)

> 所有改动都只写入**草稿**，顶部会出现「有尚未应用的更改」，点「应用」才真正切换 —— 连续调整多个参数时屏幕不会反复闪烁。

### 配置模板页

![配置模板页](docs/profiles.png)

### 托盘面板

左键点击托盘图标弹出，每份模板带迷你排列预览。

![托盘面板](docs/tray.png)

### 设置页

![设置页](docs/settings.png)

---

## 功能

### 主窗口

- **显示器页**：仿 Windows 设置的显示器排列画布，下方为选中显示器的参数：分辨率、刷新率、方向、HDR、启用/停用、设为主显示器。
  - 改动采用**草稿模式**：只写内存，不立即生效。点「应用」统一下发，点「撤销」恢复。
  - 把某个下拉框改回原值，提示条会自动消失（逐字段比对，不是「改过就算脏」）。
- **配置模板页**：从当前配置一键创建模板，支持重命名、快捷键绑定（Ctrl/Alt/Shift/Win + 数字/字母/F1-F24）、删除、导入/导出（JSON 备份）。
- **设置页**：主题（跟随系统/浅色/深色）、开机自启、启动最小化到托盘、关闭到托盘、应用后保留确认。

### 托盘

- **左键**：弹出快速切换面板，含模板列表与 仅电脑屏幕 / 复制 / 扩展 / 仅第二屏幕 投影快捷键。
- **右键**：模板列表、打开主窗口、开机自启、退出。
- 面板可拖动，**会记住你拖到的位置**（以物理像素保存，跨不同 DPI 显示器也能正确还原；显示器拔掉导致坐标失效时自动回退）。

### 安全机制

这个工具会改动系统的显示配置，因此加了三层保护：

1. **草稿模式** —— 改动不点「应用」就不生效，避免误触下拉框导致屏幕闪烁或分辨率错乱。
2. **15 秒自动还原** —— 与 Windows 设置一致：应用后屏幕底部出现确认条，15 秒内不确认自动回滚（可在设置中关闭）。
3. **验证优先** —— 所有配置先经 `SetDisplayConfig(SDC_VALIDATE)` 验证，并按「标志组合 × 参数降级」阶梯依次尝试；全部失败才报错，不会半途把显示器调乱。

---

## 使用

### 基本流程

1. 把显示器调成你想要的状态（用 Windows 设置或本工具的显示器页）
2. 到「配置模板」页点「从当前配置新建」
3. 需要时用托盘面板点一下，或按绑定的全局快捷键

### 命令行

```powershell
MonitorSwitch.exe              # 正常启动
MonitorSwitch.exe --minimized  # 只驻留托盘，不显示主窗口
```

### 数据位置

`%LOCALAPPDATA%\MonitorSwitch\`

| 文件 | 内容 |
|---|---|
| `profiles.json` | 配置模板 |
| `settings.json` | 应用设置 |
| `log.txt` | 运行日志（超过 256 KB 自动清空） |

配置文件损坏时会自动备份为 `*.corrupt-<时间戳>` 并尽力抢救可解析的条目，不会静默清空。

---

## 构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)：

```powershell
dotnet build src/MonitorSwitch/MonitorSwitch.csproj -c Release
dotnet run --project src/MonitorSwitch/MonitorSwitch.csproj
```

打包成自包含单文件（与 Release 产物一致）：

```powershell
dotnet publish src/MonitorSwitch/MonitorSwitch.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -o publish
```

产物约 65 MB，双击即用。

---

## 技术实现

这个项目的难点几乎都在 Windows 显示子系统的细节上，这里记录几处反直觉的地方。

### CCD API

用 `QueryDisplayConfig` / `SetDisplayConfig` / `DisplayConfigGetDeviceInfo` 读取与应用显示拓扑、源/目标模式、刷新率时序、HDR（`SET_ADVANCED_COLOR_STATE`）；用 `EnumDisplaySettingsExW` 枚举各显示器支持的分辨率/刷新率，`EnumDisplayDevicesW` 取显卡与监视器友好名。

### 自适应结构体步长

不同 Windows 版本的 `DISPLAYCONFIG_MODE_INFO` 大小不同（本机实测 **64 字节**，`pixelRate` 与 `hSyncFreq` 在首个 dword 处重合）。工具以原始字节缓冲区 + 运行时步长检测处理，兼容新旧系统。

### 旋转与源模式

CCD 的源模式**始终以面板原生朝向表示**。90°/270° 旋转时桌面宽高互换 —— 竖屏 2560×1600 面板对应源模式 2560×1600，而桌面占用为 1600×2560。工具内部同时保存面板朝向（`SourceWidth/Height`）与桌面朝向（`DesktopWidth/Height`），避免把两者混淆。草稿里存的是桌面朝向（用户视角），下发前再换算回面板朝向。

### 目标时序有效性判定

只有当模板中的目标模式（`pixelRate` / `vSync` / `activeSize`）仍与系统当前为该路径选中的目标模式一致时，才复用该时序；否则交给系统按新分辨率/刷新率重新选择。这是「改分辨率或刷新率静默失效」的根因。

### 托盘常驻内存优化

托盘常驻时专用工作集约 **8 MB**。做到这点踩了几个坑：

| 问题 | 原因 | 处理 |
|---|---|---|
| 常驻内存 128 MB | WPF 的 `HwndSource` 是渲染宿主，创建即初始化 D3D 管线，加载显卡驱动模块约 126 MB | 改用纯 Win32 消息窗口（`RegisterClassEx` + `HWND_MESSAGE`），托盘场景不需要任何渲染能力 |
| +24 MB | WPF-UI 的资源字典实例化整套控件模板 | 延迟到首次显示窗口时才加载 |
| +5.7 MB | `App.xaml` 里引用 `ui:FontIcon` 让 `Wpf.Ui.dll` 启动阶段就加载 | 样式改为随资源延迟注册 |
| +11 MB | `System.Drawing.Common` 拉进 `windows.storage.dll` 等模块 | 托盘图标改用 `LoadImage` |

`Shell_NotifyIcon` 本身会加载 `windows.storage.dll`（8.7 MB），这是托盘功能的刚需，无法避免。

### UI 渲染

- **不用 ClearType**：子像素渲染会在字形边缘画出橙/蓝/绿彩色条纹。深色背景 + 中文小字号时最刺眼 —— 实测单像素绿色偏移最高 +75，整片文字 36.8% 的像素带明显色偏。改用 `Grayscale` 后彩色像素占比降到 0%。
- **强调色统一覆盖**：系统强调色若是低饱和度色（如青灰 `#69797E`），WPF-UI 派生的整套画刷会让 UI 泛青绿。注意 `ApplicationAccentColorManager.Apply` **不会**更新 `AccentButtonBackground`，必须直接写资源字典。

---

## 已知限制

- **缩放百分比**为只读展示。Windows 没有公开的按显示器 DPI 设置 API；工具从注册表 `HKCU\Control Panel\Desktop\PerMonitorSettings\<monitorId>\DpiValue` 读取实际值（`0` = 100%，`+1` = 125%，`-1` = 75%）。修改缩放请用 Windows 设置。
- **显卡名**取自 GDI 的适配器友好名。CCD 的 `DISPLAYCONFIG_ADAPTER_NAME` 只提供设备路径（`\\?\PCI#...`），不提供可读名称。
- **无 EDID 名称的显示器**（部分内置屏 / 采集卡）会显示为「内置显示屏」，取决于驱动上报的信息。
- **投影快捷键**（复制/扩展等）依赖系统已保存的对应拓扑记录，首次使用某模式可能需先在 Windows 设置里手动配置一次。
- 打开过主窗口后，返回托盘时内存**不会**回到 8 MB —— WPF 的 D3D 管线是进程级状态，无法卸载。这是框架限制。

---

## 贡献

欢迎提交 Issue 与 PR，请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。

涉及显示配置的改动**务必在真实多显示器环境验证** —— 这类改动容易「看起来成功但实际没生效」。

---

## 许可证

[MIT](LICENSE)

## 参考

- [davide136/monswitch](https://github.com/davide136/monswitch)
- [terrymacdonald/DisplayMagician](https://github.com/terrymacdonald/DisplayMagician)
- [CCD APIs - Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/ccd-apis)
- [WinUI 3 / Fluent Design](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
- [WPF-UI (lepoco)](https://wpfui.lepo.co/)
