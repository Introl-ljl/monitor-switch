# 贡献指南

感谢你对 MonitorSwitch 的关注。这个项目的核心价值在于**对 Windows 显示子系统细节的正确处理**，因此代码改动请优先考虑兼容性与可验证性。

## 开发环境

- Windows 10 1809+ / Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 推荐 Visual Studio 2022 或 Rider（非必需，`dotnet` CLI 即可）

```powershell
dotnet restore src\MonitorSwitch\MonitorSwitch.csproj
dotnet build src\MonitorSwitch\MonitorSwitch.csproj -c Release
dotnet run --project src\MonitorSwitch\MonitorSwitch.csproj
```

## 提交前检查

1. **构建零警告零错误**
   ```powershell
   dotnet build src\MonitorSwitch\MonitorSwitch.csproj -c Release -v q --nologo
   ```
2. **实机验证**：显示相关改动必须在真实多显示器环境下手动验证。涉及 `SetDisplayConfig` 的改动容易「看起来成功但实际没生效」。
3. **不要提交** `bin/`、`obj/`、`/probe/` 或个人调试截图（`.gitignore` 已覆盖）。

## 代码约定

- 注释用中文，说明**为什么**这么做，而不是复述代码在做什么。本项目有多处反直觉的 Windows 行为（CCD 源模式朝向、`DISPLAYCONFIG_MODE_INFO` 结构体步长、WPF D3D 管线常驻等），这类地方务必写清原因。
- 新增 P/Invoke 放到 `Interop/` 下对应文件，命名与 Win32 原义保持一致。
- 自定义绘制的控件（`Controls/`）必须从主题资源取色，不要硬编码颜色 —— 否则浅色主题下会失效。
- UI 线程上不要做阻塞调用。

## 报告问题

提交 Issue 时请附上：

- Windows 版本（`winver`）、显卡型号与驱动版本
- 显示器数量、各自分辨率/刷新率/连接方式
- `%LOCALAPPDATA%\MonitorSwitch\log.txt` 中相关片段
- 复现步骤与期望/实际行为

如果涉及显示配置错乱，**请先确认你能用 Win+P 或 Windows 设置恢复**，再提交问题。

## 提交 PR

- 一个 PR 只做一件事，便于审查
- 说明改动动机、影响范围、验证方式
- 涉及行为变更的，同步更新 README
