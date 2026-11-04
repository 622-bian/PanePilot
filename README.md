# PanePilot · 窗领航

A Windows hotkey helper that preloads one Microsoft Edge ChatGPT web-app window and places new windows on an empty monitor first.

按 **Ctrl + Alt + G**，取用提前加载的 ChatGPT Edge 网页应用窗口；有空闲显示器时，优先在那里最大化。

这是一个早期原型，面向 Windows x64 和 **Edge 安装的 ChatGPT 网页应用（PWA）**。不适用于 ChatGPT 原生桌面客户端，不是 OpenAI 或 Microsoft 官方项目。

## 当前功能

| 情况 | 行为 |
| --- | --- |
| 存在空闲显示器 | 在空屏最大化，保留标题栏和任务栏 |
| 多块屏幕都空闲 | 优先当前操作屏，其次主屏 |
| 没有空屏 | 在按键时的操作屏尝试显示 720×405（16:9） |
| 单屏 | 空闲时最大化，否则使用 16:9 窗口 |
| 备用窗口尚未就绪 | 普通新建，应用相同布局规则 |

- 最多准备一个备用窗口，取用后补充。
- 托盘支持新建、暂停/恢复预加载和退出。
- 只在已有 ChatGPT PWA 窗口时预加载；退出时清理未交付的隐藏备用窗口。
- 不调整已打开的用户窗口，不修改 Windows 全局窗口尺寸或 Edge 安全设置。
- 当前没有自动分屏、空白矩形搜索或活跃度评分。

## 快速开始

要求：Windows 10/11 x64、Microsoft Edge、.NET Framework 4.x（包含桌面 UI Automation 程序集）、Windows PowerShell。先在 Edge 中把 `https://chatgpt.com/` 安装为应用，并创建它的桌面快捷方式。

在仓库根目录打开 PowerShell：

```powershell
.\scripts\Build.ps1
.\scripts\Configure.ps1 -ShortcutPath "$([Environment]::GetFolderPath('Desktop'))\ChatGPT.lnk"
.\scripts\Test.ps1
Start-Process .\dist\PanePilot.exe
```

快捷方式名称可能不同，请替换为实际 `.lnk` 路径。它必须是 Edge 网页应用快捷方式，而不是普通网页链接或原生 ChatGPT 客户端。

配置脚本只读取快捷方式，生成 `dist/config.json`，不会修改浏览器或登录状态；已有配置需要明确加 `-Force` 才会覆盖。该文件包含个人安装路径和浏览器标识，已被 Git 忽略。手工配置字段见 [config.example.json](config.example.json)，示例中的占位值不能直接运行。

打开一个 ChatGPT PWA 窗口，等待托盘提示备用窗口就绪，再按 **Ctrl+Alt+G**。已有程序占用此快捷键时，需要先退出冲突程序。旧版个人原型和公开版不能同时注册同一个快捷键。

程序不会自动设置开机启动。需要时可将 `dist/PanePilot.exe` 的快捷方式手动放入自己的 Windows 启动文件夹；请保留整个 `dist` 目录及其配置。

## 命令与本地文件

```powershell
Start-Process .\dist\PanePilot.exe -ArgumentList '--take'   # 取用/新建
Start-Process .\dist\PanePilot.exe -ArgumentList '--once'   # 普通新建，不驻留
Start-Process .\dist\PanePilot.exe -ArgumentList '--stop'   # 停止公开版助手
$check = Start-Process .\dist\PanePilot.exe -ArgumentList '--check-config' -Wait -PassThru
$check.ExitCode  # 0 表示本地配置结构和路径检查通过，1 表示失败
```

`--check-config` 不会打开浏览器；它不能证明浏览器中的应用仍存在或页面已就绪。

运行数据仅保存在可执行文件旁：`config.json`、`launcher.log`、`preload-status.json`。不要提交这些文件。公开源码不包含作者的浏览器配置、聊天内容、旧原型或运行日志。

## 空屏定义与限制

空屏指当前桌面上，显示器工作区域没有普通可见、未最小化的应用窗口。桌面、任务栏、一般工具浮窗、隐藏备用窗口、其他虚拟桌面的 cloaked 窗口不算占用；跨屏窗口可能让两块屏幕都算占用。检测不完整时不会宣称有空屏。

按键时检测一次，交付前再检查一次。坐标使用虚拟桌面中的物理像素，支持负坐标及不同缩放比例。复制显示的重合区域不当作独立空屏。

预加载仍有这些限制：

- 需要额外约一个网页窗口的内存；冷启动、网络延迟和 Edge 休眠仍会影响速度。
- 准备阶段用接近透明、鼠标穿透的窗口等待输入框就绪，再隐藏；窗口创建到接管之间可能短暂闪现。
- 通过新建窗口集合、PWA 身份和随机 URL 标记确认备用窗口。手工同时创建同一 PWA 可能与准备阶段竞争；识别超时会暂停预加载，普通新建仍可使用。
- 当前“全屏”是最大化，不是 F11。非标准桌面组件可能影响空屏判断；检测后用户又移动窗口的短暂竞争无法完全消除。
- Edge 可能记住该 PWA 的尺寸，影响手动打开同类窗口时的初始大小。
- 高 DPI 下，Edge 的最小窗口宽度可能阻止精确应用 720×405，触发布局兜底。当前不能保证所有缩放比例下都保持严格 16:9，详见验证指南中的已知限制。

## 验证与开发

```powershell
.\scripts\Test.ps1          # 规则、坐标和配置测试；不操作用户窗口
.\scripts\Test.ps1 -Native  # 只编译可选测试，不自动启动
Start-Process .\dist\tests\NativeLayoutSmoke.exe
```

原生测试只移动自己创建的临时空白窗口，结果写入 `dist/tests/native-layout-result.txt`。实际桌面验收、证据范围和失败判据见 [验证指南](docs/VALIDATION.md)。

| 模块 | 职责 |
| --- | --- |
| `src/ChatGPT-FastHotkey.cs` | 入口、全局快捷键、托盘和启动请求 |
| `src/AppSettings.cs` | 本地配置校验与启动参数 |
| `src/PreloadPool.cs` | 单个备用窗口的所有权和状态管理 |
| `src/WindowSupport.cs` | PWA 识别、窗口外观、UI Automation 就绪检查 |
| `src/WindowLayout.cs` | 桌面快照、布局规则和窗口放置 |

布局检测只读取窗口元数据；候选 PWA 就绪检测读取页面地址和输入框是否存在，不读取或记录聊天文本。没有遥测上传。Edge 本身访问 ChatGPT 服务不属于本程序的本地日志机制。

## 后续方向

先完善空屏识别，再考虑屏幕内空白区域搜索、可撤销的左右分屏、资料屏保护和可配置优先级。低交互不代表可以覆盖：长期展示资料的副屏可能仍在使用。

欢迎报告问题或提交小范围 PR，见 [贡献说明](CONTRIBUTING.md)。MIT License，见 [LICENSE](LICENSE)。
