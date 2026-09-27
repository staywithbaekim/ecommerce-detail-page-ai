# ARCH-01 维护性重构纵向实现任务书

## 状态

已批准连续执行；阶段 0、四条 Application 抽取、页面控件/状态/协调器抽取及 B-21 页面兼容转发清理代码子门已完成（2026-09-18）。331 个属性/命令别名与 53 个方法转发已迁移调用方并删除；Shell 为 560 行、显式业务服务接口字段 0，保留正式导航/跨页 host。B-22 随后完成受控 provider 边界下的真实进程中断恢复验证，并独立修正原有的“重启后仍显示分析中”问题；四组回归704通过，持久化/不重发规则不变。B-21旧数据读取及两次正常退出、B-20真实AI/Photoshop环境证据分别保留。完整ARCH-01仍待既定SIZE-S7/RENDER-01正式联验收，无需逐步重复审批。当前恢复证据见[B-22](../验证/ARCH-01-B22后台进程中断与恢复状态验证-20260918.md)，清理与原生运行见[B-21](../验证/ARCH-01-B21兼容转发清理与重启验证-20260918.md)，真实服务见[B-20](../验证/ARCH-01-B20页面通知与真实环境复核-20260918.md)。下方阶段性记录保留为历史快照。

## 目标

在不改变现有产品行为和已冻结技术合同的前提下，逐步收敛 Application 编排、WPF 页面状态和兼容路径，降低后续维护、运营修复和功能增量的边际成本。

2026-09-18 收尾依赖核对：本任务的重构代码与已列明回归已完成，正式出图仍依赖尚未实现的 RENDER-01 adapter/bridge，不能把余项概括为仅剩验收操作。RENDER-01 §3.1 允许在 SIZE 真实输出验收前进行合同/适配器离线验证；之后才能补齐联合输出证据。本任务不扩建正式渲染器，完整 ARCH-01 仍保留既定联合验收门，代码完成与完整验收状态分别报告。

## 当前范围

第一条已完成范围：

1. 固定当前行为、依赖和验证基线。
2. 将 AI 工作流中的结果投影职责抽到独立 Application 服务。
3. 保留 `AiBatchAnalysisWorkflow` 作为兼容门面，先委托新服务，确保调用方和接口不变。
4. 为新服务补充聚焦测试和架构依赖约束。

模板 ViewModel、主 XAML、SQLite partial 和供应商适配器的拆分不在这一条提交中提前混入。

第二条已完成范围：

1. 将生成资格检查、已确认选择、颜色计划、主体几何和尺码资源准备迁入 `AiRenderPreparationService`。
2. 保留 `PrepareRenderingAsync` 的签名、配置缺失错误及状态恢复顺序；工作流向服务传入批次、模板、请求集合与已有投影事实。
3. 复用 `IAiAnalysisResultReader`，服务仅依赖结果读取、模板资源、尺码持久化/栅格化及 RenderPlan 工厂端口，不接收 scheduler 或可变运行上下文。
4. 对照迁移前实现，补充服务独立测试，执行受影响的 Application、Infrastructure、WPF UI 和架构回归。

启动核对：第一条抽取的代码验收已通过，读取器、投影服务、RenderPlan 工厂与尺码端口均存在于当前工作树；本轮抽取前工作流/RenderPlan 集成测试 33/33 通过。这是现有用例的维护性重构，不启动仍受 SIZE-S7 联验收约束的正式 RENDER-01 adapter。

第三条已完成范围：

1. 将 scheduler/checkpoint 恢复、两道恢复锁、两项尝试标记和共享错误迁入每批次独立的 `AiAnalysisRecoveryService`。
2. `BatchContext` 创建并释放恢复服务；服务借用 scheduler，只释放自己的锁。调度器及 admission/consumer 后台任务仍由原工作流和上下文管理。
3. 保留各入口原有恢复顺序与覆盖范围、错误映射和取消行为；不合并两道锁，不增加重试，不改 scheduler 内部恢复或持久化实现。
4. 新增并发、取消、错误保留、控制状态及资源归属测试，完成受影响四个测试项目回归。

第三条启动核对：前两条代码子门已完成，scheduler、checkpoint 端口及真实 coordinator 均存在。改动前集成组两次为 32 通过/1 失败，同一精细分析用例单独运行通过；最终构建后的完整 Infrastructure 300 通过。该既有不稳定现象单独保留为待定位项，不归为本次抽取引入或修复。

第四条已完成范围（2026-09-17，产品负责人已批准）：

1. 先独立复现并修复精细分析入口使用旧投影请求的问题：命令从持久化调度记录获取实际父请求，保留无历史记录时原有的占位请求回退；不放宽父子引用检查。受控测试已验证修复前 consent-pending 不一致、修复后集成组 34/34。
2. 以修复后工作树为重构基线，按批次抽取 `AiBatchCommandService`，封装命令、请求刷新与恢复调用；不暴露可变运行上下文。
3. `AiBatchBackgroundRunner` 只拥有 admission/consumer 任务、准入取消源和运行失败状态，借用 scheduler；命令服务拥有 scheduler/恢复服务，门面保留全局取消、全部停止、逐个等待、最后统一释放的顺序。
4. 保留公开工作流接口、原命令顺序、开始即返回、持续补位和每任务 5 秒关闭等待。投影与渲染准备继续使用既有服务；本轮不迁移 WPF 页面或删除公开兼容入口。
5. 独立验证命令与后台生命周期，运行四组受影响回归，对照修复与抽取两段差异，并分别报告规模。

## 不变的合同和安全边界

- 不修改 `RenderPlan` schema、AI result schema、SQLite migration、checkpoint、manifest 或 Photoshop/UXP bridge 协议。
- 不修改颜色版本规划、模板学习、动态尺码表或批次状态规则。
- 不修改源 PSD、源照片、市场尺码图和正式输出。
- 不把 WPF、SQLite、HTTP 或供应商 DTO 引入 Domain/Application 的新公共合同。
- `AiBatchAnalysisWorkflow` 的现有公开接口保持兼容。

## 阶段 0 基线

- 源码规模和热点记录见 [维护性架构审计](../技术方案/维护性架构审计-2026-09-17.md)。
- 现有架构依赖测试必须继续通过。
- 当前工作树中已有的模板、AI、尺码表和文档变更属于此前已批准工作，重构不得覆盖或回滚。
- 每个抽取步骤完成后运行受影响测试、架构测试和 `git diff --check`。

## 第一条抽取的职责边界

新增的投影服务只负责：

- 读取已持久化的批次、请求、选择、结果和模板快照事实；
- 将这些事实转换为 `AiBatchAnalysisProjection` 及其下属只读投影；
- 复用既有状态枚举、错误映射和安全门，不自行启动 scheduler、不写数据库、不调用 provider；
- 不创建 RenderPlan，不准备尺码资源，不处理 WPF 控件状态。

第四条完成后，工作流保留兼容用例入口、批次加载/缓存、投影与渲染协调以及全局关闭顺序。命令服务拥有批次命令、请求状态、scheduler 和恢复服务；后台执行器拥有 admission/consumer 任务与局部取消源，不接管 scheduler 释放。

## 完成标准

- 新服务有独立单元测试，覆盖正常投影、缺失结果、业务阻断和恢复状态。
- `AiBatchAnalysisWorkflow` 的公开行为和现有调用方不变。
- Application、Infrastructure、WPF UI 和架构测试通过；没有新增警告。
- `git diff --check` 通过，且变更不包含源媒体、数据库、凭据、缓存或生成物。
- 任务书和当前项目状态记录实际验证结果与下一条抽取动作。

## 后续阶段

页面控件、局部弹层、业务协调器、旧绑定代理清理和受控 provider 边界下的进程中断恢复子门已完成。B-22 修复只补充运行时恢复事实及展示，不修改账本、重试或持久化合同。Shell 继续拥有导航、跨页衔接和关闭，窄 host 与有调用者的 Application 用例门面属于正式边界；旧结果兼容读取及安全检查保留。剩余正式联验收按 SIZE-S7/RENDER-01 路由执行，正式渲染器尚未实现，不扩入本轮重构实现或用旧技术夹具替代。

1. Application 命令、后台执行、结果投影、RenderPlan 准备和恢复职责已分离；公开门面仍保留，不能仅因服务完成就删除调用入口。
2. 将 `MainWindowViewModel` 降为导航/生命周期 Shell，并迁移页面状态。
3. 将 `MainWindow.xaml` 拆为页面控件和局部弹层。
4. 在验收和重启证据齐全后删除兼容路径。

## 第一条交付与验证

- `AiBatchProjectionService` 独立负责批次/款式/页面/图片框/尺码来源投影；`AiBatchProjectionSnapshot` 以不可变集合隔离运行中的请求和失败字典，不传递 scheduler 或命令能力。
- `AiAnalysisResultReader` 复用既有账本/结果引用校验、子请求归属和旧结果别名归一化；展示与渲染准备共用它。读取不保存状态、不调用 provider，取消继续向调用方传播。
- `IAiBatchAnalysisWorkflow`、状态枚举和投影 DTO 迁入独立文件，公开签名不变。原工作流中的投影与读取实现已删除，只保留快照构造和委托。门面暂时保留，待命令/恢复/渲染准备抽取及调用方迁移验证完成后再决定移除。
- 工作流 2,907 → 1,100 行；相关生产代码合计 2,907 → 3,004 行（+97），新增 13 项独立投影/读取测试和 1 项架构边界测试。总量、热点和职责分别记录，避免只用单文件行数证明减量。
- Debug solution build：0 警告/0 错误；Application 225/225（排除缺少真实本地样本的 2 项 `SizeTableGoldSampleTests`）；Infrastructure 300 通过/5 项既有外部探针跳过；WPF UI 40/40；Architecture 6/6。
- 抽取前工作树与拆分后实现对照通过：公开接口/DTO、投影业务分支和文案、读取校验及剩余工作流均一致，只更换依赖访问和增加快照委托。`git diff --check` 通过。
- 本轮未变更产品规则、schema、migration 或 bridge，未接触用户源媒体和正式输出；没有新增真实 AI 请求或 Photoshop 输出验收，也没有重新发布运行包。
- 复现命令与限制见 [结果投影抽取验证](../验证/ARCH-01-结果投影抽取验证-20260917.md)。

## 第二条交付与验证

- `AiRenderPreparationService` 负责资格检查、已确认选择、颜色计划、主体几何、尺码资源准备和工厂调用；只注入结果读取、模板资源、RenderPlan 工厂、尺码仓库和栅格化端口。
- `PrepareRenderingAsync` 保留公开签名、配置缺失错误和状态恢复顺序，传入 `AiRenderPreparationSnapshot`。请求集合在委托前复制为不可变数组，不向新服务传 scheduler 或可变 `BatchContext`。
- 原渲染准备、尺码准备、manifest 投影及样式构造实现已从工作流删除。会话读取改由已有读取器转发到同一个 store；没有新增 fallback、重复选图规则或持久化协议。
- 工作流 1,100 → 751 行，新增服务 390 行、输入快照 12 行，相关生产代码净增 53 行。两条抽取合计 2,907 → 3,057 行（+150）；当前 `src` C# 为 145 文件、49,020 行。职责隔离与总量分别记录。
- 新增 20 项独立服务测试、1 项架构约束。Debug solution build 0 警告/0 错误；Application 245 通过（2 项缺少本地金标测试排除），Infrastructure 300 通过/5 跳过，WPF UI 40 通过，Architecture 7 通过，共 592 项通过。
- 迁移前后公开接口、渲染/尺码业务分支、错误码及剩余工作流对照一致；仅替换依赖、参数访问与快照委托。diff 检查通过。测试初次编译的 CA1861 常量数组告警已在测试夹具内修正，没有为测试调整生产规则。
- 未重新发布运行包或新增真实 AI/Photoshop 输出验收。完整命令、抽取对照与限制见 [渲染准备抽取验证](../验证/ARCH-01-渲染准备抽取验证-20260917.md)。

## 第三条交付与验证

- 恢复服务按 `BatchContext` 实例持有状态，两道锁及尝试标记不跨批次共享。只有进入锁后的恢复调用消耗本次尝试；I/O 中取消或异常后同一上下文仍不重试，入锁前取消不消耗尝试。可选 checkpoint 未配置时仍直接返回。
- scheduler 恢复和批次 checkpoint 恢复的调用点、先后关系及错误覆盖规则不变。原三段恢复/阻断方法与上下文中的五项恢复字段已删除；公开工作流接口、后台处理和持久化合同不变。
- 工作流 751 → 675 行；新服务 106 行，本轮相关生产代码净增 30 行。三条抽取累计 2,907 → 3,087 行（+180）；当前 `src` C# 146 文件、49,050 行。工作流 private readonly 字段仍为 12 个，不宣称依赖总量减少。
- 新增 25 项服务测试和 1 项架构约束；Debug solution build 0 警告/0 错误；Application 270 通过（2 项缺失本地金标排除）、Infrastructure 300 通过/5 跳过、WPF UI 40 通过、Architecture 8 通过，共 618 通过。
- 迁移前工作树对照与 diff/空白检查通过；无真实 provider/Photoshop 调用或安装包发布。基线集成不稳定的两次失败和一次单独通过均保留，尚未定位根因，不以最终通过宣称修复。证据见 [恢复服务抽取验证](../验证/ARCH-01-恢复服务抽取验证-20260917.md)。

## 第四条交付与验证

- 先固定请求投影滞后场景：旧实现将 `consent-pending` 传给子请求，确定性断言失败；改为命令直接读取持久化父请求后，34 项工作流/RenderPlan 集成测试通过。父子引用检查、原授权检查和未知结果不重发规则保留。修复在重构前完成并单独保存对照基线。
- `AiBatchCommandService` 按批次封装开始、刷新、暂停/继续/取消、网络等待、人工选择、精细分析及请求刷新，提供不可变事实快照。`AiBatchBackgroundRunner` 接管原 admission/consumer 循环、一次准入、异常归一化、运行失败集合和有界关闭等待。
- 原门面中的命令体、后台循环和可变 `BatchContext` 已删除。公开接口/构造函数不变，旧门面仅委托；全局取消 → 所有批次停止准入 → 逐任务最多等待 5 秒 → 统一释放的顺序保持原样。未延长等待或新加自动重试。
- 本轮工作流 675 → 688（修复 +13）→ 229 行；新增命令服务 330 行、后台执行器 234 行。抽取本身净增 105 行，连同修复净增 118 行；四条相关生产代码累计 3,205 行（相对最初 +298）。`src` C# 为 148 文件、49,168 行。工作流 private readonly 字段仍为 12 个，依赖总量未宣称减少。
- 新增 17 项 Application 测试、2 项架构约束，集成测试增加 1 个受控场景。最终 build 0/0；Application 287 通过（2 项缺少本地金标排除）、Infrastructure 301 通过/5 跳过、WPF UI 40 通过、Architecture 10 通过，共 638 通过。公开 API、迁移基线对照与 diff/空白检查通过。
- 未改动用户源媒体、正式输出、持久化合同或 WPF 页面；无真实 AI/Photoshop 调用、发布、提交或推送。具体诊断、范围和命令见 [命令与后台执行抽取验证](../验证/ARCH-01-命令与后台执行抽取验证-20260917.md)。

## B-03 AI 分析页面状态迁移（2026-09-18）

- 新增 `AiAnalysisPageState`，集中持有 AI 分析页的投影、款式列表、筛选列表、选中款、展开集合、候选预览、替换目标/素材、授权弹层、忙碌状态、状态消息和刷新计时器；命令实例也由该页面状态拥有。
- `MainWindowViewModel.Ai.cs` 通过兼容别名和转发属性保留现有 XAML、`Results`、`SizeTables`、`ReferenceLearning` 调用路径；Shell 继续负责导航、跨页面协调和全局关闭。AI 工作流依赖仍由 Shell 持有，不把 Application 服务伪装成页面状态。
- 列表/筛选、款式详情往返、人工锁定、候选预览、替换预览、授权弹层、后台刷新和尺码图选择的行为未改变。为兼容既有 UI 诊断，刷新计时器保留私有字段镜像并与页面状态同步，后续页面控件拆分完成后再删除该代理。
- 本轮只迁移状态存储，不拆主 XAML、不删除旧绑定、不改变 AI schema、RenderPlan、SQLite/checkpoint/manifest、真实出图或颜色规则。
- B-03 完成时 `MainWindowViewModel.Ai.cs` 为 1,831 行，新增页面状态 69 行。该阶段文件行数因兼容转发暂时增加；后续页面控件和绑定边界已完成，当前文件行数以工作树审计为准，不把历史快照当作当前值。
- Debug solution build：0 警告、0 错误；受影响四组回归 640 项通过、5 项既有外部探针跳过；其中 Application 287 通过、Infrastructure 301 通过/5 跳过、WPF UI 40 通过、Architecture 12 通过，Domain 50 与 Contracts 58 也通过，另 2 项缺少本地真实尺码样本而按既有规则排除。`git diff --check` 通过。
- B-03 完成时 `AiAnalysisViewModel` 已接管命令初始化、筛选/Top-N 操作和页面状态协调，Shell 仅保留兼容调用入口；随后 B-04 页面控件拆分、B-05 页面绑定收敛和 B-06 模板工作区调用边界迁移已完成。
- 详细命令、覆盖范围和限制见 [B-03 页面状态迁移验证](../验证/ARCH-01-B03页面状态迁移验证-20260918.md)。

## B-04 WPF 页面控件、局部弹层与 Shell 收敛（2026-09-18）

- AI 分析页、款式检查页、候选预览、上传授权、批次输入、结果检查、模板工作区、设置页及四个低风险弹层已从 `MainWindow.xaml` 抽为独立 UserControl；模板拖放/画布/标定事件和批次扫描事件随页面 code-behind 迁移。
- B-04 完成时 `MainWindow.xaml` 由约 4,249 行降至 713 行、`MainWindow.xaml.cs` 为 137 行；随后 B-05/B-06 完成资源、弹层、快捷键和事件归属收敛，当前工作树为 458/43 行。主窗口只保留导航、生命周期、尺寸、通用任务行资源和凭据清空入口。行数是热点收缩证据，不代替行为验证。
- B-04 阶段 AI 页面控件以 `AiAnalysisViewModel` 为 DataContext；当时短期 `Shell` 转发桥、其他页面继承 Shell DataContext 的绑定别名和 Window.Resources 事件入口仍保留。随后 B-05/B-06 已迁移页面显式属性、命令、资源和事件归属；剩余兼容路径仍等待真实验收/重启/旧数据读取证据后删除。
- 控件拆分没有改变 AI/RenderPlan/SQLite/checkpoint/manifest/bridge 合同、颜色规则、源媒体安全边界或正式输出；没有真实 provider/Photoshop 调用。
- Debug build 0/0；Application 287 通过（两项本地金标缺失排除）、Infrastructure 301/5 跳过、WPF UI 42、Architecture 12、Domain 50、Contracts 58；`git diff --check` 通过。独立证据见 [B-07 结果页面状态抽取验证](../验证/ARCH-01-B07结果页面状态抽取验证-20260918.md)。
- 下一步不再新增页面直接 Shell 调用；先完成真实 AI/Photoshop、连续重启、旧 SQLite/结果读取和 SIZE-S7/RENDER-01 联验收，再以单独变更删除已无调用者的过渡适配、旧绑定代理和 timer 镜像。

## B-05 页面绑定边界收敛（2026-09-18）

- `AiAnalysisViewModel` 现在实现 `INotifyPropertyChanged`/`IDisposable`，AI 分析页及候选预览、上传授权弹层使用显式页面属性和命令；AI 页面 XAML 不再通过 `Shell.*` 绑定。
- `BatchInputPageViewModel`、`ResultReviewPageViewModel` 和 `SettingsPageViewModel` 已补齐本页面使用的状态、集合、命令和输入入口。批次、结果和设置页的相对命令绑定改为指向各自 UserControl，设置密码框不再直接穿透 Shell。
- B-05 完成时模板工作区仍保留 26 个 Shell 事件桥调用，涉及画布平移/缩放、PSD 拖放、标定键盘和参考学习跨页协调；这些调用已在 B-06 迁移到模板页面 ViewModel，过渡适配本身仍不能仅凭静态无调用判断删除。
- Debug build 0/0；WPF UI 40/40；Architecture 12/12；Infrastructure 单项目复跑 301 通过/5 跳过；Application 排除两项缺少本地真实金标后 287 通过；Domain 50/50；Contracts 58/58；`git diff --check` 通过。全量并行运行中的一次 `AI_SCHEDULER_STATE_STALE_WRITE` 单独复跑通过，作为既有竞态风险保留。
- 详细边界、退出条件和命令见 [页面边界与兼容退出门验证](../验证/ARCH-01-页面边界与兼容退出门验证-20260918.md)。真实 AI/Photoshop、连续重启和旧 SQLite/结果读取证据仍未完成，兼容桥暂不删除。


## B-06 模板工作区页面边界收敛（2026-09-18）

- 模板工作区 code-behind 已从 `MainWindowViewModel` 迁移到 `TemplateWorkspacePageViewModel`，覆盖 PSD 导入/文件夹选择、画布平移与缩放、模板页排序、标定素材拖放、标定几何编辑和参考学习模式判断。
- 页面 XAML/code-behind 不再直接引用 `Shell`；页面 ViewModel 仍通过 `ShellPageViewModel` 过渡适配器提供尚未完成退出门的跨页状态和通知。
- 模板卡片和模板详情的 `DataTemplate` 已从 `MainWindow.Resources` 迁移到 `TemplateWorkspacePage.Resources`，命令绑定通过模板页面祖先解析；主窗口不再承载模板页面命令资源。
- `MainWindow.xaml` 从 B-05 的 713 行进一步收敛为 458 行，`MainWindow.xaml.cs` 从 137 行收敛为 43 行；这是页面资源、弹层和事件归属变化的结构证据，行为仍以 WPF/UI 回归为准。
- 模板校准快捷键（撤销、重做和清除绑定）已迁入模板页面预览键盘事件，主窗口只保留窗口级生命周期和尺寸处理。
- 批次页面选择、尺码表编辑、模板确认和重复 PSD 复核弹层已移入对应页面 UserControl，页面 ViewModel 补齐显式状态、集合和命令；主窗口不再托管页面级弹层。
- 模板确认弹层的 Esc 事件同步使用模板页面 ViewModel，原有操作优先级和取消/确认行为保持不变。
- 迁移只调整表现层调用边界，不改变 AI 结果、RenderPlan、SQLite/checkpoint、manifest、bridge、颜色规则、源媒体和正式输出。
- 退出门仍保持开放：真实 AI/Photoshop、连续重启、旧 SQLite/结果读取和 SIZE-S7/RENDER-01 联验收完成前，不删除过渡适配、旧绑定描述符和 timer 镜像。
- 本轮 build 0/0；WPF 40/40；Architecture 12/12；Application 287/287（两项缺少本地真实金标按既有规则排除）；Infrastructure 301 通过/5 跳过；Domain 50/50；Contracts 58/58；`git diff --check` 通过。

ARCH-01 的 Application 抽取、B-03 状态迁移、B-04 控件拆分、B-05 页面绑定收敛和 B-06 模板工作区调用边界迁移均已完成；剩余工作包括页面业务编排抽取、真实验收证据和兼容路径删除，详见 B-12 后的更正范围。

## B-07 结果页面状态抽取（2026-09-18）

- 新增 `ResultReviewPageState`，持有结果卡片集合、筛选器、当前页面、RenderPlan 展示事实和窄版布局断点；`ResultReviewPageViewModel` 现在持有并转发该状态。
- `MainWindowViewModel.Results.cs` 不再持有结果集合、筛选器或布局字段，只保留 RenderPlan 工作流委托、批次导航和兼容公开别名。
- 页面状态不持有 scheduler、provider、持久化端口或可变批次上下文；没有修改 AI 结果合同、RenderPlan、SQLite/checkpoint、颜色规划、源媒体或正式输出。
- Debug build 0/0；Application 287/287（两项本地金标缺失按既有规则排除）；WPF UI 42/42；Architecture 13/13；`git diff --check` 通过。详细命令和限制见 [B-07 结果页面状态抽取验证](../验证/ARCH-01-B07结果页面状态抽取验证-20260918.md)。
- 真实 AI/Photoshop、连续重启、旧 SQLite/结果读取和 SIZE-S7/RENDER-01 联验收仍未完成；兼容退出门保持开放。

## B-08 设置页面状态抽取（2026-09-18）

- 新增 `SettingsPageState`，持有凭据状态、脱敏文案、输入缓冲、忙碌态和按钮可用性；`SettingsPageViewModel` 负责页面状态通知。
- `MainWindowViewModel.Credentials.cs` 不再持有凭据展示字段，只负责凭据端口调用、命令和 AI 请求前门禁；保存、读取、移除和输入清理语义保持不变。
- 页面状态不依赖凭据管理端口、provider、SQLite 或批次上下文；没有修改 AI 结果合同、RenderPlan、持久化、颜色规划、源媒体或正式输出。
- Debug build 0/0；Application 287/287（两项本地金标缺失按既有规则排除）；WPF UI 43/43；Architecture 14/14；`git diff --check` 通过。详细命令和限制见 [B-08 设置页面状态抽取验证](../验证/ARCH-01-B08设置页面状态抽取验证-20260918.md)。
- 真实 AI/Photoshop、连续重启、旧 SQLite/结果读取和 SIZE-S7/RENDER-01 联验收仍未完成；兼容退出门保持开放。

## B-09 批次输入页面状态抽取（2026-09-18）

- 新增 `BatchInputPageState`，持有模板与页面选择、目录展示、选图方式、可编辑 PSD 选项、扫描状态/文案、页面选择弹层快照、草稿批次号和输出预览；`BatchInputPageViewModel` 负责页面属性、命令边界和状态通知。
- `MainWindowViewModel.BatchInput.cs` 不再持有上述页面展示字段或页面选择备份；Shell 继续拥有批次扫描、草稿持久化、上传授权、AI 恢复、模板服务和导航编排，扫描顺序、错误映射、取消行为和源媒体只读边界保持不变。
- 为兼容现有 UI 诊断和旧入口，`_currentBatchId` 保留为同步镜像；退出门关闭前不删除该字段或 `ShellPageViewModel` 过渡适配。状态对象不依赖工作流、文件夹选择器、锁、取消源、provider 或持久化端口。
- 新增 3 项页面状态单测和 1 项架构边界断言。Debug solution build 0/0；WPF UI 46/46；Architecture 15/15；`git diff --check` 通过。详细证据见 [B-09 批次输入页面状态抽取验证](../验证/ARCH-01-B09批次输入页面状态抽取验证-20260918.md)。
- 本轮未修改 AI 结果、RenderPlan、SQLite/checkpoint、manifest、颜色规则、源媒体或正式输出；真实 AI/Photoshop、连续重启、旧数据读取和 SIZE-S7/RENDER-01 联验收仍未完成。

## B-10 模板画布视图状态抽取（2026-09-18）

- 新增 `TemplateWorkspacePageState`，接管模板画布专属的窄布局面板、校准视口缩放、模板画布缩放/平移/重置及边界约束；`TemplateWorkspacePageViewModel` 持有该状态并向页面暴露原有属性名。
- `MainWindowViewModel.Templates.cs` 不再持有上述纯视图字段；Shell 继续通过兼容别名提供既有公开属性和通知，模板 PSD 导入、页面选择/加载、校准草稿、几何提交、参考学习、对话框和持久化编排保持在 Shell。
- 页面 XAML/code-behind 的绑定名和事件入口不变；状态对象不依赖模板工作区、校准、参考学习、取消源或持久化端口。未改变模板页面合同、RenderPlan、SQLite/checkpoint、manifest、颜色规则、源媒体或正式输出。
- 新增 3 项视图状态单测和 1 项架构边界断言。当前验证：Debug build 0/0；WPF UI 49/49；Architecture 16/16；Application 287/287（两项本地金标按既有规则排除）；Infrastructure 301 通过、5 个真实环境探针跳过；Domain 50/50；Contracts 58/58；`git diff --check` 通过，共 761 项通过、5 项跳过。
- 当前页面 UserControl 的 XAML/code-behind 直接引用 `Shell` 或 `MainWindowViewModel` 仍为 0；`ShellPageViewModel` 过渡投影和 `_currentBatchId`、AI timer 镜像仍按退出门保留。详细证据见 [B-10 模板画布视图状态抽取验证](../验证/ARCH-01-B10模板画布视图状态抽取验证-20260918.md)。
- B-10 仍不关闭真实 AI/Photoshop、连续重启、旧 SQLite/结果读取和 SIZE-S7/RENDER-01 联验收退出门；这些证据齐全后，兼容删除必须单独变更并重复完整回归。

## B-11 模板弹层视图状态抽取（2026-09-18）

- `TemplateWorkspacePageState` 新增模板确认弹层和重复 PSD 复核的展示状态：标题、正文、安全提示、按钮文案、危险标记、输入缓冲、可见性和重复复核可见性；`TemplateWorkspacePageViewModel` 直接从页面状态提供绑定。
- Shell 继续拥有 `TaskCompletionSource<TemplateDialogChoice>`、确认/取消/三级操作顺序、重复 PSD 决策、模板工作区服务和持久化副作用；确认按钮输入门禁的命令刷新语义保持不变。
- 新增 1 项状态单测，并扩展架构边界断言。当前验证：Debug build 0/0；WPF UI 50/50；Architecture 16/16；Application 287/287（两项本地金标按既有规则排除）；Infrastructure 301 通过、5 个真实环境探针跳过；Domain 50/50；Contracts 58/58；`git diff --check` 通过，共 762 项通过、5 项跳过。
- B-11 未改变 AI、RenderPlan、SQLite/checkpoint、manifest、颜色规则、源媒体边界或正式输出；真实 AI/Photoshop、连续重启、旧数据读取和 SIZE-S7/RENDER-01 联验收仍是兼容路径退出条件。详细证据见 [B-11 模板弹层视图状态抽取验证](../验证/ARCH-01-B11模板弹层视图状态抽取验证-20260918.md)。

## B-12 模板页面状态抽取（2026-09-18）

- `TemplateWorkspacePageState` 接管当前模板、当前模板页、模板组名称、状态消息及忙碌状态。Shell 保留选择切换的输入捕获、异步加载版本控制、派生通知和命令刷新；模板服务、校准、参考学习、持久化及对话框完成信号归属不变。
- B-12 构建 0 警告/0 错误；WPF 50、Architecture 16、Application 287、Infrastructure 301（另 5 跳过）、Domain 50、Contracts 58，共 762 通过。另 2 项真实尺码金标缺失按既有规则过滤，未过滤时 runner 记为失败。页面/弹层直接 Shell 引用为 0，`git diff --check` 通过。详见 [B-12 验证记录](../验证/ARCH-01-B12模板页面状态抽取验证-20260918.md)。
- 不继续把 `_templateScreen`、`_templateDetails`、`_templateDraft` 或模板业务服务迁入纯页面状态对象；后续仍需将业务编排迁入页面协调器，并完成验收证据与兼容删除。
- 已定位 G 盘 Photoshop 2025、办公室素材包和现有 DesktopV1 数据目录。文件/目录存在不代表实机准入、旧数据读取或输出验收通过；下一步优先复用这些资源，无需用户重复提供。

## B-12 后的剩余收尾（B-13 复核后更正）

1. 继续阶段 2 的页面业务编排抽取：尺码编辑器已在 B-13 完成；B-14/B-15 已迁移设置凭据和批次输入操作；B-16 已迁移 AI 工作流、上传授权及渲染准备交接；仍需收敛结果页命令、模板/参考学习等页面的业务归属。页面状态抽取不能替代独立页面协调器，不能把原目标缩成仅删除代理。
2. 同时在已有环境上补齐真实 AI、关闭/重启/后台恢复、旧数据读取和 O-03 实机证据；SIZE-S7/RENDER-01 按已有任务路由联验收，不以离线测试替代。
3. 退出证据齐全后，单独删除无调用者的页面兼容适配、旧绑定和镜像；保留仍有业务调用方的用例门面，重复相关回归并审查差异。
4. 更新职责/依赖/规模审计及完整验收状态。达到原 ADR 的全部退出条件后才标记 ARCH-01 整体完成。

## B-13 尺码编辑器职责抽取（2026-09-18）

- `SizeTableEditorViewModel` 独立持有编辑状态、加载/保存及七个命令；弹层直接以其为 DataContext。通过 `ISizeTableEditorHost` 保留批次/预览查询和保存后的跨页刷新，Shell 仅保留该范围的兼容别名与跨页协调。
- 表格状态和行/列/单元格类型原样迁入独立文件，原 Shell partial 471 → 53 行；相关三文件合计 533 行（+62）。没有修改尺码业务规则、Application port、持久化、RenderPlan 或正式输出边界。
- build 0/0；WPF 55、Architecture 17、Application 287、Infrastructure 301，受影响四组共 660 通过、5 跳过，另 2 项金标缺失过滤；diff 与页面 Shell 引用审计通过。详见 [B-13 验证记录](../验证/ARCH-01-B13尺码编辑器职责抽取验证-20260918.md)。
- 同期使用 B-12 独立运行包和原数据副本验证了旧模板画布及批次恢复；生产读取端口成功读取 4 批次/7 会话/6 结果。原文件哈希未变。连续关闭/重启因原生窗口最小化/用户输入保护未完成，不能算完整恢复通过。详见 [旧数据副本实机读取验证](../验证/ARCH-01-B12旧数据副本实机读取验证-20260918.md)。

## B-14 设置凭据职责抽取（2026-09-18）

- 设置页拥有独立 `AiCredentialViewModel`，负责既有凭据端口、三个命令和扫描前状态检查；Shell 仅保留兼容别名、清空事件转发及失败导航。上传授权仍待 AI 编排迁移。
- 受影响四组共 667 通过、5 跳过，另 2 项缺失金标过滤；build 0/0、diff 通过。Shell 接口依赖字段 10 → 9；没有访问真实 Key 或操作原生窗口。见 [B-14 验证](../验证/ARCH-01-B14设置凭据职责抽取验证-20260918.md)。
- 后续连续执行批次/AI/模板与参考学习的编排抽取；整个 ARCH-01 和兼容退出门尚未关闭。

## B-15 批次输入职责抽取（2026-09-18）

- 独立 `BatchInputViewModel` 接管 13 个命令、模板页读取、目录选择、草稿恢复/保存和取消扫描；页面持有协调器，Shell 保留跨页 host、布局和旧公开别名/批次镜像。原提交/投影/接口类型原样迁至合同文件。
- build 0/0；WPF 67、Architecture 19、Application 287、Infrastructure 301，共 674 通过、5 跳过，另 2 项缺失金标过滤；diff 与页面引用审计通过。新增异步旧草稿隔离、扫描取消/重试、恢复顺序及阻断回归。见 [B-15 验证](../验证/ARCH-01-B15批次输入职责抽取验证-20260918.md)。
- 下一条：AI 工作流/上传授权/刷新/替换及结果交接，然后模板/参考学习。既有真实运行、兼容退出门继续保留，未宣称整体完成。

## B-16 分析页面职责抽取（2026-09-18）

- `AiAnalysisViewModel` 直接拥有工作流、分析控制/刷新、授权续接、人工选图及渲染准备；Shell 不再持有 AI 工作流，保留窄跨页 host 和诊断/绑定兼容。批次重试动作移交批次协调器，条目模型迁入独立文件。
- build 0/0；WPF 75、Architecture 19、Application 287、Infrastructure 301，共 682 通过、5 跳过，另 2 项金标缺失过滤；diff 通过。33 个方法对照与 8 个新增独立用例通过。见 [B-16 验证](../验证/ARCH-01-B16分析页面职责抽取验证-20260918.md)。
- 下一条：结果页命令归属、模板与参考学习编排。连续执行授权不变，真实运行/重启/兼容退出门仍开放。

## B-17 结果页命令抽取（2026-09-18）

独立结果协调器已接管筛选、详情选择、翻页、返回和布局；Shell 保留跨页导航及兼容别名。build 0/0；受影响四组 684 通过、5 跳过，另 2 项缺失金标过滤。见 [B-17 验证](../验证/ARCH-01-B17结果页命令抽取验证-20260918.md)。下一条继续模板/参考学习共同工作区的协调器抽取。

## B-18 模板与参考学习职责抽取（2026-09-18）

独立模板工作区协调器统一拥有共享草稿、校准/参考学习、六个端口、目录选择、对话框与命令；Shell 只提供导航可见性和授权 host。92 个方法主体、模型与命令迁移对照通过，剩余 Shell 审计确认业务服务接口字段为 0；兼容入口仍有调用者，继续保留。

build 0/0；WPF 79、Architecture 19、Application 287、Infrastructure 301，共 686 通过、5 跳过，另 2 项真实金标缺失过滤。新增独立加载失败/恢复和目录取消/模式复位回归；见 [B-18 验证](../验证/ARCH-01-B18模板与参考学习职责抽取验证-20260918.md)。阶段 2 代码职责收敛已完成，下一步为最新包实机关闭/连续重启及既定真实证据，最后单独处理兼容删除与最终审计。B-12 旧窗口因先前用户输入保护保持暂停，不能把缺少实机证据写成已经完成。
