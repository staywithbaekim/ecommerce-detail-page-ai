# SIZE-01 SIZE-S0 前置核对与金标冻结记录

## 1. 结论

2026-09-01 在任务书指定基线 `a60b54015cd195be2fc48c187e965bb483f04415` 完成 SIZE-S0 只读核对。`INPUT-01`、`TPL-01`、`AI-01` 和 `render-plan/v1` 的验收状态与具体生产者代码均存在，聚焦回归通过；现有 `AiRenderPlanFactory` 也确实保持“有结构化候选但等待 SIZE-01 栅格器”的安全停机。

SIZE-S0 **通过，SIZE-01 已解除前置阻塞并允许进入 SIZE-S1**。产品负责人于同日确认两张新提供的市场尺码图属于不同款式，应分别作为有效真实来源，不能在真实产品归属中互相判为冲突；同时批准用确定性结构化数据补充 `-`、`/`、`\`、空值、重复引用、冲突路由和低置信/不可解析场景。补充场景只验证产品规则、布局和状态机，不冒充额外真实市场图，也不使用生成式图片。

## 2. 本次范围与边界

- 目标：验证 SIZE-S1 的全部上游门，冻结已有产品规则，并判断真实金标是否足以开始领域合同实现。
- 约束：源 PSD、源照片、市场尺码图和人工结果只读；不重扫输入目录，不上传媒体，不发起真实 AI/Photoshop 调用；不把 fixture、设计截图或历史探测摘要冒充金标。
- 非目标：不实现 `SizeTable`、持久化、栅格器、O-03、RenderPlan 或 Photoshop/UXP 执行；不安装新依赖；不选择未经验证的字体或栅格引擎。
- 退出标准：上游生产者存在且通过聚焦复核；批准的只读金标覆盖有效、异常和冲突样本；没有通过猜测补齐单位、符号、缺值或冲突规则。

## 3. 基线与工作区

| 项目 | 结果 |
| --- | --- |
| Git HEAD | `a60b54015cd195be2fc48c187e965bb483f04415`，与任务书执行基线一致 |
| 分支状态 | worktree 为 detached HEAD；`codex/desktop-foundation-01` 包含该提交 |
| 既有修改 | 开始核对时已有 `项目总览.md`、`docs/当前项目状态.md`、`docs/验收矩阵.md` 的 SIZE-01 路由修改，以及未跟踪的 SIZE-01 任务书；均按用户既有工作保留 |
| 业务代码差异 | SIZE-S0 开始前 `src/`、`tests/` 无工作区差异；本次未修改业务代码或测试 |
| 媒体目录 | 当前 Codex worktree 未带入 `需求样本`；同一 Git 仓库的主工作树存在被 `.gitignore` 排除的 `需求样本/第一组`。本轮只读盘点并计算 hash，没有复制、重命名或写入；`git ls-files` 确认这些媒体未被跟踪 |

## 4. 上游验收与生产者复核

| 门 | 验收状态 | 当前基线生产者 | 结论 |
| --- | --- | --- | --- |
| `INPUT-01` | `docs/验收矩阵.md` 为“通过（已合并）” | `src/Desktop.Domain/Inputs/BatchDraft.cs` 提供 `Draft/Scanning/ScanReady/ScanBlocked` 与冻结模板快照；`src/Desktop.Application/Inputs/BatchInputContracts.cs` 提供 `AssetManifestV1`；`src/Desktop.Infrastructure/Inputs/AssetManifestFileStore.cs` 以 staging、digest、marker 和原子写提交 manifest；`docs/技术方案/schemas/asset-manifest-v1.schema.json` 只保存相对来源引用与内容 hash | 通过。SIZE-01 可以消费正式 `BatchDraft`/manifest，不需要也不得复制目录扫描 |
| `TPL-01` | `docs/验收矩阵.md` 为“通过” | `TemplatePageContentType.DynamicSizeTable` 已存在；`BatchTemplatePageSnapshot` 冻结 `ContentType`、页面/模板资源/生成方式，`BatchTemplateSnapshot` 只接受 committed 修订并在分析开始后拒绝切换 | 通过。动态尺码页与旧批次快照边界存在；当前快照尚未包含 SIZE-01 后续要新增的尺码样式/布局版本，这是本任务交付缺口而非上游验收失败 |
| `AI-01` | `docs/验收矩阵.md` 为“通过（S0-S7 已完成并合并）” | `AiAnalysisKind.SizeTableExtraction`、`AiSizeTableCandidateV1`、`ai-analysis-v1` Schema 和 `AiAnalysisResultSemanticValidator` 存在；候选携带 `source_asset_id`、confidence、columns、rows，语义门拒绝越界与不等长行；`AiBatchAnalysisWorkflow` 对多候选/provider conflict、低置信、未知结果和损坏结果 fail closed | 通过。AI 候选是 SIZE-01 输入，不等同于已确认 `SizeTable` |
| pending 缺口 | AI-01 已明确交接 | `AiRenderPlanFactory`：无候选返回 `BusinessBlocked/AI_SIZE_TABLE_CANDIDATE_MISSING`；有候选返回 `AwaitingSizeTableRasterization/AI_SIZE_TABLE_RASTERIZER_PENDING`，且不调用 Photoshop | 与任务书一致，后续只能在 SIZE-01 完成来源选择、结构校验和确定性资源后替换第二种 pending |
| `render-plan/v1` | ADR-014 已批准并冻结 | `docs/技术方案/schemas/render-plan-v1.schema.json` 允许 `deterministic_size_table`，动态页要求非空 `size_table`，普通页要求 `size_table=null`；计划只含逻辑资源、hash、结构证据和文件名 | 通过。禁止路径、token、命令、分析副本、市场源图和生成式图片进入计划 |

## 5. 需求追踪与已冻结产品规则

| 来源 | SIZE-01 约束 | S0 结论 |
| --- | --- | --- |
| PRD 9.1、13.1 | 动态尺码表是模板页面类型；一张有效来源自动使用；相同内容自动去重；多张内容冲突必须由用户选择；缺少来源为红色 | 冻结。相同内容以 INPUT-01 已提供的 SHA-256 为同一性证据，不按文件名或路径判定 |
| PRD 13.2 | 只提取网格中的表头、尺码、数值和单元格内单位；不提取广告、模特、联系方式、试穿建议和表格外说明 | 冻结 |
| PRD 13.3、ADR-014 | 不贴市场图、不生成式绘制；缺列省略、额外列保留；“建议体重”仅在存在时使用模板红色样式；固定内容随表格高度下移；资源角色为 `deterministic_size_table` | 冻结为产品级规则；具体字体度量、样式版本和布局参数仍须由批准样本/模板证据与 SIZE-S4 技术验证确定，不能在 S0 猜测 |
| PRD 13.4、14.3 | 无法可靠解析整体结构、来源冲突未选择或缺少必需来源时为业务红色；清晰正常样本不强制进入编辑器 | 冻结 |
| 任务书 3.1、总体方案 6.5、当前状态技术交接 | 单元格保留来源原文；合法 `-`、`\`、`/` 和单元格内单位不判为缺值，不补零、不换算、不擅自规范化 | 冻结。空白/无法确认的单元格不能用合法符号代填 |
| 交互 O-02/O-03 | 异常或主动纠错才显示编辑入口；来源冲突先选唯一来源；支持改单元格和增删行列；非法数据不可保存；保存并重生成只影响当前款当前尺码页 | 冻结 |
| PRD 18/25、ADR-014 | 旧结构、旧资源、旧计划和旧正式输出可恢复；新计划使用新 `plan_id`/幂等标识；局部更新不修改模板、其他页或其他款 | 冻结 |

## 6. 金标证据审计

### 6.1 真实只读金标

批准的真实来源已复制到当前工作树的 Git 忽略目录，仅作为只读验收输入。本表使用脱敏 ID 和仓库内相对位置，不记录原始聊天缓存绝对路径：

| 脱敏 ID | 忽略目录相对位置 | 类型/字节 | SHA-256 | 批准用途 |
| --- | --- | --- | --- |
| `size-real-a` | `需求样本/SIZE-01金标/真实来源/size-real-a-577fa4d19f919a7a.jpg` | JPG / 98,392 | `577fa4d19f919a7a7bbd64f07daa1102a34642c470e0967364f44543271a9e2b` | 款式 A 的有效市场尺码图；5 列、L–5XL、袖长原文 `/` |
| `size-real-b` | `需求样本/SIZE-01金标/真实来源/size-real-b-9231ad299120bba6.jpg` | JPG / 239,126 | `9231ad299120bba65af9391ba8a59097b7ff169197810a38971621df886d9fb0` | 款式 B 的有效市场尺码图；6 列、L–5XL、含小数 |

两项是不同款式的独立有效金标，不组成真实冲突组。副本 hash 与产品负责人提供的原文件一致，且均命中 `.gitignore` 的 `需求样本/` 规则、`git ls-files` 返回空；实现和测试只能按 hash/逻辑 ID 引用，不能保存源绝对路径或修改媒体。

`size-real-a` 的逐单元格预期：

| 尺码 | 衣长 | 胸围 | 肩宽 | 袖长 |
| --- | --- | --- | --- | --- |
| L | 61 | 102 | 40 | `/` |
| XL | 62 | 107 | 41 | `/` |
| 2XL | 63 | 112 | 42 | `/` |
| 3XL | 64 | 117 | 43 | `/` |
| 4XL | 65 | 122 | 44 | `/` |
| 5XL | 66 | 127 | 45 | `/` |

`size-real-b` 的逐单元格预期：

| 尺码 | 胸围 | 肩宽 | 袖长 | 衣长 | 袖口 |
| --- | --- | --- | --- | --- | --- |
| L | 99 | 39.5 | 51.5 | 64 | 28 |
| XL | 103 | 40.5 | 52.5 | 65 | 29 |
| 2XL | 107 | 41.5 | 53.5 | 66 | 30 |
| 3XL | 111 | 42.5 | 54.5 | 67 | 31 |
| 4XL | 115 | 43.5 | 55.5 | 68 | 32 |
| 5XL | 119 | 44.5 | 56 | 68.5 | 33 |

此外，`docs/验证/AI-01-S5-S7连续实现验证记录.md` 登记了一个真实 `clear-size-table` 分析来源：

- 内容 SHA-256：`577fa4d19f919a7a7bbd64f07daa1102a34642c470e0967364f44543271a9e2b`；
- 正式 AI 结果：confidence `0.99`，提取 L–5XL、衣长/胸围/肩宽/袖长，袖长原文 `/` 保留；
- 本轮核对确认该 hash 与 `size-real-a` 完全一致，因此历史 AI 提取结果可作为同一真实金标的既有端到端候选证据，不再是无法映射的孤立摘要。

这项历史证据证明 AI-01 曾在 `size-real-a` 的正式分析副本上返回结构化候选，并保留 `/`。SIZE-01 仍以本节逐格表为预期，不以历史 AI 输出自行覆盖金标。

O-03 的五列表格和现有测试 fixture 仅证明设计/合同或故障行为。按任务书，它们不能代替真实金标，也不能作为 SIZE-S0 退出证据。

### 6.2 产品负责人批准的确定性补充场景

以下场景是结构化测试/布局输入，不是新真实市场图；后续只能由 SIZE-S4 确定性栅格器生成无损资源，不得调用 ImageGen 或其他生成式图片能力：

| 场景 ID | 输入规则 | 预期 |
| --- | --- | --- |
| `derived-legal-symbols` | 在合法完整表格中，将“袖长”列分别以 `/`、`\`、`-` 三种参数实例化，实例内每个尺码行都保留同一原文符号 | 三种均为合法非空原文，可保存、可排版，不得转为数字或空值 |
| `derived-variable-shape` | 以批准真实表的原文单元格构造 4 行×5 列和 8 行×7 列版本，不增加推断值 | 行列和高度可变，额外列保留，行列关系仍需完整 |
| `derived-missing-cell` | 将一个必需单元格设为真正的空字符串，而不是 `/`、`\` 或 `-` | `ReviewRequired`/`Invalid`，保存和 RenderPlan 生成均被阻止 |
| `derived-duplicate-source` | manifest 对同一款式登记两个不同逻辑引用，但二者内容 hash 都为 `577fa4...9e2b` | 按 hash 自动去重，无需制造或提交第二个媒体文件 |
| `derived-conflicting-source` | 测试 manifest 把两个批准真实 hash 临时分配给同一个合成款式 | 必须要求用户选择唯一来源；此场景不表示两张真实图在实际业务中属于同款 |
| `derived-low-confidence` | 候选 confidence 低于可靠门或没有可靠候选 | 进入复核/红色，不猜值、不生成尺码页 |
| `derived-edit-correction` | 从缺值或冲突状态进入 O-03，由用户补全/选择并保存 | 新 revision 只重生成当前款当前尺码页，其他页和旧计划保持不变 |

本节同时冻结空值语义：`-`、`/`、`\` 是合法可见原文；只有长度为零的单元格才是缺失。冲突默认动作是用户选择，重复默认动作是按内容 hash 去重。

## 7. 聚焦验证

实际执行：

```text
dotnet restore EcommerceDetailPage.Desktop.slnx -p:NuGetAudit=false --verbosity minimal
结果：11 个项目恢复成功。

dotnet test tests/Desktop.Application.Tests/Desktop.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~BatchInputServiceTests|FullyQualifiedName~AiRenderPlanFactoryTests|FullyQualifiedName~TemplateWorkspaceServiceTests" --verbosity minimal
结果：30/30 通过。

dotnet test tests/Desktop.Contracts.Tests/Desktop.Contracts.Tests.csproj --no-restore --filter "FullyQualifiedName~AiAnalysisResultSemanticValidatorTests|FullyQualifiedName~JsonSchemaContractValidatorTests" --verbosity minimal
结果：48/48 通过。

dotnet test tests/Desktop.Domain.Tests/Desktop.Domain.Tests.csproj --no-restore --filter "FullyQualifiedName~TemplateDomainTests|FullyQualifiedName~BatchInputDomainTests" --verbosity minimal
结果：18/18 通过。

dotnet test tests/Desktop.Infrastructure.Tests/Desktop.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~BatchInputInfrastructureTests|FullyQualifiedName~AiBatchWorkflowRenderPlanIntegrationTests" --verbosity minimal
结果：17/17 通过。

$source = Split-Path -Parent ((git rev-parse --path-format=absolute --git-common-dir).Trim())
Get-FileHash -Algorithm SHA256 <三项批准目录候选的只读路径>
git -C $source check-ignore -v -- <三项仓库相对路径>
git -C $source ls-files -- <三项仓库相对路径>
结果：两张尺码候选和一个尺码页 PSD 的长度/hash 与第 6.1 节一致；只读盘点前后 hash 不变；三项均命中 `.gitignore` 的 `需求样本/`，`git ls-files` 无输出。
```

本轮没有运行完整 solution、WPF 实机、Photoshop、安装或性能矩阵；SIZE-S0 只关闭上游、金标和规则门，后续阶段必须按任务书补齐实现与相应验证。

收口检查：`git diff --check` 通过；五个本任务文档的尾随空白检查为 0；`src/`、`tests/`、`docs/技术方案/` 和 `docs/架构决策/` 的业务/合同差异为 0；未跟踪文件只有既有 SIZE-01 任务书和本记录；禁止媒体、数据库、日志、压缩包、二进制与 dump 为 0；本机绝对用户路径和常见凭据模式为 0。三项只读候选的收口 SHA-256 与第 6.1 节首次盘点一致。

## 8. S0 退出与下一步

- 原阻塞码 `SIZE_S0_APPROVED_GOLDEN_SET_UNAVAILABLE` 已于 2026-09-01 由产品负责人提供的两张真实来源、逐格确认和确定性补充场景解除。
- SIZE-S0 通过；SIZE-S1 可以开始。`SIZE-01` 整体仍未通过，S1 至 S7 的实现、WPF、布局、RenderPlan 和真实集成证据仍须完成。
- 下一动作：按本记录冻结的空值、符号、重复和冲突语义实现领域合同与状态机；若实现需要改变这些产品规则，必须先回到产品确认，不得在代码中静默推断。
- 不采用的替代：不会把派生场景冒充真实媒体，不会生成商品/尺码来源图，也不会把不同款式的两张真实图误判为真实冲突。
