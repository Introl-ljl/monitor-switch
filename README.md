# MonitorSwitch 显示器助手

一款 Windows 11 风格的显示器配置切换工具：把当前的显示器排列与参数（分辨率、刷新率、方向、HDR、拓扑）保存为**模板**，之后通过**托盘面板**或**全局快捷键**一键切换。

![技术栈](https://img.shields.io/badge/.NET-8.0--WPF-blue) ![UI](https://img.shields.io/badge/UI-WPF%E2%80%90UI%20(Fluent%202)-purple)

## 功能

### 主窗口
- **显示器页**：仿 Windows 设置的显示器排列画布（可拖拽、边缘吸附、防重叠、主显示器色条标记），下方为选中显示器的参数设置：分辨率、刷新率、方向、HDR、启用/停用、设为主显示器。

  所有改动**只写入草稿，不会立即生效**。页面顶部出现"有尚未应用的更改"提示条，点"应用"才统一下发到系统，点"撤销"恢复为当前配置。
  这样连续调整多个参数时屏幕不会反复闪烁；把某个下拉框改回原值，提示条会自动消失。
- **配置模板页**：从当前配置一键创建模板，支持重命名、快捷键绑定（Ctrl/Alt/Shift/Win + 数字/字母/F1-F24）、删除、导入/导出（JSON 备份）。
- **设置页**：主题（跟随系统/浅色/深色）、开机自启、启动最小化到托盘、关闭到托盘、应用后保留确认。

### 托盘
- 左键点击托盘图标弹出快速切换面板：每份模板带迷你排列预览图与"当前使用中"标记，底部还有 仅电脑屏幕 / 复制 / 扩展 / 仅第二屏幕 投影快捷键。
- 右键菜单：模板列表、打开主窗口、开机自启、退出。

### 安全机制
- **二次确认（草稿模式）**：显示器页的改动不会即时生效，必须先点"应用"。避免误触下拉框导致屏幕闪烁或分辨率错乱。
- **15 秒保留/还原保护**：每次应用会改变显示效果的配置后，屏幕底部出现确认条，15 秒内未确认自动还原到之前的配置（与 Windows 设置行为一致，可在设置中关闭）。
- **验证优先**：所有配置先经 `SetDisplayConfig(SDC_VALIDATE)` 验证，并按"标志组合 × 参数降级"阶梯依次尝试，全部失败才会报错，不会半途把显示器调乱。

## 技术实现

- **CCD API**（`QueryDisplayConfig` / `SetDisplayConfig` / `DisplayConfigGetDeviceInfo`）：读取与应用显示拓扑、源/目标模式、刷新率时序、HDR（`SET_ADVANCED_COLOR_STATE`）。
- **GDI**（`EnumDisplaySettingsExW`）：枚举各显示器支持的分辨率/刷新率列表；`EnumDisplayDevicesW` 提供显卡与监视器友好名。
- **自适应结构体步长**：不同 Windows 版本的 `DISPLAYCONFIG_MODE_INFO` 大小不同（本机实测为 **64 字节**，`pixelRate` 与 `hSyncFreq` 在首 dword 处重合）。本工具以原始字节缓冲区 + 运行时步长检测处理，兼容新旧系统。
- **旋转与源模式**：CCD 的源模式始终以面板原生朝向表示，90°/270° 旋转时桌面宽高互换。工具内部同时保存面板朝向（`SourceWidth/Height`）与桌面朝向（`DesktopWidth/Height`），避免竖屏时把 2560×1600 误当成 1600×2560。
- **目标时序有效性判定**：只有当模板中的目标模式（`pixelRate` / `vSync` / `activeSize`）仍与系统当前为该路径选中的目标模式一致时，才复用该时序；否则交给系统按新的分辨率/刷新率重新选择。这是"改分辨率或刷新率静默失效"的根因。
- **WPF + WPF-UI 4**（Fluent 2 设计语言，Mica / Acrylic 背板）、`Shell_NotifyIcon` 自实现托盘、`RegisterHotKey` 全局快捷键。

## 构建与运行

需要 [.NET 8 SDK](https://dotnet.microsoft.com/)（项目目录自带 `\.dotnet\` 8.0.427 亦可）：

```powershell
# 使用项目自带的本地 SDK
.\.dotnet\dotnet.exe build src\MonitorSwitch\MonitorSwitch.csproj -c Release

# 运行
src\MonitorSwitch\bin\Release\net8.0-windows\MonitorSwitch.exe

# 静默启动（仅托盘）
src\MonitorSwitch\bin\Release\net8.0-windows\MonitorSwitch.exe --minimized
```

数据存储在 `%LOCALAPPDATA%\MonitorSwitch\`（profiles.json / settings.json / log.txt）。配置文件损坏时会自动备份为 `*.corrupt-<时间戳>` 并尽力抢救可解析的条目，不会静默清空。

## 已知限制

- **缩放百分比**为只读展示。Windows 没有公开的按显示器 DPI 设置 API；本工具从注册表
  `HKCU\Control Panel\Desktop\PerMonitorSettings\<monitorId>\DpiValue` 读取实际值
  （`0` = 100%，`+1` = 125%，`-1` = 75%）。修改缩放请使用 Windows 设置。
- **显卡名**取自 GDI 的适配器友好名。CCD 的 `DISPLAYCONFIG_ADAPTER_NAME` 只提供设备路径（`\\?\PCI#...`），
  不提供可读名称。
- **无 EDID 名称的显示器**（部分内置屏 / 采集卡）会显示为"内置显示屏"或"未知显示器"，这取决于驱动上报的信息。
- 投影快捷键（复制/扩展等）依赖系统已保存的对应拓扑记录，首次使用某模式可能需要先在 Windows 设置中手动配置一次。
- 显示器以**目标设备路径**（`\\?\DISPLAY#...`）为稳定标识，同一物理端口重新插拔后仍可匹配。

## 参考

- [davide136/monswitch](https://github.com/davide136/monswitch)
- [terrymacdonald/DisplayMagician](https://github.com/terrymacdonald/DisplayMagician)
- [CCD APIs - Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/ccd-apis)
- [WinUI 3 / Fluent Design](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
- [WPF-UI (lepoco)](https://wpfui.lepo.co/)
