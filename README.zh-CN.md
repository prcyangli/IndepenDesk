# IndepenDesk

[English](README.md) | [Türkçe](README.tr.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Italiano](README.it.md) | [Русский](README.ru.md) | **中文** | [日本語](README.ja.md)

**Windows 上每个显示器独立的虚拟桌面** — 相当于 macOS 的"显示器具有单独的空间"功能。

## 问题

Windows 的虚拟桌面是全局的：按 `Win+Ctrl+←/→` 会**同时切换所有显示器**。而在 macOS 上，每个屏幕都有自己的空间（Spaces），只有光标所在的屏幕会切换。IndepenDesk 把这种体验带到了 Windows。

## 功能

- 🖥 **每个显示器独立的桌面** — 切换只影响鼠标所在的显示器，其他显示器保持不变。
- ➕ **动态且独立的桌面数量** — 每个显示器从 1 个桌面开始；在末尾向右滑动会新建桌面（每个显示器最多 9 个）。末尾的空桌面会自动删除。
- 🔢 **全局编号** — 编号跨显示器连续（显示器 1：1-2-3，显示器 2：4-5-6）。`Ctrl+Alt+数字` 可直接跳转。
- 🎞 **macOS 风格的滑动动画**，限制在显示器内部。
- 🗂 **总览** (`Ctrl+Alt+↑`) — 类似 Mission Control 的网格视图，支持拖放：在桌面和显示器之间移动窗口，将整个桌面移到另一台显示器，右键移动菜单。
- 🌍 **8 种语言** — 自动检测，可在托盘菜单中更改。
- 🔄 通过 GitHub Releases **检查更新**。
- 🛟 **防崩溃** — 隐藏的窗口会记录到磁盘，下次启动时自动恢复。

## 安装

1. 如未安装，请先安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)。
2. 从 [Releases](https://github.com/harungecit/IndepenDesk/releases) 下载 `IndepenDesk-vX.Y.Z-win-x64.zip`，解压后运行 `IndepenDesk.exe`。
3. 程序驻留在系统托盘。开机自启：`Win+R` → `shell:startup` → 放入快捷方式。

## 快捷键

| 快捷键 | 功能 |
|---|---|
| `Ctrl+Alt+←` / `Ctrl+Alt+→` | 切换鼠标所在显示器的上一个 / 下一个桌面（在末尾按 `→` 新建） |
| `Ctrl+Alt+↑` | 打开/关闭总览 |
| `Ctrl+Alt+1..9` | 跳转到对应全局编号的桌面 |
| `Ctrl+Alt+Shift+←/→` | 将当前窗口移到相邻桌面并跟随 |

每次切换时屏幕会显示指示器（"桌面 4 — 显示器 2 • 2/3"）。托盘菜单中的**使用说明**包含动画手势指南。

## 触摸板（macOS 风格滑动）

默认情况下四指滑动会触发 Windows 的**全局**桌面切换。覆盖方法：

1. 打开 **设置 → 蓝牙和其他设备 → 触摸板 → 高级手势**。
2. 为四指滑动的每个方向选择**自定义快捷方式**并录制：
   - 向左滑动 → `Ctrl+Alt+←`，向右滑动 → `Ctrl+Alt+→`，向上滑动 → `Ctrl+Alt+↑`
3. 录制自定义快捷方式后会自动替换 Windows 默认行为 — 只有光标所在的显示器会切换。

## 工作原理

IndepenDesk 不使用（也无法修改）Windows 的全局虚拟桌面系统。它为每个显示器维护窗口集合，切换时只隐藏/显示该显示器的窗口（`ShowWindow`）。隐藏的窗口也会从任务栏和 Alt-Tab 中消失，因此感觉就像真正的桌面切换。新窗口会自动归入其所在显示器的当前桌面。

## 已知限制

- 以管理员权限运行的程序窗口无法隐藏，除非 IndepenDesk 本身以管理员身份运行。
- 系统原生的 `Win+Ctrl+←/→` 仍会触发全局切换 — 不使用即可。
- `Ctrl+Alt+←/→` 可能与 Intel 显卡的"旋转屏幕"热键冲突；如有需要请在 Intel 设置中禁用。
- Windows 11 任务栏的右键菜单无法被第三方扩展；请使用总览中的右键菜单。

## 构建

```
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true -o publish
```

需要 .NET 8 SDK。推送 `v*` 标签后，GitHub Actions 会自动构建发布版本。

## 许可证

[MIT](LICENSE)
