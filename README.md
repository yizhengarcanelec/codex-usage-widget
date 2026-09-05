# Codex Usage Widget

一个轻量、无边框的 Windows 桌面小组件，用于实时查看本机 Codex 的今日 Token、5 小时额度和周额度。v0.6.0 采用受 Apple 设计语言启发的浅色/深色界面、圆角卡片与克制动效。所有数据只从本机 Codex 会话记录读取，不需要 API Key，也不会上传会话内容。

- 今日 Token 总量
- Input / Output / Cached Token 明细
- 5 小时额度与周额度的剩余比例、已用比例和独立重置时间
- 完整面板、窄面板、仅今日 Token 面板和双环圆形额度球
- 自动/浅色/深色外观、五种强调色、CN/EN 中英文切换和窗口置顶
- 约 200ms 的尺寸与颜色过渡，并遵循 Windows“显示动画”设置
- 系统托盘后台运行，并记住用户选择的关闭行为

<table>
  <tr>
    <th>完整面板</th>
    <th>圆形额度球（界面缩小后）</th>
  </tr>
  <tr>
    <td><img src="docs/screenshot.png" alt="Codex Usage Widget 双额度完整面板" width="354"></td>
    <td align="center"><img src="docs/compact-mode.png" alt="Codex Usage Widget 双环圆形额度球" width="126"></td>
  </tr>
</table>

## Download for Windows

**[Download Codex Usage Widget v0.6.0](https://github.com/yizhengarcanelec/codex-usage-widget/raw/main/download/Codex-Usage-Widget-v0.6.0-win-portable.zip)**

下载 ZIP 后解压，双击 `GPTUsageWidget.exe` 即可。便携包内含程序和使用说明，无需安装。

SHA-256：`39E736C3CBD02C0C788B63A34C608BA0104951F1736E7AFFE0A5ED4AC402491F`

## v0.6.0 更新

- 全面改为受 Apple 设计语言启发的视觉：更轻的层级、圆角卡片、胶囊控件和清晰留白。
- 新增自动、浅色、深色三种外观；自动模式会跟随 Windows 应用颜色设置。
- 五种主题改为统一的强调色体系：Apple Green、California Blue、Orchid Purple、Watermelon Pink、Sunset Orange。
- 外观、强调色和紧凑模式切换加入克制的缓动过渡；系统关闭动画效果时会自动停用动效。
- 圆形额度球沿用双环设计，悬停时显示重置日期；展开箭头改为无底色、低存在感的细线控件，不再遮挡圆环。

## v0.5.0 更新

- 同时识别 Codex 的 5 小时额度窗口和周额度窗口，分别显示剩余、已用和重置时间。
- 圆形模式升级为双环显示：内环为 5 小时额度，外环为周额度。
- 圆形中心显示两种额度中较低的剩余比例，便于快速判断当前更紧张的额度。
- 旧日志暂时缺少某一种额度记录时，对应位置显示 `--%`，不会使用 Token 数量推算额度。

## 功能与操作

从上方链接、GitHub Actions 构建产物或 Releases 下载便携包，解压后双击 `GPTUsageWidget.exe`。

- 每 5 秒自动刷新。
- 拖动窗口空白区域可移动。
- 拖动任意边缘或四个角可分别调整宽、高；完整面板大小固定为 360×286（系统缩放前的逻辑尺寸）。
- 缩小时会依次隐藏次要模块，最小矩形只显示今日 Token；宽、高均到达矩形下限后继续向内拖动才会变为圆形额度球。
- 圆球大小可在 84×84 到 132×132 之间等比例缩放；内环显示 5 小时额度，外环显示周额度，中心显示两者中更紧张的剩余比例。
- 圆球悬停时进度环轻微提亮，并分别显示 5 小时与周额度的重置日期。
- 双击窗口或使用右键菜单，可在圆球与标准面板之间快速切换。
- 支持 CN/EN 中英文切换；外观模式、强调色和语言选择都会保存。
- 顶部外观按钮在自动、浅色、深色之间循环；右键菜单可直接选择指定外观和强调色。
- 右键窗口可立即刷新、切换置顶、切换尺寸、最小化或退出，菜单项均为中文。
- 首次点击 `X` 会询问“彻底结束程序”或“最小化至后台”；选择会被记住，之后可在右键的“关闭行为”中修改。
- 选择“最小化至后台”后，窗口和任务栏图标都会隐藏，程序继续刷新并驻留在 Windows 系统托盘；双击托盘图标可恢复窗口。
- 重复启动不会产生多个窗口。

也可以使用 `GPTUsageWidget.exe --compact` 直接以圆形额度球启动。

## 额度显示说明

- 完整面板中的“5 小时剩余”和“本周剩余”是两个彼此独立的额度窗口。
- 圆形额度球的内环代表 5 小时额度，外环代表周额度；中心数字取两者中较低的剩余比例。
- 鼠标悬停圆球后，会显示两个额度窗口各自的重置日期。
- 额度百分比来自 Codex 写入本地会话记录的限额数据，不代表可换算成固定 Token 数量的余额。

## 数据与隐私

程序只读取当前用户目录下的 Codex 本地会话记录：

- `%USERPROFILE%\.codex\sessions`
- `%USERPROFILE%\.codex\archived_sessions`

程序不需要 API Key，不联网，也不会上传会话内容。今日 Token 按本地日历日汇总；如果本机缺少当天较早的记录，状态栏会显示 `partial history`。

程序不会使用今日 Token 数量反推额度。如果本机缺少某个额度窗口的最新记录，对应位置会显示 `--%`。

## 兼容性

- Windows 10 / Windows 11
- x64 或 x86（AnyCPU）
- 使用系统自带的 .NET Framework 运行，无需安装额外依赖

## 本地构建

在 Windows PowerShell 中运行：

```powershell
.\build.ps1
```

生成文件位于 `release\GPTUsageWidget.exe`。

## GitHub Actions

仓库内置 Windows 构建工作流。Push、Pull Request 或手动运行 workflow 后，会生成可下载的 `GPT-Usage-Widget-win-portable` 构建产物。

## 技术说明

- C# / Windows Forms
- 单文件 WinExe
- 无第三方运行时依赖
- 本地只读扫描，活动会话文件使用共享读取
- 缩放时强制完整重绘并裁切窗口区域，避免无边框窗口残影
