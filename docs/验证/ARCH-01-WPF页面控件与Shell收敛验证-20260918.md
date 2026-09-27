# ARCH-01 WPF 页面控件与 Shell 收敛验证（2026-09-18）

## 范围

本轮在 B-03 页面状态和 `AiAnalysisViewModel` 协调基础上完成 WPF 结构抽取。只移动页面组合、局部交互和事件处理，不改变业务规则、Application 合同或持久化事实。

新增页面控件：

- `AiAnalysisPage.xaml`：AI 分析页和款式检查页。
- `BatchInputPage.xaml`：批次输入、PSD 选择和拖放扫描入口。
- `ResultReviewPage.xaml`：结果总览和结果详情。
- `TemplateWorkspacePage.xaml`：模板库、无限画布、参考学习和标定画布；拖放、平移、缩放、键盘和文件选择事件随控件 code-behind 迁移。
- `SettingsPage.xaml`：设置页和凭据/上传授权卡；密码输入事件随控件迁移，Shell 只保留凭据清空生命周期入口。

新增局部弹层：

- `AiCandidatePreviewDialog.xaml`
- `AiUploadConsentDialog.xaml`
- `SizeTableEditorDialog.xaml`
- `BatchPageSelectionDialog.xaml`
- `TemplateConfirmationDialog.xaml`
- `DuplicateReviewDialog.xaml`

`MainWindow.xaml` 现在只组合导航壳、页面控件、窗口级资源和生命周期级入口；`MainWindow.xaml.cs` 只保留窗口启动/尺寸边界、页面 ViewModel 生命周期和凭据输入清理事件。模板快捷键、模板卡片移动、模板确认弹层、页面拖放、画布、扫描和密码输入均已归属对应页面。

## 兼容边界

- `AiAnalysisPage`、AI 两个弹层以 `AiAnalysisViewModel` 为 DataContext；批次、结果、模板和设置页面分别以自己的页面 ViewModel 为 DataContext。
- 所有页面 XAML/code-behind 均不直接引用 `Shell` 或 `MainWindowViewModel`。模板页面的画布、拖放、标定、参考学习和排序事件也统一经 `TemplateWorkspacePageViewModel` 进入。
- `ShellPageViewModel` 仍作为短期兼容适配器，向页面提供尚未完成真实退出门验证的跨页状态/通知；其 `ICustomTypeDescriptor` 优先解析页面显式属性，再补齐 Shell 属性。
- `MainWindowViewModel` 的公开绑定别名、AI 工作流门面、RenderPlan、AI schema、SQLite migration/checkpoint/manifest、Photoshop/UXP bridge、颜色规划和源媒体边界均保持不变。
- 旧别名、Shell 适配器和 timer 镜像未删除。删除条件是调用方搜索、自动化重启、旧数据读取、真实 AI/Photoshop 及 SIZE-S7/RENDER-01 联验收证据齐全；删除需单独变更。

## 规模证据

| 文件 | 当前行数 |
| --- | ---: |
| `MainWindow.xaml` | 458 |
| `MainWindow.xaml.cs` | 43 |
| `AiAnalysisPage.xaml` | 710 |
| `TemplateWorkspacePage.xaml` | 1,345 |
| `BatchInputPage.xaml` | 354 |
| `ResultReviewPage.xaml` | 287 |
| `SettingsPage.xaml` | 209 |
| 六个局部弹层 XAML 合计 | 578 |

按当前工作树原主窗口约 4,249 行 XAML、约 786 行 code-behind 的基线，页面与事件已从 Shell 迁出；增加的控件文件是职责显式化成本，不能单独解读为全局代码量下降。

## 验证命令与结果

- `dotnet build src/Desktop.App/Desktop.App.csproj --configuration Debug --no-restore`：0 警告、0 错误。
- `dotnet test tests/Desktop.Application.Tests/Desktop.Application.Tests.csproj --configuration Debug --no-build --no-restore --filter "FullyQualifiedName!~SizeTableGoldSampleTests"`：287 通过。两项本地真实金标缺失仍按既有动态跳过规则排除。
- `dotnet test tests/Desktop.Infrastructure.Tests/Desktop.Infrastructure.Tests.csproj --configuration Debug --no-build --no-restore`：301 通过、5 项既有真实外部探针跳过。
- `dotnet test tests/Desktop.App.UiTests/Desktop.App.UiTests.csproj --configuration Debug --no-build --no-restore`：40 通过、0 失败。覆盖页面导航、批次输入、模板/标定、设置、结果和 AI 弹层自动化标识；现有用例包含窗口创建/关闭与返回路径。
- `dotnet test tests/Desktop.Architecture.Tests/Desktop.Architecture.Tests.csproj --configuration Debug --no-build --no-restore`：12 通过。
- `dotnet test tests/Desktop.Domain.Tests/Desktop.Domain.Tests.csproj --configuration Debug --no-build --no-restore`：50 通过。
- `dotnet test tests/Desktop.Contracts.Tests/Desktop.Contracts.Tests.csproj --configuration Debug --no-build --no-restore`：58 通过。
- `git diff --check`：通过；Git 的 LF/CRLF 提示不属于空白错误。

## 限制与未宣称事项

本轮没有真实 AI provider 请求、Photoshop/UXP 调用、安装包发布、提交或推送；没有读取/修改用户源 PSD、源照片、尺码图、数据库、凭据、缓存或正式输出。Infrastructure 外部探针和两项本地金标缺失均按既有规则保留，不把控件回归写成真实出图验收。

页面调用方边界已完成收敛；下一步是收集真实 AI/Photoshop、连续重启和旧数据读取证据，再单独审查并删除已经没有调用方的 `Shell` 桥、旧绑定代理和 timer 镜像。在真实验收条件满足前不删除兼容入口。

