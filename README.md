# PanePilot · 窗领航

A Windows hotkey helper that preloads an Edge ChatGPT web-app window, finds free screen space, and supports side-by-side tiling with one-step undo.

按 **Ctrl + Alt + G**，取用提前加载的 ChatGPT Edge 网页应用窗口；有空闲显示器时，优先在那里最大化。

这是一个早期原型，面向 Windows x64 和 **Edge 安装的 ChatGPT 网页应用（PWA）**。不适用于 ChatGPT 原生桌面客户端，不是 OpenAI 或 Microsoft 官方项目。

## 当前功能

| 情况 | 行为 |
| --- | --- |
| 存在空闲显示器 | 在空屏最大化，保留标题栏和任务栏 |
| 多块屏幕都空闲 | 优先当前操作屏，其次主屏 |
| 没有空屏，但有至少 720×405 的空白区域 | 优先在操作屏找空白区域，其次其他屏幕 |
| 没有足够空白区域 | 将按键时的窗口放到左半屏，新 GPT 放到右半屏 |
| 不具备安全分屏条件 | 退回普通窗口，不强行调整已有窗口 |
| 单屏 | 同样依次尝试空屏、空白区域、左右分屏 |
| 备用窗口尚未就绪 | 普通新建，应用相同布局规则 |

- 最多准备一个备用窗口，取用后补充。
- **Ctrl+Alt+Shift+G** 撤销最近一次分屏；托盘也提供撤销入口。
- 托盘支持新建、暂停/恢复预加载和退出。
- 只在已有 ChatGPT PWA 窗口时预加载；退出时清理未交付的隐藏备用窗口。
- 分屏只调整按键时的前台窗口和新 GPT，不重排其他应用，不修改 Windows 全局窗口尺寸或 Edge 安全设置。
- 当前没有活跃度评分或资料屏保护名单。

## 分屏与撤销规则

没有完整空屏时，查找不与普通窗口相交的 720×405 空白矩形；找不到才尝试左右分屏。只分屏可调整大小的普通窗口，跳过工具窗、对话框、最小化窗口和无标题栏的全屏窗口。每列至少 720 像素宽、405 像素高；竖屏或很小的屏幕会退回普通窗口。

按键时保存当前窗口身份和布局；交付前检查它没有被关闭、换成其他窗口、移动或最小化。如果加载期间切到其他应用，也会跳过分屏。应用拒绝半屏大小时撤回本次调整；拒绝调整或恢复的应用会在日志中记录，不能保证所有第三方窗口都能接受布局请求。

撤销恢复最近一次分屏前的位置、尺寸和最大化状态；新 GPT 恢复成普通窗口，**不会关闭或清空对话**。只保留一层撤销，下一次成功分屏会替换记录，助手退出或重启后记录消失。已关闭的窗口不会重新打开；显示器布局发生变化或窗口恢复失败时，保留记录供重试。撤销会覆盖分屏后手动做的窗口位置调整。

这是普通窗口的左右布局，不创建 Windows Snap 分组。`--once` 或助手未运行时的单次启动不分屏，因为进程退出后无法保留撤销记录。

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
Start-Process .\dist\PanePilot.exe -ArgumentList '--undo'   # 撤销常驻助手的最近一次分屏
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
| `src/SplitLayout.cs` | 前台窗口状态验证、分屏事务、失败回退与撤销 |

布局检测只读取窗口元数据；候选 PWA 就绪检测读取页面地址和输入框是否存在，不读取或记录聊天文本。没有遥测上传。Edge 本身访问 ChatGPT 服务不属于本程序的本地日志机制。

## GitHub 同步

完成代码修改和相关桌面验收后，可执行：

```powershell
.\scripts\Publish.ps1 -Message "Describe the completed change"
```

脚本按顺序构建、测试、暂存白名单内的源码与文档、检查常见凭据及个人路径、提交并推送当前分支。要求 Git 已配置身份及远端上游，并已登录 GitHub；推送失败时保留本地提交，不强推。GitHub Actions 随每次推送自动构建和测试，发布完成后还应检查对应运行结果。

这是修改完成时执行的同步流程，不是保存文件就上传的后台监听器；自动检查不能替代人工 diff 审查。项目的 agent 协作规则已要求完成修改后自动执行此流程并检查 CI。

## 后续方向

继续完善 DPI/应用最小尺寸兼容、资料屏保护和可配置优先级。低交互不代表可以覆盖：长期展示资料的副屏可能仍在使用。

欢迎报告问题或提交小范围 PR，见 [贡献说明](CONTRIBUTING.md)。MIT License，见 [LICENSE](LICENSE)。
