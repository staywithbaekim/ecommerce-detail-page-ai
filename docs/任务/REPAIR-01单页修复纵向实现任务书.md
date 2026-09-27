# REPAIR-01：单页修复纵向实现任务书

日期：2026-09-24。状态：R0与R1a～c代码已实现并有聚焦测试/正式只读入口证据；**R1d真实改值及完整REPAIR-01尚未验收**。

## 当前执行位置（2026-09-24 20:52）

新尺码修订/单页计划、journal v3/v18原子请求、AI安全收口租约和O-03真实repair入口已接入。正式App已迁移v18、按ID恢复6015七页并成功打开真实尺码编辑器；未改值/保存/连接PS，70保护文件与10业务表不变。构建/故障测试及确切范围见 [R1实施与只读实机记录](../验证/REPAIR-01-R1尺码修复实现与只读实机入口-20260924.md)。旧current不推进、原计划regen不放宽，R2尚未实施。

下一门R1d需有依据的更正值。6015原25格正确，已向用户请求款号、行/列、正确值或新正确尺码图；不编造产品尺寸，也不将只读入口或fixture修改写成实际修复验收。下文候选/“本次未执行”描述保留作为任务形成时历史，实施决定及本节/证据优先。

## 当前实施决策（2026-09-24 20:20，优先于下文候选）

主线原生整合与连续使用入口已通过限定门，现开始R0/R1；本节是实施决定，不是通过声明。

- **D1/D3**：采用既有 `RenderBatchCoordinator` 同一个运行锁和写锁，增加窄 `ISizeTablePageRepairWorkflow`，不让UI提交任意计划。journal v3 的追加页带 `RenderSizeTableRepairOrigin`：request ID/请求摘要、期望journal/attempt、基plan digest、基尺码快照JSON和新尺码快照JSON。两个严格、受限、可重算数据digest的不可变快照保存在SQLite journal正文内，而不是先写可变current文件再补账本。原生成前 `current.json` 不推进，之后编辑已修复页直接读取其最新有效修复快照；原计划重生成沿祖先找同页最近修复，不继承新请求身份。
- 在v17后追加v18修复请求索引（batch/request唯一、page index/birth revision/request digest）；复用既有attempt origin表和事务。新 `RenderPageRepairRequested` 事件、请求索引、新attempt、journal和任务投影同事务。读取验证出生版/父版/事件/请求索引，v1/v2历史字节与规则不改，v3不降级。旧二进制拒绝新数据库，不承诺降级可写。
- PNG仍使用既有内容身份和受控resources路径；先生成并回读核验，再CAS授权唯一新attempt。未登记候选不能执行，失败不覆盖旧PNG/正式输出，来源或基线不闭合则阻断。不增加第二套current指针或另一个修复文件仓储。R1先支持有显式插入校准的动态尺码页，其他旧形状明确不支持，不猜校准。
- 修复候选仍由 `SizeTable.Edit`、既有rasterizer/校准resolver/builder生成，严格白名单核对只有尺码结构/关联栅格资源/确定性placement及新执行身份改变。原 `PreservesFacts` 不放宽；普通图片、模板、输出PSD意图、文件名和其他页保持。
- 首次真实“改值”必须有来源/用户依据；当前6015的25格已全部与原图一致，不为了测试擅自修改真实尺寸。可先完成独立代码/负例/故障恢复工作；实机需要更正内容时再明确请求用户提供目标格和正确值，不把fixture改值写成产品真实验收。
- R1不做PRD16.4旧正式输出替换，独立artifact先验证内容确实生效；R2另行完成可恢复发布/按款可识别目录。其他REPAIR/V1门仍开放。

本次文档子任务只新增本文件；未修改生产代码、测试、ADR、验收矩阵或共用状态，未执行构建、测试、真实桌面或 Photoshop 操作。主线程独占当前真实批次与 Photoshop。以下“新增/扩展/应当”均为后续实施要求，不是现有能力声明。

## 1. 目标、来源与边界

依据：[PRD 1.5](../../产品需求文档-电商详情页智能生产系统-PRD-1.5.md) §16.3、§16.4、§17、§18，以及自然语言的 §10.3；[V1 交付执行计划](V1交付执行计划-20260922.md) C 阶段；[验收矩阵](../验收矩阵.md) `REPAIR-01`，并联动 `SIZE-01`、`RENDER-01`、`FLOW-01`、`DATA-01`、`UI-01`。

目标：用户从正式结果页选择**当前批次、当前款、当前页、当前输出变体**，明确修改内容或布局，经本地校验、冻结新计划、单页执行和受控发布得到修复结果。非目标页、模板规则、源文件和未知执行记录不变；失败及重启后可恢复到明确的旧结果或待处理状态。

第一条实际修复链固定为：

> 已登记的动态尺码结果页 → 用户实际修改一个有依据的单元格 → 新尺码数据修订与确定性资源 → 新冻结计划 → 仅目标逻辑页追加一个 attempt → 正式执行并独立核验 → 新结果可见，旧正式输出保持。

这里的“新修订”是**当前页尺码数据/修复记录修订**，不是新模板修订。批次 `TemplateSnapshot`、`template_revision_id` 和模板资源保持原值。第一条链先沿用独立 artifact 提交保护旧文件，只关闭“实际修改已生效”的限定子门；PRD §16.4 的同页正式结果替换、不永久保留生成结果历史，由后续 R2 单独完成，不能遗漏。

明确不做：通用修复框架、整批重分析器、模板隐式学习、任意 Photoshop 命令执行器、商品生成/重画、自动补尺码值、安装发布及完整性能优化。现有“原计划重新生成”、禁用按钮、冻结编辑门禁、静态预览和测试计数均不能代替 REPAIR。

文中代码路径均相对于仓库根目录；方法名用于定位，避免并行工作造成行号漂移。实施前再次核对对应文件的新变更，不覆盖其他 agent 或主线程的工作。

## 2. 当前代码事实与缺口

| 边界 | 当前实际行为、来源 | 对本任务的含义 |
| --- | --- | --- |
| 批次计划准备 | `src/Desktop.Application/Rendering/RenderBatchCoordinator.cs` 的 `PrepareAsync` 发现已有 journal 即返回原 journal，不用新 preparation 替换它 | 保存新尺码再调用原“开始生成”不能保证采用新数据 |
| 原计划重生成 | 同文件 `PrepareRegenerationAsync` 占用 `_run`，按 style/page/variant/expectedAttempt 选页，追加 max attempt + 1，并固定 `RegenerationPageIndex` | 可以复用单页调度约束，不能把修改后的计划塞进此 API |
| 事实保护 | `src/Desktop.Application/Rendering/RenderPlanRegeneration.cs` 的 `Create` 仅更新 plan/idempotency/artifact/created_at/digest；`PreservesFacts` 剔除这些身份后要求其余 JSON 语义相同 | 尺码值、图片、placement、导出 PSD 意图变化均不是原计划重生成；必须保留这条严格校验 |
| 账本准入 | `src/Desktop.Application/Rendering/RenderBatchJournalValidator.cs` 仅支持 Version 1/2；`CanRegenerate` 要求批次已完成、完成有问题、取消或暂停，目标最新页为 Committed/Failed/Cancelled 且有计划，整批无 Submitting/Running/Unknown | Unknown 同级页也阻断；Blocked/无计划页不能借重生成变为可执行页；修复需独立受限追加分支 |
| 历史与执行范围 | `ValidateUpdate` 不允许改历史页、计划、已知执行/commit；Version 2 只更新选定索引。`RunAsync` 先核对已有执行，再只投递范围内 Planned 页 | 取消批次原有 Planned 页不能因修复复活；新修复版本必须继承同样隔离，不能仅在 UI 筛选 |
| SQLite 与终态重开 | `src/Desktop.Infrastructure/Persistence/SqliteDesktopStore.RenderBatches.cs`、`SqliteDesktopStore.RenderRegeneration.cs`：revision CAS、严格 JSON/hash；追加 journal、origin、`RenderPageRegenerationRequested` 事件和任务投影在同一事务。当前 `SqliteDesktopStore.Migrations.cs` 最新为 v17 | 现有 origin 重放会调用 `ValidateUpdate`/`PreservesFacts`，故不能只改写入口；历史 v1/v2 必须继续按原语义读取 |
| 任务计数 | `src/Desktop.Infrastructure/Persistence/SqliteDesktopStore.RenderCommits.cs`、`src/Desktop.Domain/Tasks/TaskStateReducer.cs` 按逻辑页计数，新的 attempt 不重复增加 output_count；`RenderBatchCoordinator.Finish` 保守保留历史 issue count | 修复成功不能靠删事件清除旧问题；“当前页已修复”和“历史曾有问题”需要不同展示，不在本任务偷改计数规则 |
| 尺码编辑 | `src/Desktop.Application/SizeTables/SizeTableEditingWorkflow.cs` 校验 expected revision，调用 `SizeTable.Edit`/保存。`src/Desktop.Infrastructure/SizeTables/SizeTableFileRepository.cs` CAS 后原子写该页 `current.json`，带 document/data digest | 尺码数据是文件仓储，不是 SQLite 表；当前只保留 current 文件，并没有修复修订与执行 intent 的跨存储提交链 |
| 尺码门禁 | `src/Desktop.App/SizeTableEditorViewModel.cs` 打开与保存时均读 `IRenderBatchWorkflow.LoadAsync`；有任意冻结 journal 或读取失败即阻断。Application 的尺码保存服务本身尚未实现此修复事务 | 这是避免“保存后仍渲染旧计划”的安全补丁；接新修复入口前不得删除门禁或让旧 SaveAsync 绕过它 |
| 编辑后的准备范围 | `src/Desktop.App/MainWindowViewModel.SizeTables.cs` 的 `RefreshSavedPageAsync` 经 `AiAnalysisViewModel.PrepareSavedSizePageAsync` 调用批次 `PrepareRenderingAsync`。`src/Desktop.Application/Ai/AiRenderPreparationService.cs` 遍历批次款式/页面、读取各尺码表并准备计划 | UI 最后重选一个页面不等于只重建一页；真正 REPAIR 必须有明确的单目标准备输入，不能重算整批后仅取一页掩盖副作用 |
| 尺码生产能力 | `src/Desktop.Infrastructure/SizeTables/WpfSizeTableRasterizer.cs`、`src/Desktop.Application/SizeTables/SizeTableRenderPlanBuilder.cs` 已有确定性 PNG、单元格原文/structured_data_digest、插入/替换校准、资源 hash 和计划校验；`AiRenderPlanFactory.CreateSizeTablePlanIdentity` 的身份输入含尺码 revision/data digest/raster | 复用这些生产者。计划不需要携带本机路径；新表内容必须与实际 PNG 和冻结 JSON 闭合，而非只更新时间或身份 |
| 渲染资源 | `src/Desktop.Infrastructure/Bridge/RenderBatchResourcePreparer.cs` 从批次快照核对模板，商品资源必须属于当前款；尺码 PNG 从应用规定的该页 resources 地址按逻辑 ID 读取并核 hash/长度 | 新修复资源仍须满足同一地址/用途/归属协议，不接受任意外部路径或市场尺码图直接渲染 |
| 正式提交 | `src/Desktop.Infrastructure/Bridge/RenderPageCommitter.cs` 的 `PreparedRenderPage.CommitAsync` 先 durable Verified，再将暂存目录移动到不存在的新 artifact 目录，校验后 Committed；已有目标目录则拒绝。`src/Desktop.Infrastructure/Bridge/RenderBatchOutputReader.cs` 只核验读取已提交 JPG | 已有“新 artifact 的完整提交”，没有“替换当前逻辑页旧正式文件”的发布协议；当前新 attempt 保留旧目录，不能声称已满足 §16.4 |
| 结果投影 | `src/Desktop.App/ResultReviewViewModel.Rendering.cs` 恢复最新 attempt，刷新输出；`ResultReviewPageState.cs` 区分最新执行与可用的旧 Committed 输出；重生成保留精确 variant 并返回带进度/控制的总览 | 可复用恢复、旧图保留、进度与选中项；新增修复需明确区分新修订待生成、新 attempt 失败、仍显示旧结果、发布成功 |
| 结果操作 | `src/Desktop.App/ResultReviewPage.xaml` 的移动缩放 Slider、自然语言 TextBox、Photoshop 编辑按钮仍禁用；候选区是显示而非当前页换图提交链；可用的“重新生成”仍走上述原计划入口 | 未实现结果页换图/几何编辑、自然语言修复、按需 PSD/手动回写。生成前 AI 选图能力不能直接冒充结果修复 |

多批次按 ID 恢复入口现位于 `src/Desktop.App/BatchInputWorkflowAdapter.cs`、`BatchInputViewModel.cs`、`MainWindowViewModel.cs`；它提升已有结果可达性，不修改计划或重投 Unknown，也不构成 REPAIR 完成证据。

## 3. 首条链的精确设计约束

### 3.1 操作身份与并发

新增一个窄的“尺码页修复”Application 用例，而不是让调用方提交任意新 RenderPlan。建议实现点为 `src/Desktop.Application/Rendering/SizeTablePageRepairService.cs`（拟新增），组合既有 SIZE 校验、栅格器、builder 与 renderer-neutral 渲染端口。

请求至少绑定：`repairRequestId`、batch/style/page/outputVariantId、expected journal revision、expected latest attempt、base plan digest、expected table revision/data digest、用户确认后的单元格草稿。`null` 变体是精确身份，不是所有变体的通配符。双击/超时重试同一 requestId 应返回同一已落盘意图；同 requestId 不同内容拒绝。

服务端在准备与提交边界核对：目标仍是同一最新页；基计划、表格来源和模板快照闭合；扫描/AI 后台已安全收口；编排器没有活动运行；整批没有 Submitting/Running/Unknown。UI 忙状态仅改善交互，不替代持久化 CAS、单 writer 和 `_run`/单 Photoshop 通道。出现冲突只返回可重读错误，不悄悄基于新状态重算并执行。

首个最小切片只接收**有有效冻结计划、有可验证结构化数据的动态尺码页**，优先使用已有成功输出的案例。无计划的业务红页、旧表与基计划 data digest 不一致、旧资源缺失/损坏均保持明确阻断；不能从旧 JPG 猜回表格，也不能将“无计划”改成成功。

### 3.2 新数据与新计划，旧事实不动

1. 从冻结批次/目标 plan 及当前可核验尺码文档获取基线；校验来源 asset 属于该款，来源 hash 不变，表格 data digest 与基计划相符。用户修改只应用于该页工作草稿。
2. 第一例只修改一个有用户/来源依据的非空单元格，保留列、行、单位、`-`、`/`、`\` 原文语义。不通过格式归一化、AI 补值或改 expected 值制造成功。后续行列增删继续使用 SIZE 现有有效性与尺寸门。
3. 生成 revision + 1 的候选尺码文档，保留 table/source/page 身份，重新计算 data digest；使用原页面校准样式生成独立确定性 PNG，不覆盖旧资源。根据新栅格尺寸重新核对该尺码对象的安全区域，放不下则要求处理，不扩展画布或猜测布局。
4. 使用冻结的模板修订、模板副本和尺码插入/替换校准，构建单页新计划。仅放行：明确确认的尺码结构、对应结构 digest/PNG 资源/hash/长度、该尺码对象必需的确定性 placement，以及新的计划/idempotency/artifact/时间/digest 身份。首切片不改导出 PSD 开关、文件名、模板内容、普通 targets、隐藏旧层规则或其他页资源。
5. 新计划必须通过原 Schema、RenderPlanValidator、模板结构证据和 SIZE 资源闭合校验。还须以修复类型的白名单比较基计划和新计划，证明所有未授权事实不变；仅通过 Schema 不够。预览与最终执行使用同一候选修订/digest。
6. 新 attempt 为选定逻辑页的 max + 1；历史 PlanJson、executionId、commit、旧 journal payload/hash 保留。原计划重生成继续走 `PreservesFacts`；新修复追加使用另一个严格校验分支，不把两者改成“任意合法 JSON 都可追加”。

### 3.3 文件与 SQLite 的提交顺序

现有 `SizeTableFileRepository.SaveAsync` 会推进 current 文件，不能采用“先调用它、再试着 append journal；失败时把用户文件改回去”的无记录双写方案。

建议最小方案：在既有 SIZE 文件仓储扩展**按修复身份保存/读取不可变候选修订**；表格内容与 PNG 先写应用自有 stage，flush、长度/hash 回读。随后在一个 SQLite writer/事务中，以 expected journal/attempt/data 基线 CAS 落修复请求、候选引用/digest、出生 origin、任务重开事件和唯一新 attempt。SQLite 事务不能冒充文件系统事务。

- 事务前的文件只是未获执行授权的候选；失败不得派发、不得推进正式结果。能证明未被引用且未派发的自有候选才可清理。
- 事务成功后即使 UI 未收到响应，恢复按 requestId/origin 找回同一个 attempt；不得重建另一个 attempt。恢复直接读该修订引用和已冻结 JSON，不读可变 current.json 重算。
- 冻结后的修复编辑器按当前选择的 plan/repair 修订加载。旧 `current.json` 的生成前编辑语义保持；不得把它与新修复账本同时当成可互相覆盖的真相源。修复修订何时成为“当前可编辑数据”随明确的目标 attempt/发布指针切换，见 D1。
- 旧任务若缺少可证明的原始表格修订，明确显示不支持安全继续编辑；允许另建正常批次的既有安全路径，但不能把另建批次计作本页修复。

### 3.4 执行、显示与首条真实门

复用 `RenderBatchCoordinator.RunAsync`、资源准备、bridge、独立媒体验证和新 artifact 提交，执行范围仅指向新追加的目标页。不要复用整批 AI `PrepareRenderingAsync`；必要的 builder 复用只做局部提取，不重构整个 AI/RENDER 模块。

新 attempt 执行/失败时旧 Committed 输出仍可读；UI 显示“尺码修订已保存，待生成/生成中/失败，当前显示旧结果”等真实状态。成功必须显示新输出与其 attempt/data digest，不能因为后台又返回旧 journal 而声称已修复。修复后返回原款、原页、原变体；暂停、取消、连接等待及 Unknown 继续可见。

首条实机门：正式 UI 真正修改尺码 → 新数据 revision、data digest、计划 digest 和 PNG hash 均按实际内容更新 → 只有目标页出现新 attempt/新 execution → 独立核对新 JPG/PSD 内修正单元格 → 旧输出全部原字节、其余页/变体/模板/源媒体 hash 不变 → 新进程按该 batchId 恢复，不再派发该页。此时只可标记“R1 限定链通过”，不能关闭 R2 或完整 REPAIR。

## 4. 最小合同与 ADR 变化清单

变更前阅读 [ADR-012](../架构决策/ADR-012-本地持久化提交与迁移恢复.md)、[ADR-014](../架构决策/ADR-014-renderer-neutral-RenderPlan-v1.md)；涉及人工 Photoshop 会话时再落实 [ADR-013](../架构决策/ADR-013-Photoshop回环Bridge安全协议.md)。[ADR-034](../架构决策/ADR-034-尺码校准模板包v2与索引兼容.md) 与 [SIZE→RENDER 合同](RENDER-01-SIZE-01渲染交接合同.md) 的模板校准、用途和索引保持，不因修一页升级模板包。

| 合同/边界 | 最小必要变化与兼容策略 |
| --- | --- |
| Application 修复命令 | 新增类型化的尺码修复请求/结果、基线 CAS 与幂等 requestId。调用者给草稿和目标，不直接给任意计划。`IRenderBatchWorkflow` 或其窄的修复端口需有明确的修改事实追加操作，与 `PrepareRegenerationAsync` 分开；端口选择在 D1 固化，不并存两套编排 |
| 尺码修订文件 | 增加不可变候选修订的保存/按身份加载及引用校验；复用已有表格语义和栅格器。若包装结构改变，建立新的修复 envelope 版本，不向既有 `size-table/v1` current 文件偷偷添加另一种语义。明确兼容旧 current-only 数据的可编辑准入 |
| Render journal | 推荐增加 Version 3 候选，显式区分原计划重生成和内容修复的出生类型，并绑定 repairId/基计划/候选修订证据；仍只有一个活动追加页索引。v1/v2 按原规则只读恢复，旧 payload 不回写；v3 后续追加不能降回 v2 丢失修复来历。字段形状与读取规则须先冻结 |
| SQLite/任务事件 | 在当前 v17 后追加新迁移（若主线已有后续迁移则顺延，不改旧 migration/checksum）。持久化 repair request 唯一键、来源/出生 revision/基 plan digest/新 plan digest/候选引用；与 journal 和任务重开同事务。建议新 `RenderPageRepairRequested` 事件区别原重生成；同步 reducer 和通用写口防绕过，保留输出计数与历史 issue 事件 |
| RenderPlan | R1/R3 的新内容可由现有 v1 尺码资源/普通 targets/absolute placement 表达，不因值变化升级 Schema；创建全新计划身份并重新严格验证。repairId/数据库路径/UI 草稿不塞进计划。若后续新增资源用途或人工编辑执行语义，再按 ADR-014 明确新 capability/显式迁移，破坏语义时新主版本 |
| 同页正式结果发布 | R2 必须新增可恢复的替换/当前输出登记协议，绑定旧/新 manifest digest、逻辑页、expected 当前输出、事务状态和短期回退副本。补 ADR-012；不把现有“不覆盖 artifact”的 `RenderPageCommitter` 直接改成 overwrite=true |
| 受限自然语言动作 | R4 新增桌面侧严格动作结构/版本、范围/数值/目标校验与确认。它不是 RenderPlan 或 Bridge 的任意命令字段；旧渲染端只接收最终合法计划 |
| 按需 PSD | 改 `editable_psd.enabled` 已违反原 `PreservesFacts`；R5 使用单独的修复/导出意图与新 attempt，从当前有效修复计划派生，不修改批次全局选项。不把 `verification_psd` 临时验证资源提升为正式 PSD |
| Photoshop 编辑回写 | R6 需要受认证、版本/能力协商的单页编辑会话及 open/finish/cancel/recover 语义、受控工作副本租约、文档/进程身份与回写 evidence。现有普通渲染提交不能代替此会话；未知能力严格拒绝，不加任意脚本/路径字段、不放宽旧普通输出校验 |

以上均为候选合同；本 brief 不直接改 ADR 或分配永久 ADR 编号。由主线程在实际实现切片开始前将必要决策固化，并保持现有契约负例不变。

## 5. 分阶段实施与完成门

### R0：冻结最小修复语义

产物：D1/D2 决策、R1 请求/状态/错误形状、journal/SQLite 兼容和 old-output 安全矩阵。明确“修复内容”与“原计划重生成”是两个入口。数据基线使用受控副本，不修改主线程运行中的真实批次。

完成门：给出旧版读取、新版拒绝/迁移、重复请求与崩溃窗口的可执行断言；没有协议不明的自动回退。R0 文档完成不算修复功能。

### R1：尺码实际修改的纵向链

- R1a：单页尺码候选修订、确定性资源和新计划构建；基线/差异白名单/陈旧版本校验。初始修改一个单元格；不同表格尺寸仍按已有校准规则安全拒绝或明确处理。
- R1b：修复 intent、origin、新 attempt、任务重开原子登记；恢复用冻结修订。扩展 journal/存储的修复分支与单目标调度，原重生成 `PreservesFacts` 不变。
- R1c：结果页 O-03 编辑器接新用例，明确显示“保存修复并生成当前页”；冻结批次不再走旧 SaveAsync。只有新用例可用且安全准入通过时开放，原门禁继续保护其他入口。接回原进度、失败恢复及精确选中位置。
- R1d：主线程串行完成 §3.4 的真实修改与新进程恢复；保持全部旧正式文件，记录新产物身份，证明不是原计划重跑。

完成门：R1a～d 均有证据；修改内容出现在真实输出而不是只在编辑器或新 JSON 中。R2 未完成时仍声明“独立 artifact 输出，尚未关闭正式替换门”。

### R2：同页原子发布与旧结果生命周期

按 PRD §16.4 实现当前逻辑页的正式结果替换。推荐保留内部不可变 artifact 作为核验材料，再通过显式页级 publication journal/CAS 切换用户看到的当前结果与导出目标；用户输出目录布局及稳定命名以 PRD §16.1 为准，不另造长期历史版本产品。

必须分别说明：应用内“当前结果指针”原子切换、用户可见 JPG/PSD 文件替换、失败回滚三者的边界。Windows 多个文件的先后 rename 不能直接宣称是 JPG+PSD 原子事务；目录移动到新位置也不等于替换已有页。实现前选择并验证目标卷上的页级提交/回退协议，外部文件修改/占用/卷不可用时保留旧结果，不能先删旧输出再试写。

只有本次用户明确请求、当前任务登记的目标页文件可被受控替换；同目录其他文件、其他批次、源图与源 PSD 不在授权内。现行 RENDER/AGENTS 的旧正式输出保护，不能通过本 brief 自动扩大为通用删除授权；R2 应先明确这项 PRD 定义的精确替换边界并更新相关交接决策。

短期回退副本在发布与恢复引用均收口后按归属安全回收；不展示永久结果历史。旧 attempt 的审计证据仍保留，但其历史 artifact 被合法回收后，恢复器必须识别“已被登记替代”，不能继续要求每个旧 Committed 文件都存在而卡死新任务。不得把文件缺失直接推断成已被替代。

完成门：同页新结果替换、JPG-only/JPG+PSD、外部读者/占用、每个提交窗口中断、新进程恢复、原目录非目标哨兵及无永久生成历史均验证；缺任一项不关闭 §16.4。

### R3：当前页换图与移动/缩放

先实现当前款已登记素材中的单框换图，再接直接拖动/等比缩放；复用 `TemplatePlacementResolver`、已有 `layout/absolute-placement-v1` 和既有 source/hash 绑定。每次绑定 batch/style/page/variant/frame、基 plan/repair revision；只允许真实校准框，不把所有智能对象当成产品位。

- Top-2/Top-3 及该款全部素材只作为候选；确认替换形成**页级人工选择修订**，不直接修改会影响同款其他页/变体的全局 AI session 锁。后续自动分析不能覆盖该人工选择，除非用户明确重置。
- 新图使用其自身已验证几何；缺少证据时走明确人工调整/待检查或受控单素材分析，不借旧图/主色/参考款几何，不整批重分析。计划使用高分辨率源，不用缩略图。
- 画布预览将屏幕坐标明确转换为文档像素，保持等比、已有蒙版/剪贴和允许的交集；不增加旋转、透视、镜像、生成填充能力。无解不静默越过模板/主体约束。
- 撤销/重做只作用于当前修复草稿；确认后形成新修订/新计划/唯一新 attempt，依赖 R1 的提交恢复和 R2 的发布，不发明第二套渲染管线。
- 无计划红页和补齐新素材的修复另列 R3b 子切片：从明确页面原因、当前款素材登记及冻结模板生成首个合法目标计划。新增素材必须显式登记/校验来源，不能靠全批重扫重写 manifest；没有该合同前不得声称已覆盖 PRD §18 的补图红页修复。

完成门：至少覆盖同款多个框、同页多个输出变体、换图后几何不同、取消草稿、重启、修改后失败保留旧结果；同款其他页/其他变体及模板规则保持原 hash/计划/人工选择。

### R4：自然语言支持边界

沿用 PRD §10.3：高频明确动作优先本地解析；口语组合可调用已有 AI 基础设施的文本模型，但响应必须是受限动作，不是 Photoshop 命令。动作至少含目标页/框、类型、方向、相对幅度、作用范围、置信度与是否需澄清，并绑定草稿 revision。

首个可验证集合：当前框左/右/上/下移动、等比放大/缩小、撤销/重做、“再一点”。“一点”采用已冻结的框相对步长，不用屏幕像素；“再一点”只沿用当前草稿的有效上一个动作，切页后不沿用。主体居中/头顶留白/完整显示只能基于当前素材已有或正式补充的主体证据做确定性计算。

多目标/指代不明先高亮或澄清；“更高级/更有感觉”、跨页批量修改、改模板规则、生成/修补商品、修改尺码事实、任意代码均不执行。新响应到达时选择/草稿已变化则丢弃或要求重新确认。所有动作先预览并可撤销，再由用户确认冻结；未知 AI 结果不盲目重发。视觉上传仍受既有授权、凭据、预算与输入边界保护，不因文字框引入新的隐式上传。

完成门：支持/不支持/歧义/过量数值/恶意命令/超时/迟到响应都有测试与 UI 证据；真实支持动作改变目标输出，不支持表达产生零变更。仅本地子集通过时明列子集，不宣称混合解析和视觉语义动作全部完成。

### R5：JPG-only 当前页按需 PSD

基于**当前有效的已修复计划**而非最早批次计划，为选定页新建导出意图、计划身份和 attempt，只将当前页 PSD 输出打开；JPG 仍按合同输出。不能修改批次的 IncludeEditablePsd 或让其他页补出 PSD。已有可验证正式 PSD 则从其受控副本继续编辑，不重跑整批。

`verification_psd` 是临时验证材料，可能已清理且不具有正式交付登记，不能直接把它复制/改名当作按需 PSD 完成。按需新生成的 PSD 仍须独立核验结构、商品智能对象、可编辑文字、蒙版/装饰/背景、ICC 和当前修复事实。

完成门：正式 JPG-only → 当前页新增可编辑 PSD → 产品负责人实际编辑验证；同款其他页不增加 attempt/PSD、原 JPG 在成功发布前不变。批量 PSD 性能承诺不扩展到此操作。

### R6：Photoshop 受控编辑会话与回写

R5 可交付 PSD 不等于 R6 已实现。另行冻结一个最小单页编辑会话：绑定 batch/style/page/variant/base publication/plan/PSD hash、Photoshop 进程与文档身份、受控工作副本和会话状态。只打开本页副本，正式 PSD 不作为在 Photoshop 中直接写入的工作文件；退出/重启不能按文档名碰巧相同而接管。

用户“完成编辑”后由受支持 adapter 保存该副本并导出对应 JPG，经独立校验和 R2 发布，刷新结果并回到原页；取消/断线/保存或回写未知保留旧结果及恢复现场，不自动重开或重导出。不得占用当前另一批次的运行/模态窗口，不强关用户已有未保存文档。

人工修改可能改变当前页文字/布局，现有普通渲染的“非目标内容必须与模板相同”不能无条件套用，也不能整体关闭。D4 必须定义人工授权的页内修改范围与独立 evidence：工作副本来源、文档身份、保存版本/hash、JPG/PSD 同次导出闭合、画布/ICC/可编辑结构和其他页零触碰；无法验证的编辑类型明确拒绝或保持待人工核验，不伪造“符合旧 plan”。

完成门：真实手动修改 → 受控保存 → JPG/PSD 同页更新 → 缩略图变化 → 旧页仅在成功提交后替换；保存中退出、PS 崩溃、换文档/进程、外部改动、回写失败和重启恢复均有证据。源码 PSD/模板规则不变。

## 6. 安全、隔离与恢复矩阵

| 时点/异常 | 必须行为 | 不允许 |
| --- | --- | --- |
| 用户改草稿但未确认 | 可撤销/取消，当前正式输出及批次快照不变 | 自动生成、自动改模板或推动全款 AI 锁 |
| 候选文件写好但 SQLite 未提交 | 无执行资格；恢复核对归属与引用，保留旧输出 | 见到 PNG/plan 文件就创建新执行 |
| intent/attempt 事务已提交、UI 响应丢失 | 同 requestId 恢复同一修订/attempt；用冻结 plan | 重新分配 ID，再追加一个 attempt |
| Submitting/Running/Unknown 或任何同级页未决 | 阻断新的内容修复；原通道查询、核对 checkpoint/执行证据 | 换 idempotency key 绕过 Unknown 或清空账本 |
| Verified 后发布前/后崩溃 | 核对 manifest、checkpoint、hash、marker 和 publication journal；完成原提交或恢复旧结果 | 重渲染、猜成功、重复计数 |
| 新 attempt 明确失败/取消 | 旧成功结果仍可用；显示新修复失败与旧结果的区别，保留目标身份 | 用旧图证明新修改成功、删除其他成功页 |
| 外置卷/文件锁/权限/磁盘空间问题 | 停在明确可恢复状态，释放能安全释放的自有资源；不改默认输出根 | 在 C 盘重建业务结果、改权限、填盘试验或覆盖无归属文件 |
| 修复发布与另一修改并发 | expected 当前输出/attempt/revision CAS，冲突后要求重读 | 静默覆盖新结果、复活陈旧候选或跨变体应用 |
| 人工 PS 会话中断 | 保留会话与工作副本证据，明确未完成；只在确认身份后恢复 | 按页名打开陌生文档、强关用户文档、把未保存画面当作新输出 |
| 清理 | 仅处理本请求自有且已无恢复/读取引用的临时对象；遵守归属/文件身份检查 | 按日期或目录递归删除源文件、旧正式输出或 Unknown 所需现场 |

## 7. 聚焦验证计划（本次均未执行）

### 7.1 必须保留的现有回归

以下文件已存在；只在对应实现或合同确实被改动时运行，不因文档新增重跑已绿套件。

| 项目/测试文件 | 必须保留的断言与精确 filter |
| --- | --- |
| `tests/Desktop.Application.Tests/RenderPlanRegenerationTests.cs` | 普通/动态原计划重生成仍保持全部冻结事实；`FullyQualifiedName~Desktop.Application.Tests.RenderPlanRegenerationTests` |
| `tests/Desktop.Infrastructure.Tests/RenderBatchCoordinatorTests.cs` | `AppendRejectsRewritingHistoryFactsOrExecutionScope`、`CancelledBatchRegenerationNeverDispatchesOtherPlannedPages`、`UnknownExecutionBlocksRegenerationAndLeavesJournalAndTaskUntouched`、origin 回滚/损坏/历史兼容；类 filter：`FullyQualifiedName~Desktop.Infrastructure.Tests.RenderBatchCoordinatorTests` |
| `tests/Desktop.Infrastructure.Tests/RenderPageCommitStoreTests.cs` | 新 attempt 不重复计数、提交四方闭合、身份不符拒绝、数据库失败回滚、旧 v14 迁移；`FullyQualifiedName~Desktop.Infrastructure.Tests.RenderPageCommitStoreTests` |
| `tests/Desktop.Infrastructure.Tests/SizeTableFileRepositoryTests.cs` | 现有 current CAS、损坏恢复、原文保存不退化；`FullyQualifiedName~Desktop.Infrastructure.Tests.SizeTableFileRepositoryTests` |
| `tests/Desktop.App.UiTests/SizeTableEditorViewModelTests.cs` | 冻结 journal 不能通过旧保存入口编辑、打开后才冻结的保存重查、不可读账本 fail closed、revision conflict；`FullyQualifiedName~Desktop.App.UiTests.SizeTableEditorViewModelTests` |
| `tests/Desktop.App.UiTests/ResultReviewViewModelTests.cs` | 精确变体、同级 Unknown 门禁、重生成连接/进度/恢复可达、旧成功输出与当前失败分开；`FullyQualifiedName~Desktop.App.UiTests.ResultReviewViewModelTests` |

计划生成部分按实际改动追加 `SizeTableInsertionTests`、`SizeTableRenderTargetTests`、`WpfSizeTableRasterizerTests`；普通几何修改追加 `TemplateRenderPlanBuilderTests`、`AiRenderPlanFactoryTests`；新增迁移/事件/严格字段则必须追加对应 Domain、Contracts 与 Infrastructure 测试，不用 UI 测试代替存储断言。

主线程统一命令形态：`dotnet test tests/<项目>/<项目>.csproj --no-build --no-restore -v:minimal --filter "<上述精确 filter>"`，先统一构建。本文不授权子线程并行构建共享输出目录。

### 7.2 R1 必须新增的反例与端到端断言

建议在现有测试项目新增窄的 `SizeTablePageRepairTests`（Application）和 `RenderPageRepairTests`（Infrastructure），UI 优先扩展上述已有类；这些是拟新增名称，不是已通过测试。

1. 确认改一个实际单元格，revision/data digest/PNG hash/新 plan 内容闭合；旧原文、旧 PNG、旧 plan、模板快照保持；相同内容不得伪装成已修改。
2. 只新增指定 batch/style/page/variant 的一个 attempt，其余页包含同页另一 variant 和原 Planned 页均不变、不投递；同 requestId 重试仍唯一。
3. 陈旧 journal/table/attempt/base digest、同 requestId 不同草稿、错误款/页/变体、换模板/导出选项的夹带修改、缺资源/错误用途全部零派发。
4. 目标 Unknown、同级 Unknown、活动扫描/AI/渲染、打开编辑器后才冻结/变忙均拒绝；命令直接调用也不能绕过服务端门禁。
5. 候选写入失败、事务前/后中断、origin/event/plan 任意一处篡改、重复恢复、UI 返回前进程退出均保持唯一意图；旧 v1/v2 payload/hash 不变，新版本被旧读取器拒绝而非忽略修复。
6. 使用生产 SIZE builder/rasterizer、SQLite store、coordinator 和只记录提交的受控 session 验证数据链；fixture 只证离线副作用边界，不冒充实际 PS 单元格变化。
7. 修改/执行失败时仍显示旧正式输出并标明其旧身份，刷新/返回/跨批恢复不将旧计划准备投影覆盖新修复；output_count 不重复、历史事件不删除。

### 7.3 实机质量门与证据

主线程在批准的独立测试批次/输出根串行执行，不能碰当前正在运行的批次或重跑旧 Unknown。记录软件/PS/UXP 完整版本、页/变体/attempt/计划及数据 digest、实际执行次数、提交/恢复状态、输入/模板/其他页及非目标哨兵 hash。证据不含凭据、客户图片字节或敏感路径。

- R1：实际尺码改值与独立输出逐格核验，旧文件不变，新进程恢复零新派发。
- R2：成功替换与每个双提交窗口故障，JPG/PSD 一致、旧结果可恢复、其他页不变，回退资源安全收尾。
- R3：换图、拖动/缩放与撤销，正确素材/变体/框，无越页影响；至少一次真实失败恢复。
- R4：支持命令在当前画布和最终页可见；歧义/不支持命令不执行；所使用的模型路径有真实而非模拟证据。
- R5/R6：从 JPG-only 实际按需 PSD，用户在受控副本做可见编辑后回写 JPG/PSD，正常退出及新进程恢复不重跑；取消/崩溃/未知仍保旧结果。
- UI：1100×760、100%/125% DPI、中文 IME、键盘/撤销重做/UIA、长列表返回位置与忙碌门禁。只修改现有结果/尺码编辑区，不扩展全局视觉重构。

## 8. 待决问题与可分派的最小实现切片

### 8.1 必须明确的决策

| 编号 | 决策点 | 推荐方向及阻断范围 |
| --- | --- | --- |
| D1 | 修复 intent、候选尺码修订、当前可编辑数据与新 attempt 的权威关系；journal v3 字段/SQLite origin/event 的形状 | 推荐不可变修订文件 + 单 SQLite 事务绑定意图/新 attempt；冻结页不以 current.json 重算。先固定严格重放规则，再实施 R1b；不阻断纯 SIZE 候选校验试验 |
| D2 | 稳定正式输出路径、当前输出指针、JPG+PSD 页级替换/回退、历史 artifact 退役与旧恢复引用 | 首链保持旧文件，R2 另行补 ADR-012 并验证 Windows 目标卷协议；未决时不得放开原地覆盖或关闭 §16.4 |
| D3 | 允许更改的事实白名单；多变体尺码数据如何以页级修订隔离；无计划红页/新增素材如何登记 | R1 只接受明确的一个 variant，别的 variant 继续引用原数据/计划；无计划红页不走原重生成。新增素材与首计划资格在 R3b 决定，不能隐式改全批 manifest |
| D4 | 人工 PS 编辑允许改变哪些页内内容，如何记录授权与验证回写；新编辑会话采用何种 capability/版本 | 单独受控编辑会话与 evidence，保留普通渲染严格校验；需要 ADR-013/014 的最小增补。R6 前必须定案，不阻断 R1～R4 |
| D5 | 自然语言动作参数/相对步长、歧义阈值、文本模型与视觉补充范围，以及修复后历史 issue 的展示 | 本地明确动作先做；数值/语义未冻结不猜。历史审计计数不删，用页级最新状态说明本次是否已修复；若要改任务聚合另列 FLOW 决策 |

这些是实现前应由主线程落实的具体合同选择，不是要求每个可逆本地步骤重新向用户索取批准；只有来源、作用范围或风险变化时再按 AGENTS 处理授权。

### 8.2 可直接分派

| 切片 | 最小交付 | 文件归属与依赖 |
| --- | --- | --- |
| A：单页 SIZE 候选与计划 | 窄 `SizeTablePageRepairService` 的候选构建部分、严格差异校验与 Application tests；不写 journal、不接 UI、不调 PS | `src/Desktop.Application/Rendering/` 的新专项文件，必要的 `SizeTables/` builder 局部复用；由 D1/D3 固定输入。不得顺带重构整批 preparation |
| B：持久化与单目标执行 | v3/origin/repair 事务、幂等 CAS、恢复、新任务事件与 Infrastructure/Domain tests；原 `PreservesFacts` 回归原样通过 | 独占 `RenderBatchContracts/Coordinator/JournalValidator`、SQLite render partial/migrations、相关 task event/reducer；依赖 R0/A 合同，禁止与另一个改相同文件的 agent 并行 |
| C：正式结果 UI 尺码修复 | 结果页/尺码编辑接唯一新用例，忙碌/错误/恢复/选中身份准确；UI tests | 独占 `SizeTableEditorViewModel`、`MainWindowViewModel.SizeTables`、`ResultReview*` 必需接线；依赖 A/B，不提前取消冻结门禁 |
| D：R1 真实限定门 | 新测试批次实际改单元格、只执行目标页、独立核验与新进程恢复记录 | 主线程独占真实 UI/PS/输出，A～C 子线程只读审查；不复用主线程当前活动批次进行故障注入 |
| E：R2 页级发布 | 只在 D2 冻结后实现当前页替换与故障恢复，不扩大到通用文件管理 | 独占输出 committer/reader、publication 存储与结果投影；在 R1 已有真实证据后接入 |

R3～R6 沿以上同一修复记录、单页 attempt 和发布链逐项扩展；不要一开始做插件体系、通用动作引擎或第二个任务调度器。可并行的是不争抢同一文件/合同的纯候选测试、持久化设计审查和 UI 交互准备；真实 UI、构建与最终合同写入由主线程串行集成。

## 9. 交付与验收声明

每个切片交付：变更文件及具体行为、相关 ADR/版本变化、精确测试 filter 与实际结果、源/旧输出保护检查、剩余真实门。主线程统一更新矩阵/状态/计划，本任务不修改这些共用文档。

完整 REPAIR-01 只有在实际尺码/换图/几何修复、自然语言支持边界、按需 PSD、PS 回写、正式原子替换、页/变体隔离及失败恢复都有对应真实证据后才能关闭。R1 的独立新 artifact 成功、原计划重生成、门禁补丁、历史 PSD 可编辑证明以及本任务书的存在，都不能单独触发完成。

下一可执行项：主线程确定 D1/D3 的最小合同后分派 A；B 对该合同实施事务/恢复；C 最后开放冻结结果的真实尺码修复入口。真实运行继续由主线程安排，不打断当前正式批次。
