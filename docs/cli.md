# MonitorSwitch CLI 参考

MonitorSwitch 的所有核心能力——切换配置模板、调整显示器参数——都可以在命令行中完成。
单一二进制 `MonitorSwitch.exe`，无需额外安装，适合脚本、启动器、窗口管理器与远程场景。

> 命令面以 `MonitorSwitch.exe help` 的实时输出为准（帮助文本内置于程序内），
> 本文档与之同步维护；两者若有出入，以程序输出为准。

```
MonitorSwitch.exe <命令> [参数] [--json]
```

## 双运行形态

| | 应用正在运行 | 应用未运行 |
|---|---|---|
| 执行方式 | 经命名管道中继给托盘实例 | CLI 进程内直连执行 |
| 模板列表/快捷键/托盘菜单 | 立即同步刷新 | 文件已更新，下次启动生效 |
| 15 秒还原保护 | 有（屏幕底部确认条） | 无（改动立即生效） |

中继优先：管道不可达（应用未运行）时自动回退直连，命令行为完全一致。

## 命令一览

### 配置模板

```powershell
MonitorSwitch.exe list                          # 列出模板，“*”为当前使用中
MonitorSwitch.exe list --json                   # 机器可读输出
MonitorSwitch.exe apply work                    # 按名称切换（不区分大小写，支持唯一前缀）
MonitorSwitch.exe apply 598c2cc474bd437880b44cefccf063f7   # 按 Id 切换
MonitorSwitch.exe save gaming                   # 把当前显示配置保存为模板
MonitorSwitch.exe save gaming --replace         # 同名模板存在时覆盖更新
MonitorSwitch.exe delete gaming                 # 删除模板
MonitorSwitch.exe rename gaming work            # 重命名
MonitorSwitch.exe hotkey work ctrl+alt+1        # 绑定全局快捷键
MonitorSwitch.exe hotkey work none              # 清除快捷键
```

模板选择器 `<名称|ID>` 的匹配顺序：完整 Id → 完整名称 → 唯一前缀。
前缀命中多个模板时会报错并列出全部候选。

`apply` 在当前配置已经与模板一致时直接返回“无需切换”，不会触发闪烁。

### 显示器参数

```powershell
MonitorSwitch.exe displays                      # 列出显示器及当前参数
MonitorSwitch.exe set 1 refresh 144             # 显示器 1 → 144 Hz
MonitorSwitch.exe set 1 resolution 2560x1440    # 分辨率（桌面朝向的宽 × 高）
MonitorSwitch.exe set 1 rotation 90             # 0 | 90 | 180 | 270
MonitorSwitch.exe set 1 hdr on                  # on | off
MonitorSwitch.exe set 1 position 2560,0         # 桌面坐标（物理像素，可为负）
MonitorSwitch.exe set 2 rotation 90 resolution 1080x1920    # 一条命令多组参数
MonitorSwitch.exe set all hdr on                # 所有显示器（仅 hdr / rotation 支持 all）
MonitorSwitch.exe topology extend               # internal | clone | extend | external
```

`<显示器>` 选择器：`displays` 中的序号（如 `1`）、显示器名称的唯一子串（如 `LECOOK`），
或 `all`。

说明：

- **resolution 按“应用完本条命令后的最终朝向”解释**。同一命令里同时改朝向和分辨率时，
  分辨率以新朝向下的桌面宽高为准；只改朝向不改分辨率时，面板分辨率保持不变，桌面宽高自动互换。
- 修改分辨率或刷新率后，旧的时序参数会被丢弃并交由系统重新选择（避免“改了却没生效”）；
  仅改 HDR / 位置时沿用当前时序，切换过程最平稳。
- 分辨率与刷新率会先对照 GDI 枚举的支持列表做预检，直接报错而不是闪屏后失败
  （同一命令里改了朝向时列表不可靠，跳过预检，交给 CCD 校验）。
- `topology` 依赖系统保存过的拓扑记录：某个模式首次使用可能需要先在 Windows 设置里配置一次。

### 应用设置

```powershell
MonitorSwitch.exe config list
MonitorSwitch.exe config set theme dark         # Auto | Light | Dark
MonitorSwitch.exe config set language zh-CN     # auto | en | zh-CN
MonitorSwitch.exe config set start-minimized on
MonitorSwitch.exe config set close-to-tray off
MonitorSwitch.exe config set revert-guard off   # 关闭 15 秒还原确认
MonitorSwitch.exe config set autostart on       # 开机自启（写注册表 Run 键）
```

中继模式下修改 theme / language 会立即作用于托盘应用，与设置页行为一致。

## 退出码

| 码 | 含义 |
|---|---|
| 0 | 成功（有 warning 时仍为 0，warning 输出到 stderr） |
| 1 | 执行失败（应用失败、模板不存在等；中继无响应时自动回退直连重新执行，因此超时本身不产生该退出码） |
| 2 | 用法错误（未知命令、缺参数、值非法等） |

## --json 输出

加 `--json` 后所有命令输出统一信封：

```json
{
  "ok": true,
  "exitCode": 0,
  "message": null,
  "error": null,
  "warnings": [],
  "data": [ ... ]
}
```

`data` 按命令不同：

- `list` → 模板数组：`id` / `name` / `hotkey` / `current` / `createdAt` / `modifiedAt` /
  `monitors[]`（`name` `width` `height` `refreshHz` `rotation` `hdr`）；
- `displays` → 显示器数组：`number` / `name` / `adapter` / `connectorName` / `gdiDevice` /
  `devicePath` / `x` `y` / `width` `height` / `sourceWidth` `sourceHeight` / `refreshHz` /
  `rotation` / `scalePercent` / `hdr` / `hdrSupported` / `primary` / `resolutions[]` / `refreshRates[]`；
- `config list` → 各设置项对象；
- 其余命令 → 简单的执行结果对象（如 `applied`、`created`）。

示例（PowerShell，把主显示器切到 144 Hz 并检查结果）：

```powershell
$out = MonitorSwitch.exe set 1 refresh 144 --json | ConvertFrom-Json
if (-not $out.ok) { Write-Error $out.error }
```

## 终端注意事项

`MonitorSwitch.exe` 是 GUI 程序（WinExe）。从 PowerShell、cmd、Windows Terminal
直接运行时，输出会自动附着到父控制台，行为与普通命令行工具一致。

两个已知限制：

- **mintty（Git Bash 默认终端）** 不持有 Windows 控制台，CLI 的输出拿不到。
  在 Git Bash 里请改用 `winpty MonitorSwitch.exe list`，或用 PowerShell / cmd。
- 从资源管理器双击 / 运行对话框执行时没有输出可看，只有退出码。
  排查问题可看 `%LOCALAPPDATA%\MonitorSwitch\log.txt`。

## 设计说明

- 中继通道为按会话命名的管道 `monitor-switch-cli-<sessionId>`，帧格式为
  4 字节小端长度 + UTF-8 JSON；单请求处理窗口 90 秒，客户端等响应 60 秒。
- 中继执行时，改模板/设置的操作会派发到 UI 线程（与界面共用同一份内存态并发事件），
  显示器操作走线程池（`ApplyGuard` 内部自行调度 UI）。
- 直连执行时进程内没有托盘与窗口，只加载设置与模板存储；还原保护条依赖 UI，
  因此该形态下不启用，结果里的 warning 会明确提示。
- CLI 入口在单实例检查之前截获，`MonitorSwitch.exe list` 之类的调用不会触发
  “唤起已有实例”或弹出主窗口。
