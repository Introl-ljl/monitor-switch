<div align="center">

# MonitorSwitch

**Save your monitor setup as a profile. Switch it back with one click.**

A lightweight Windows tray utility that remembers your display arrangement, resolution,
refresh rate, orientation and HDR state — and restores the whole setup instantly.

[![Build](https://github.com/Introl-ljl/monitor-switch/actions/workflows/build.yml/badge.svg)](https://github.com/Introl-ljl/monitor-switch/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/Introl-ljl/monitor-switch)](https://github.com/Introl-ljl/monitor-switch/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)](https://github.com/Introl-ljl/monitor-switch)
[![.NET](https://img.shields.io/badge/.NET-8.0--WPF-512BD4)](https://dotnet.microsoft.com/)

[**Download**](https://github.com/Introl-ljl/monitor-switch/releases/latest) · [Screenshots](#screenshots) · [中文文档](README.zh-CN.md)

</div>

---

If you work with more than one monitor, you probably cycle through the same few setups:
laptop-only, dual-screen at the desk, one screen rotated for reading, projector mode.
`Win+P` changes the topology but forgets resolution, refresh rate and arrangement —
so you end up reopening Windows Settings every time.

MonitorSwitch stores the entire configuration as a **profile**. Click it in the tray,
or hit a global hotkey, and everything comes back at once.

## Features

- **Profiles** — capture the current arrangement, resolution, refresh rate, orientation,
  HDR and enable state as one named snapshot.
- **Draft mode** — changes on the Displays page are staged and only applied when you press
  *Apply*. No more screen flicker while you adjust several settings in a row.
- **15-second revert guard** — like Windows: after applying, a confirmation bar appears at
  the bottom of the screen and rolls back automatically if you don't confirm.
- **Tray panel** — left-click the tray icon for a quick switcher with mini layout previews,
  plus projector shortcuts (PC only / Duplicate / Extend / 2nd only). The panel is draggable
  and remembers where you put it.
- **Global hotkeys** — bind any profile to Ctrl/Alt/Shift/Win + a letter, digit or F1–F24.
- **CLI** — switch profiles and change resolution, refresh rate, rotation or HDR from
  scripts, with `--json` output and stable exit codes. See [docs/cli.md](docs/cli.md).
- **Light, dark or follow Windows**, in **English or 简体中文** — switchable without restart.
- **~8 MB resident** while sitting in the tray.

## Install

Download `MonitorSwitch-v*-win-x64.exe` from the [latest release](https://github.com/Introl-ljl/monitor-switch/releases/latest).

- Self-contained single file — **no .NET runtime required**, just double-click.
- The first launch extracts to a temp folder and takes a few seconds; later launches are faster.
- The binary is not code-signed, so SmartScreen may warn about an unknown publisher →
  *More info → Run anyway*.

> Requires Windows 10 1809+ or Windows 11.

## Screenshots

### Displays

Drag monitors to rearrange (with edge snapping and overlap prevention), then tune the
selected display below.

![Displays page](docs/display.png)

> Every change is staged as a draft. The bar at the top appears until you press **Apply** —
> this keeps the screen from flickering while you line up several settings.

### Profiles

![Profiles page](docs/profiles.png)

### Tray panel

![Tray panel](docs/tray.png)

### Settings

![Settings page](docs/settings.png)

## Usage

1. Set your monitors the way you want (Windows Settings, or this app's Displays page).
2. Go to **Profiles** → **New from current**.
3. Switch back any time from the tray panel or your global hotkey.

### Command line

```powershell
MonitorSwitch.exe              # normal launch
MonitorSwitch.exe --minimized  # tray only, no main window
```

Profiles and display parameters can also be driven entirely from the command line —
handy for scripts, hotkey launchers and window managers:

```powershell
MonitorSwitch.exe list                 # list profiles, "*" marks the active one
MonitorSwitch.exe apply work           # switch to the "work" profile
MonitorSwitch.exe save gaming          # save the current setup as a profile
MonitorSwitch.exe displays             # show displays and their parameters
MonitorSwitch.exe set 1 refresh 144    # set display #1 to 144 Hz right now
MonitorSwitch.exe set all hdr on       # turn HDR on for every display
MonitorSwitch.exe topology clone       # projector shortcuts: internal|clone|extend|external
```

Commands are relayed to the running tray app (keeping hotkeys, tray menu and the
15-second revert guard in sync); when the app is not running they execute directly.
Add `--json` for machine-readable output. Full reference: [docs/cli.md](docs/cli.md).

### Where data lives

`%LOCALAPPDATA%\MonitorSwitch\`

| File | Contents |
|---|---|
| `profiles.json` | saved profiles |
| `settings.json` | app settings |
| `log.txt` | runtime log (cleared above 256 KB) |

A corrupted config file is backed up to `*.corrupt-<timestamp>` and salvaged entry by entry —
it is never silently wiped.

## Build

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0):

```powershell
dotnet build src/MonitorSwitch/MonitorSwitch.csproj -c Release
dotnet run --project src/MonitorSwitch/MonitorSwitch.csproj
```

Produce the same single-file binary that ships in releases (or run `scripts/publish.ps1`):

```powershell
dotnet publish src/MonitorSwitch/MonitorSwitch.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -o publish
```

## How it works

Almost all of the difficulty in this project comes from quirks in the Windows display
subsystem. A few notes worth keeping, since they are easy to get wrong.

### CCD API

`QueryDisplayConfig` / `SetDisplayConfig` / `DisplayConfigGetDeviceInfo` handle topology,
source and target modes, timings and HDR (`SET_ADVANCED_COLOR_STATE`).
`EnumDisplaySettingsExW` enumerates supported resolutions and refresh rates per display;
`EnumDisplayDevicesW` supplies adapter and monitor friendly names.

### Adaptive struct stride

`DISPLAYCONFIG_MODE_INFO` has different sizes across Windows versions (measured **64 bytes**
on Windows 11 here; `pixelRate` and `hSyncFreq` overlap in the first dword). The app works on
raw byte buffers with runtime stride detection so it behaves on both old and new builds.

### Rotation and source modes

A CCD source mode is **always expressed in the panel's native orientation**. At 90°/270° the
desktop width and height swap — a portrait 2560×1600 panel has a source mode of 2560×1600
while occupying 1600×2560 on the desktop. The app stores both panel-oriented
(`SourceWidth/Height`) and desktop-oriented (`DesktopWidth/Height`) values and converts
between them before applying.

### Target timing validity

A stored target timing (`pixelRate` / `vSync` / `activeSize`) is only reused when it still
matches what the system currently selected for that path. Otherwise the system picks a new
timing for the requested resolution/refresh rate. This is the root cause of the classic
"resolution change silently does nothing" bug.

### Keeping the tray footprint small

Resident private working set is about **8 MB**. Getting there required a few things:

| Problem | Cause | Fix |
|---|---|---|
| 128 MB resident | WPF's `HwndSource` is a render host; creating one initializes the D3D pipeline and loads GPU driver modules (~126 MB) | Replaced with a plain Win32 message window (`RegisterClassEx` + `HWND_MESSAGE`) — a tray app needs no rendering at all |
| +24 MB | WPF-UI resource dictionaries instantiate the whole control template set | Load lazily, only when a window is first shown |
| +5.7 MB | Referencing `ui:FontIcon` in `App.xaml` pulls `Wpf.Ui.dll` in at startup | Register that style lazily with the rest of the resources |
| +11 MB | `System.Drawing.Common` drags in `windows.storage.dll` and friends | Load the tray icon with `LoadImage` instead |

Note that `Shell_NotifyIcon` itself loads `windows.storage.dll` (8.7 MB) — that is unavoidable
for a tray icon.

Once the main window has been shown, memory does not return to 8 MB: WPF's D3D pipeline is
process-wide state and cannot be unloaded.

### UI rendering

- **No ClearType.** Sub-pixel rendering paints colored fringes on glyph edges. On dark
  backgrounds with small CJK text this is very visible — measured up to +75 green-channel
  deviation per pixel, with 36.8% of text pixels showing a color cast. Switching to grayscale
  antialiasing brings that to 0%.
- **Accent color is overridden.** If the system accent is a desaturated color (e.g. the teal-gray
  `#69797E`), every brush WPF-UI derives from it tints the UI. Note that
  `ApplicationAccentColorManager.Apply` does **not** update `AccentButtonBackground`, so that
  resource must be written directly.

## Known limitations

- **Scale percentage is read-only.** Windows exposes no public per-monitor DPI API. The app
  reads `HKCU\Control Panel\Desktop\PerMonitorSettings\<monitorId>\DpiValue` (`0` = 100%,
  `+1` = 125%, `-1` = 75%). Use Windows Settings to change it.
- **GPU name** comes from the GDI adapter friendly name. CCD's `DISPLAYCONFIG_ADAPTER_NAME`
  only returns a device path (`\\?\PCI#...`).
- **Displays without an EDID name** (some internal panels, capture devices) show up as
  "Built-in display", depending on what the driver reports.
- **Projector shortcuts** rely on topology records the system has already saved; the first
  use of a given mode may require configuring it once in Windows Settings.
- Memory does not drop back to ~8 MB after the main window has been opened once (see above).

## Contributing

Issues and pull requests are welcome — please read [CONTRIBUTING.md](CONTRIBUTING.md) first.

Changes that touch display configuration **must be verified on real multi-monitor hardware**.
This class of bug tends to look successful while silently doing nothing.

## License

[MIT](LICENSE)

## References

- [davide136/monswitch](https://github.com/davide136/monswitch)
- [terrymacdonald/DisplayMagician](https://github.com/terrymacdonald/DisplayMagician)
- [CCD APIs — Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/ccd-apis)
- [WinUI 3 / Fluent Design](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
- [WPF-UI (lepoco)](https://wpfui.lepo.co/)
