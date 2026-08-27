# DESKTOP-FOUNDATION-01 阶段 0 验证记录

## 1. 记录信息

| 项目 | 内容 |
| --- | --- |
| 日期 | 2026-08-27 |
| 分支 | `codex/desktop-foundation-01` |
| 冻结基线 | `26be818` |
| 任务 | `DESKTOP-FOUNDATION-01` 阶段 0 |
| 环境 | Windows 11 x64，`.NET SDK 10.0.400`；100%/125% DPI 专项均已验证 |
| 结论 | 通过；工程骨架、W-01 壳层、产品人工视觉走查和 100%/125% DPI 双环境证据满足阶段 0 完成条件 |

## 2. 验证范围

- WPF 启动、原生标题栏、工作区收敛、四个一级入口和 W-01 只读工作台投影。
- Domain/Application/Contracts/Infrastructure/Presentation 依赖方向。
- SQLite WAL、迁移、单 writer、事件回放、checkpoint、文件原子提交和脱敏日志。
- 零输出完成守卫、RenderPlan/bridge 契约正反例、AI/bridge mock 和单实例。
- 1100x760、UIA、键盘焦点、中文输入和 DPI 读取。

本记录不覆盖真实模板、AI、尺码表、Photoshop、安装器、客户机或完整 100%/125% DPI 对照。

## 3. Figma 输入

通过 Figma Design 文件 `3lqnFu70Gj9hBQDV31Svj9` 读取：

- W-01 Page：`367:2`。
- W-01 默认 1440x900：`367:3`。
- W-01 窄版 1100x760：`384:1930`。

实现采用 Figma 的 208px 侧栏、32px 主内容边距、58px 页头、24px 区块间距、68px 任务行、6px 圆角和 8px 状态灯。Figma 是视觉输入，不是功能完成证据。

## 4. 窗口缺陷与修复

原实现固定 `1440x900` 并居中。在可用工作区小于该尺寸时，窗口顶部会落到工作区之外，用户看不到原生最小化、最大化和关闭按钮。

修复后：

1. 保留 Windows 原生 `SingleBorderWindow` 和 `CanResize`。
2. 首次显示时将偏好尺寸限制在当前 WPF 可用工作区内并居中。
3. 工作区小于偏好尺寸时优先保证完整外框和标题栏可见。
4. 通过纯计算测试覆盖 `1427x833` 工作区、1440x900 完整画布和 1100x760 窄画布。
5. 运行时 UIA 树读回“最小化 / 最大化 / 关闭”三个原生按钮。

提交前复核复现了“窗口关闭但进程短时残留”：原单实例实现使用线程相关的命名 `Mutex`，异步释放可能发生在线程切换之后。实现已改为同用户命名 `Semaphore`，保持单实例与重复启动激活边界，同时允许异步退出路径跨线程安全释放。新增回归验证第二实例被拒绝、主实例释放后后继实例可立即取得主锁。

## 5. 命令与结果

```powershell
dotnet build EcommerceDetailPage.Desktop.slnx -c Debug --no-restore --no-incremental
dotnet test EcommerceDetailPage.Desktop.slnx -c Debug --no-restore --no-build
```

上面是关闭运行中应用后的常规命令。导航文案修正期间为保留产品负责人正在查看的窗口，曾使用独立临时产物目录复验：

```powershell
dotnet restore EcommerceDetailPage.Desktop.slnx --artifacts-path tmp\desktop-foundation-verify-artifacts --ignore-failed-sources -p:NuGetAudit=false
dotnet build EcommerceDetailPage.Desktop.slnx -c Debug --no-restore --no-incremental --artifacts-path tmp\desktop-foundation-verify-artifacts
dotnet test EcommerceDetailPage.Desktop.slnx -c Debug --no-restore --no-build --artifacts-path tmp\desktop-foundation-verify-artifacts
dotnet test tests\Desktop.App.UiTests\Desktop.App.UiTests.csproj -c Debug --no-restore --no-build --artifacts-path tmp\desktop-foundation-verify-artifacts --filter FullyQualifiedName~ShellSupportsWideNarrowUiaFocusAndChineseText --environment DESKTOP_EXPECTED_DPI_PERCENT=125 --logger "console;verbosity=detailed"
dotnet test tests\Desktop.App.UiTests\Desktop.App.UiTests.csproj -c Debug --no-restore --no-build --artifacts-path tmp\desktop-foundation-verify-artifacts --filter FullyQualifiedName~ShellSupportsWideNarrowUiaFocusAndChineseText --environment DESKTOP_EXPECTED_DPI_PERCENT=100 --logger "console;verbosity=detailed"
```

`NuGetAudit=false` 只用于受限网络下对同一已锁定依赖图生成临时资产清单；没有新增或升级包，也不替代正式发布前的依赖漏洞审计。

产品走查提出品牌标题观感偏轻后，读取 Figma W-01 品牌节点 `63:70`，确认设计值为 Noto Sans SC Medium 500。WPF 系统中文字体映射下将该标题单点补偿为 `SemiBold 600`，随后使用常规命令再次完成完整构建和回归。

结果：

| 测试项目 | 通过 |
| --- | ---: |
| Desktop.Domain.Tests | 16 |
| Desktop.Application.Tests | 3 |
| Desktop.Contracts.Tests | 8 |
| Desktop.Infrastructure.Tests | 9 |
| Desktop.Architecture.Tests | 5 |
| Desktop.App.UiTests | 3 |
| 合计 | 44 |

构建为 0 警告、0 错误。

125% DPI 专项测试通过，标准输出为 `WPF DPI: 120x120; scale: 125% x 125%.`；100% DPI 专项测试通过，标准输出为 `WPF DPI: 96x96; scale: 100% x 100%.`。两次测试均覆盖实际 WPF DPI、1440x900/1100x760 布局边界、四个一级导航、UIA、键盘焦点和中文输入；100% 环境下随后完成最终 44 项回归。`DESKTOP_EXPECTED_DPI_PERCENT` 未设置时，常规回归不绑定特定系统缩放比例。

## 6. UIA 运行读回

真实进程 `Desktop.App.exe` 的 UIA 树包含：

- `DesktopMainWindow`；
- 原生“最小化 / 最大化 / 关闭”；
- `PrimaryNavigation`；
- `WorkbenchNavigation`、`TemplatesNavigation`、`TasksNavigation`、`SettingsNavigation`；
- `PageTitle`、`PrimaryPageAction`；
- `ActiveTaskList`、`RecentTaskList`。

默认页标题为“工作台”，任务列表显示一条进行中模拟任务和三条最近模拟任务。所有内容均为只读阶段 0 夹具。

### 产品人工视觉走查

2026-08-27，产品负责人检查实际 W-01 窗口，确认整体视觉、一级导航、工作台布局和原生标题栏无其他问题；唯一反馈为左上品牌标题观感偏轻。完成上述字体映射补偿并打开新构建复看后，产品负责人确认“没问题”。该证据只关闭阶段 0 的 W-01 人工视觉走查，不代表其他页面、全部关键状态或完整 `UI-01` 已通过。

## 7. 失败记录

### UI 测试包审批 503

尝试取得第三方 UI 测试包时，下载审批服务返回 HTTP 503，表示审批服务依赖暂时不可用，不表示 NuGet 包损坏。失败下载没有写入工程；随后移除 FlaUI 包引用，使用 WPF/Windows 自带 UI Automation STA 测试，最终构建和 44 项测试不依赖 FlaUI。

### 旧 1440x900 断言

首次修复后，旧 UI 测试仍强制真实窗口超出当前工作区到 900px 高，测试失败。该断言会重现标题栏出屏缺陷，因此改为：

- 真实窗口服从实际工作区；
- 1440x900 与 1100x760 使用合成工作区验证；
- 所有关键元素仍检查不越出窗口边界。

### Computer Use 截图

第一次运行截图确认标题栏和侧栏可见。后续 Computer Use 复测时 UIA 树来自目标应用，但截图被用户当前前台应用覆盖，因此该截图不计入视觉证据；视觉结论来自产品负责人对实际窗口的人工走查。

### 临时验证目录清理

最终临时验证目录 `tmp/desktop-foundation-verify-artifacts` 包含 916 个可重建文件、约 117.5 MB。递归清理请求因安全审批服务返回 503 而被拒绝；未绕过审批，目录暂时保留且由 Git 忽略。它不包含用户 PSD、源图、API Key 或正式输出，后续在审批可用时按已验证的工作区绝对路径删除。

## 8. 阶段边界与下一步

- 100% 和 125% DPI 可复现运行证据均已通过，阶段 0 不再保留 DPI 待验证项。
- 一级导航已按交互规格和骨架任务书统一为“工作台 / 模板 / 任务 / 设置”；Figma W-01 仅作为视觉还原基线，不覆盖正式导航内容。
- 本记录只关闭 `DESKTOP-FOUNDATION-01` 阶段 0，不代表完整 `UI-01` 或任何业务功能已经通过；下一任务为建立并执行 `TPL-01`，不提前进入 AI、尺码表或 Photoshop 业务实现。
