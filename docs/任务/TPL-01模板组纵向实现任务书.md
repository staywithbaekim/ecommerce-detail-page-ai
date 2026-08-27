# TPL-01 模板组纵向实现任务书

## 1. 任务信息

| 项目 | 内容 |
| --- | --- |
| 任务编号 | `TPL-01` |
| 状态 | 实施中；`TPL-S0` 已通过，当前进入 `TPL-S1` |
| 建立日期 | 2026-08-27 |
| 目标阶段 | 阶段 1：模板组管理、校准、增量修订和批次快照纵向实现 |
| 产品基线 | PRD 1.5 |
| 设计输入 | 页面信息架构与交互规格 v1.2；冻结的 HIFI-01/HIFI-02 中 T-01 至 T-05 |
| 技术输入 | 总体技术方案 1.4；ADR-010、ADR-011、ADR-012、ADR-014；RenderPlan v1 Schema |
| 工程基线 | `ce4c693 feat: establish desktop foundation` |
| 适用环境 | Windows 11 x64；WPF + .NET 10 x64 |

## 2. 任务目标

在阶段 0 的正式五层工程中完成一条可实际操作、可持久化、可恢复、可验收的模板纵向链路：

`模板库 -> 导入源 PSD 的内部副本 -> 页面设置 -> 图片框绑定与拖拽校准 -> 原子提交模板修订 -> 增量添加页面 -> 创建批次模板快照 -> 导出/导入模板包`。

本任务的核心验收场景是：一个已校准五页模板组增加第六个 PSD 页面时，只配置和校准第六页；原五页稳定 ID、PSD 副本和规则不变；提交失败继续使用旧修订；更新前创建的批次继续使用原快照；后续新批次默认看到六页。

任务必须交付真实领域行为和本地持久化证据。页面截图、mock、测试数量或 Figma 画板不能单独关闭 `TPL-01`。

## 3. 执行基线与约束

实施前按以下顺序读取：

1. `AGENTS.md`；
2. `产品需求文档-电商详情页智能生产系统-PRD-1.5.md`；
3. `docs/当前项目状态.md`；
4. 本任务书；
5. `docs/验收矩阵.md` 的 `TPL-01`、`UI-01`、`DATA-01` 相关行；
6. ADR-010、ADR-011、ADR-012、ADR-014；
7. 交互规格 T-01 至 T-05 和高保真 T-01 至 T-05 交付记录；
8. 只有在复核 PSD 结构合同时才读取明确引用的 UXP 历史验证证据。

始终遵守以下硬边界：

- 用户源 PSD、代表款照片、人工结果和市场尺码图只读；所有实验只操作明确的内部副本或测试副本。
- 不把每个智能对象都当作可替换图片框；图片框必须有用户选择或已验证结构证据。
- 不生成、重画或发明商品图片，不猜缺失素材或尺码值。
- AI 和产品规则不得进入 Photoshop/UXP 适配器。
- 模板规则输出保持 renderer-neutral，可生成符合 RenderPlan v1 的目标结构。
- 不引入账号、支付、云同步、平台上传、商家工作区、自动更新或多机共享数据库。
- 不提交 PSD、源图、人工结果、模板包样本、数据库、缓存、日志、临时预览或凭据。

## 4. 本任务范围

### 4.1 必须实现

1. T-01 模板库的列表、空状态、详情、编辑、另存、导出、导入、删除确认和未完成任务引用阻断。
2. T-02 的 PSD 选择、有效性检查和拖拽校准入口。
3. T-03 的内容哈希重复检测、用户确认、页面命名、排序、内容类型、生成方式和组合容量校验。
4. T-04 的图片框注册、结构证据展示、代表款素材列表、单框单图拖放、移动、缩放、撤销、重做、规则文字和布局意图编辑。
5. 内部 PSD 副本创建、长度和 SHA-256 读回校验、源文件前后哈希保护、失败副本隔离或清理。
6. `TemplateGroup`、不可变 `TemplateRevision`、稳定 `TemplatePage`、`FrameBinding`、校准草稿和预览状态领域模型。
7. 已校准模板组一次添加一个或多个页面，只让新增页或用户明确改变结构的原页进入待校准。
8. 模板修订的文件 manifest、SQLite 记录和 `current_revision_id` 原子切换；失败时旧修订继续可用。
9. `BuildBatchSnapshot`：创建时冻结修订、页面顺序、页面选择和规则摘要；分析开始后拒绝切换快照。
10. 尚未分析草稿显式更新到最新修订的应用命令和确认数据；不得静默新增页面。
11. 模板包导出/导入、完整性校验、版本拒绝、冲突处理和原子落库；包中不得包含 Key、任务历史或客户源照片。
12. 从校准结果构造 RenderPlan v1 目标片段，并以正式 Schema 验证结构证据、路径顺序和替换栈合同。
13. T-01 至 T-04 的 WPF 实现、UIA、键盘后备操作、中文输入和 1440x900/1100x760 布局证据。

### 4.2 延后但必须保留接口边界

- “从人工结果学习”的真实 AI 推断由 `AI-01` 实现。本任务只建立供应商无关的校准推断端口和人工复核数据边界，不得用 mock 宣称该路径可用。
- T-05 的 Photoshop 准确示例由后续真实 Photoshop/bridge 集成完成。本任务可建立页面状态和 `IPhotoshopProbe` 端口，但保存模板不得依赖该端口，mock 结果不计真实预览证据。
- 动态尺码表页可配置为页面内容类型，但结构提取、确定性绘制和尺码编辑器由 `SIZE-01` 实现。
- `BuildBatchSnapshot` 必须真实可用，但 B-01 完整批次输入识别、AI 分析和生产队列不属于本任务。

### 4.3 明确不实现

- 真实火山方舟请求、模型或 Prompt 选择、候选排序和素材语义分析。
- 正式 Photoshop 启动、bridge 监听、UXP 渲染、JPG/PSD 输出和批量性能验收。
- 普通文字自动替换、生成式图片、任意可视化规则编程器。
- 模板历史版本浏览、云模板库、多人共享、网络盘数据库或自动同步。
- 为任意 PSD 猜测图层含义，或为通过测试降低结构门禁。
- 完整 `DATA-01` 的模板库跨卷迁移；本任务只保证单个模板修订和模板包导入的提交安全。

## 5. 完成条件

### 5.1 主链路

- 用户可从模板库创建模板组，导入一个或多个有效 PSD，完成页面设置和拖拽校准并保存。
- 保存后重启应用，模板组、当前修订、页面、绑定、规则和预览状态可完整恢复。
- 模板库、详情和编辑流程使用真实数据投影，不显示阶段 0 模拟模板数据。
- 用户可跳过 Photoshop 示例效果保存，模板明确记录“未预览”而仍可用于创建批次快照。

### 5.2 增量修订

- 五页模板添加第六页后，只第六页为待配置/待校准；原五页只读显示“已校准 / 原规则保持”。
- 成功提交后原五页 `page_id`、PSD hash、`FrameBinding` 和规则 digest 逐项不变，第六页取得新的稳定 ID。
- 新页插入中间只改变新修订及后续批次的顺序，不改旧批次快照或历史输出记录。
- 在复制、hash、结构检查、manifest 写入、SQLite 提交和当前指针切换处分别注入失败，均不得产生半修订或丢失旧修订。

### 5.3 批次快照

- 更新前创建的批次在模板组变为六页后仍读取原五页快照；重启恢复结果一致。
- 更新后创建的新批次默认读取六页当前修订。
- 未分析草稿只有经用户明确确认才能更新到六页；分析中及之后状态一律拒绝切换。
- 任何计划构造入口只能读取 `BatchTemplateSnapshot`，不能运行时跟随 `TemplateGroup.current_revision_id`。

### 5.4 原文件与数据安全

- 全部批准测试样本在导入、校准、提交、失败回滚、导出和删除后源文件 SHA-256 不变。
- 删除模板只删除应用内部副本、修订和规则；源 PSD、代表款素材、人工结果和正式输出不受影响。
- 模板包不含 Key、凭据、任务历史、绝对本机路径、代表款源照片或人工结果；内部 PSD 副本是模板包的必要内容。
- 未完成任务引用模板时删除被阻断，并给出可定位任务入口。

### 5.5 契约与界面

- 所有已注册图片框均有 `psd_bottom_to_top` 路径、目标类型、可见性、剪贴、蒙版、透明度、混合模式、稳定层 ID/结构签名和逐层替换栈证据。
- 基于校准夹具构造的 RenderPlan v1 正例通过；路径顺序缺失、结构证据缺失、未注册框、旧 `superseded_paths` 和任意 Photoshop 命令被拒绝。
- T-01 至 T-04 的关键状态忠实落地冻结交互与视觉；1100x760、100%/125% DPI、Tab 顺序、可见焦点、中文输入、UIA 和长列表不遮挡。
- 拖拽不是唯一操作方式；键盘或明确按钮可选择素材并绑定到当前图片框。

## 6. 领域模型与不变量

| 模型 | 最小字段 | 不变量 |
| --- | --- | --- |
| `TemplateGroup` | `group_id,name,current_revision_id,status,created_at,updated_at` | 当前指针只指向一个已提交修订 |
| `TemplateRevision` | `revision_id,group_id,revision_no,state,manifest_ref,rule_digest,created_at` | 提交后不可变；失败修订不得成为当前修订 |
| `TemplatePage` | `page_id,revision_id,order,name,content_type,generation_mode,variant_capacity,enabled,template_resource_id` | 继承页面在修订间保持稳定 `page_id`；组合模式之外不得保存容量 |
| `FrameBinding` | `frame_id,page_id,layer_path,expected_layer,superseded_layers,selection_requirement,layout_intent` | 只有显式注册框可替换；一个框只绑定一张源图 |
| `CalibrationDraft` | `draft_id,base_revision_id,affected_page_ids,state,last_event_seq` | 原页默认不进入影响集；草稿可恢复但不能被批次使用 |
| `CalibrationSample` | `frame_id,asset_ref,geometry,confirmed,source` | 只引用用户选择的只读素材；不进入模板包 |
| `TemplatePreviewState` | `revision_id,page_id,status,evidence_ref,created_at` | `NotPreviewed` 不阻止提交；mock 不得写成 `Succeeded` |
| `BatchTemplateSnapshot` | `snapshot_id,batch_id,revision_id,pages,rules_digest,created_at` | 创建后不可变；分析开始后不可切换 |
| `TemplatePackageManifest` | `schema_version,package_id,group,revision,files,created_at` | 仅相对路径；每个文件有长度与 SHA-256；未知主版本拒绝 |

页面内容类型固定为 `NormalComposition` 和 `DynamicSizeTable`；生成方式固定为 `OncePerProduct`、`OncePerVariant`、`VariantCapacityCombination`。这些是产品规则，不得以中文显示文案作为持久化值。

`selection_requirement` 和 `layout_intent` 是品类中立的结构化文本/值对象；不得内置“正面、背面、领口、颜色”等服装枚举。服装词只允许出现在用户规则或测试样本内容中。

## 7. 状态机与命令

### 7.1 校准修订状态

主流程：

`Draft -> ImportingCopy -> DuplicateReview(optional) -> StructureInspecting -> FrameBinding -> Calibrating -> AwaitingReview -> Previewing(optional) -> Committing -> Committed`

允许的失败终点为 `ImportFailed`、`InspectionBlocked`、`CommitFailed` 和 `Abandoned`。失败后保留可恢复草稿或回到旧修订，不能创建伪成功修订。

| 转换 | 守卫 | 幂等/恢复证据 |
| --- | --- | --- |
| `Draft -> ImportingCopy` | 源路径可读且扩展/签名允许；导入意图确定 | `source_hash + import_intent_id` |
| `ImportingCopy -> StructureInspecting` | 内部副本长度/hash 与源一致 | 临时副本 manifest |
| `StructureInspecting -> FrameBinding` | 画布和层树可读；不自动注册所有智能对象 | inspection schema/version + source hash |
| `FrameBinding -> Calibrating` | 每个注册框具有完整结构证据 | `frame_id + structure_signature` |
| `Calibrating -> AwaitingReview` | 所有受影响启用页的必需框有示范和规则 | calibration revision/event sequence |
| `AwaitingReview -> Committing` | 页面配置有效；增量页集合与 base revision 一致 | `commit_intent_id + expected_current_revision_id` |
| `Committing -> Committed` | 文件 manifest、ready marker、SQLite 事务和当前指针切换一致 | revision manifest hash + commit marker |

### 7.2 必需应用命令

- `CreateTemplateDraft`
- `ImportTemplatePages`
- `ResolveDuplicatePages`
- `ConfigureTemplatePage`
- `ReorderTemplatePages`
- `RegisterFrameBinding`
- `PlaceCalibrationAsset`
- `UpdateCalibrationGeometry`
- `UpdateSelectionRequirement`
- `UndoCalibrationAction` / `RedoCalibrationAction`
- `CommitTemplateRevision`
- `AddPagesToTemplateGroup`
- `SaveTemplateAsNewGroup`
- `BuildBatchSnapshot`
- `UpdateDraftBatchSnapshot`
- `ExportTemplatePackage`
- `ImportTemplatePackage`
- `DeleteTemplateGroup`

每个写命令携带命令 ID、期望版本和取消令牌；重复提交必须返回同一结果或明确幂等结果。UI 不得直接写 SQLite、复制文件或修改 `current_revision_id`。

## 8. 分层职责与接口

### Domain

- 模板修订、页面、绑定、草稿、快照和值对象。
- 页面配置、结构证据完整性、增量继承、修订提交、快照冻结和删除引用守卫。
- 不引用 WPF、SQLite、文件路径 API、PSD 库、HTTP、AI 或 Photoshop 类型。

### Application

- 编排上述命令、草稿恢复、进度事件、撤销/重做、查询投影和错误翻译。
- 依赖 `ITemplateRepository`、`ITemplateRevisionCommitter`、`IPsdInspectionPort`、`ICalibrationPreviewPort`、`ITemplatePackagePort`、`IBatchSnapshotRepository` 和现有文件/日志端口。
- 对 UI 返回稳定的 `code/retryable/scope/user_action`，不暴露库异常和敏感绝对路径。

### Infrastructure

- SQLite 迁移与 repository；PSD 只读检查适配器；内部副本/manifest/marker；模板包 ZIP 适配器。
- PSD/压缩库 DTO 只能停留在 Infrastructure，并转换为供应商无关的 inspection DTO。
- 文件读取使用顺序流和有界缓存；大 PSD 不整文件加载进 UI 内存。

### Presentation

- T-01 至 T-04 的 WPF 页面、导航、步骤状态、弹窗、画布交互、UIA 和只读投影。
- 画布使用稳定尺寸与缩放视口；素材列表虚拟化；拖放、按钮绑定和键盘路径调用同一 Application 命令。
- UI 不根据页面数量、文件名或智能对象名称猜业务规则。

### Contracts

- PSD inspection DTO、模板 revision manifest、模板包 manifest 和 RenderPlan target 映射的版本化合同。
- 模板包 manifest 在实现导入/导出前冻结 JSON Schema、正反例和主版本拒绝规则。

## 9. 本地数据与文件布局

建议应用自有根目录内采用以下逻辑结构；实际根由现有 `IPathPolicy` 决定，不写死盘符：

```text
templates/
  groups/<group_id>/
    group-manifest.json
    revisions/<revision_id>/
      revision-manifest.json
      pages/<page_id>/template.psd
      pages/<page_id>/preview.png
      committed.marker
    drafts/<draft_id>/
      draft-manifest.json
      staging/
template-package-staging/<operation_id>/
```

SQLite 迁移至少增加 `template_groups`、`template_revisions`、`template_pages`、`frame_bindings`、`calibration_drafts`、`batch_template_snapshots` 和必要引用索引。迁移必须单调版本化并记录 checksum；不得修改已应用迁移文本，也不得在失败时删除旧数据库。

内部 manifest 只保存相对路径。源 PSD 和代表款素材的真实路径仅由受控本地引用存储处理，不进入日志、模板包或 RenderPlan。模板包使用 ZIP 仅作为容器，不接受 `..`、绝对路径、盘符、重复条目、符号链接或解压后逃逸目标。

## 10. 原子提交与崩溃恢复

新修订提交顺序：

1. 在 `drafts/<draft_id>/staging` 复制新增 PSD，并核对源/副本长度和 SHA-256。
2. 完成结构检查、页面配置、绑定、校准和领域守卫；生成不可变 revision manifest。
3. 在目标修订目录写临时文件，flush、读回长度/hash，再写 `ready.marker`。
4. 通过 SQLite 单 writer 在一个事务中插入修订、页面、绑定，并以 `expected_current_revision_id` 比较后切换当前指针。
5. 事务成功后写 `committed.marker`；清理无引用草稿。若 marker 与数据库不一致，启动恢复按 manifest/hash/事务记录判定，不猜成功。

至少注入并记录以下故障：复制中断、副本 hash 不一致、PSD 检查异常、manifest 写入失败、ready 后进程退出、SQLite 事务失败、数据库已切换但 committed marker 未写、旧位置不可写、模板包校验失败。任何故障后必须满足：旧修订可读、源文件不变、未提交修订不可被批次选择、可重试范围明确。

## 11. PSD 检查与本地预览技术门

### 【待技术验证】PSD 只读检查适配器

在编写正式 adapter 前，用现有批准 PSD 的明确副本对候选方案做一次有界验证：

| 方案 | 优点 | 风险 | 当前推荐 |
| --- | --- | --- | --- |
| A. 进程内受维护的 .NET PSD 库 | 部署简单、类型边界清楚、无额外进程 | 可能缺少剪贴、蒙版、智能对象或复杂层树证据 | 首选；全部通过才采用 |
| B. 隔离的本地 PSD 检查 worker | 可复用成熟解析能力，崩溃与 UI 隔离 | 增加制品、进程协议和发布面 | A 不满足结构门时采用 |
| C. 通过 Photoshop/UXP 检查 | 与真实 Photoshop 结构最接近 | 占用 Photoshop，不符合默认本地校准路径 | 仅作明确兼容性探针/对照，不作默认实现 |

验证输入：至少包含已验证单框页、四框复杂页、重复 PSD 和一份结构不支持/损坏反例，全部使用副本。

验证方法：读取合成预览、画布、色彩/位深、完整层树顺序、智能对象、可见性、剪贴、蒙版、透明度、混合模式、稳定层标识和结构签名；与已批准结构证据及 Photoshop 只读探针交叉比较；记录耗时、峰值内存、许可证和自包含发布影响。

通过标准：已批准目标路径和关键属性全部一致；不修改源/副本；损坏或不支持结构返回可执行阻断；代表 PSD 不导致 UI 冻结或无界内存增长。候选均失败时停止 `TPL-01` adapter 实施并形成技术报告，不手写通用 PSD 解析器救场。

该选择会影响部署和兼容范围，通过后必须形成或更新 ADR，再合入正式 adapter。

### 【待技术验证】校准预览一致性

对相同代表款素材，比较 WPF 本地预览与 Photoshop 准确预览的图片框边界、cover 裁切、缩放和偏移。通过标准不是像素完全相同，而是注册框无白边、几何换算一致且偏差不会改变用户校准意图；无法可靠提取蒙版或变换时明确阻断/要求兼容性检查，不猜坐标。

## 12. 模板包合同

模板包是跨电脑长期合同，正式实现前必须冻结 Schema 和 ADR。推荐 `template-package/v1`：ZIP 容器 + 根 `manifest.json` + 当前有效修订的完整内部 PSD 副本、规则、结构证据和允许的非敏感预览；不导出废弃修订、批次快照、任务历史、Key、日志、缓存、代表款源照片或人工结果。

导入流程必须先在 staging 中完成版本、条目白名单、路径安全、长度/hash、manifest 引用闭合和空间/写权限检查，再让用户处理同名或同 ID 冲突，最后以新本地 group ID 或明确替换策略原子提交。V1 不做模板包自动合并，也不因导入而强制重新预览；首次实际渲染仍受环境和结构探针约束。

## 13. 错误码边界

至少稳定定义以下错误族：

| 错误码 | 范围 | 用户操作 |
| --- | --- | --- |
| `TPL_SOURCE_UNREADABLE` | 导入页 | 重新选择或检查权限 |
| `TPL_SOURCE_CHANGED_DURING_COPY` | 导入页 | 文件稳定后重试 |
| `TPL_DUPLICATE_REVIEW_REQUIRED` | 页面 | 选择保留、继续或取消 |
| `TPL_PSD_UNSUPPORTED_STRUCTURE` | 页面/图片框 | 选择结构或运行兼容性检查 |
| `TPL_FRAME_EVIDENCE_INCOMPLETE` | 图片框 | 完成绑定 |
| `TPL_CALIBRATION_INCOMPLETE` | 页面 | 补齐必需框示范/规则 |
| `TPL_REVISION_CONCURRENCY_CONFLICT` | 模板组 | 重新载入当前修订 |
| `TPL_REVISION_COMMIT_FAILED` | 修订 | 继续旧修订并重试 |
| `TPL_BATCH_SNAPSHOT_FROZEN` | 批次 | 保持原快照 |
| `TPL_DELETE_ACTIVE_REFERENCE` | 模板组 | 打开未完成任务 |
| `TPL_PACKAGE_INVALID` | 导入包 | 选择有效模板包 |
| `TPL_PACKAGE_VERSION_UNSUPPORTED` | 导入包 | 使用受支持版本重新导出 |

日志只记录对象 ID、阶段、错误码、耗时和脱敏摘要；不得记录源绝对路径、PSD 内容、代表款图片内容或异常中的凭据。

## 14. 实施顺序与阶段门

### TPL-S0：PSD 检查技术门

- 交付：候选适配器对比、可复现命令、结构差异、许可证/发布影响和 ADR。
- 完成：第 11 节通过标准满足并确定正式 adapter。
- 非目标：不做业务 UI，不写通用 PSD 解析器。

### TPL-S1：领域模型、迁移和仓储

- 交付：模板/修订/页面/绑定/快照模型、领域守卫、SQLite 单调迁移、manifest 合同和 repository。
- 完成：稳定 ID、修订不可变、快照冻结、迁移回滚和架构测试通过。
- 非目标：不实现 WPF 画布和真实 PSD 操作。

### TPL-S2：源 PSD 导入与重复检测

- 交付：只读选择、内部副本、hash、inspection、重复确认和错误恢复。
- 完成：重复 PSD 不被自动删除；源 hash 前后不变；失败无半导入对象。
- 非目标：不开始 AI 或 Photoshop。

### TPL-S3：T-01/T-02/T-03 WPF 纵向链路

- 交付：模板库、详情、新建、页面设置、增量页面和重复确认状态。
- 完成：真实投影和命令接通；宽窄、UIA、键盘、中文输入通过；产品走查通过。
- 非目标：不把 Figma 截图作为控件实现。

### TPL-S4：T-04 校准编辑器

- 交付：三栏编辑器、稳定画布、框绑定、拖缩、规则、撤销/重做、草稿恢复和本地预览。
- 完成：单框/四框夹具可完成真实校准；重启恢复；无结构证据时阻断。
- 非目标：不启用真实 AI 学习和 Photoshop 准确预览。

### TPL-S5：增量原子提交与批次快照

- 交付：五页加第六页、原子修订、故障恢复、旧/新批次快照和草稿显式更新。
- 完成：`TPL-01` 核心验收场景及全部故障注入通过。
- 非目标：不实现批次扫描、AI 或渲染。

### TPL-S6：模板包与模板维护收口

- 交付：模板包 Schema/ADR、导出、导入、另存、删除与引用阻断。
- 完成：跨空白测试数据根导入后修订/规则/hash 一致；敏感内容扫描通过。
- 非目标：不做云同步、账号或自动合并。

### TPL-S7：纵向验收与交接

- 交付：验证记录、验收矩阵更新、任务书实施记录、当前项目状态和 reviewed diff。
- 完成：第 5 节全部证据齐全，`TPL-01` 才可标记通过；下一步才建立 `AI-01` 任务书。
- 非目标：不因 TPL 通过顺带宣称 `UI-01`、`DATA-01`、`AI-01` 或 `RENDER-01` 通过。

每个子阶段完成后先运行聚焦测试并审查 diff；只有涉及跨层合同或共享持久化时才扩大回归。不得在每个小改动后运行 Photoshop、安装、完整性能或全量发布矩阵。

## 15. 测试与验收证据

### 单元测试

- 页面配置组合、容量守卫、稳定 ID、增量继承、结构证据完整性、撤销/重做、修订状态机和快照冻结。
- 删除引用守卫、模板包条目路径规则和错误映射。

### 契约测试

- PSD inspection DTO、revision manifest、template package Schema 和 RenderPlan target 映射正反例。
- 未知版本、未知字段、绝对路径、路径逃逸、重复 ZIP 条目、hash 不匹配和引用不闭合必须失败。

### 集成测试

- SQLite WAL/迁移、单 writer、模板 repository、内部副本、manifest/marker、重启恢复和包导入导出。
- 五页加第六页、原五页逐字段不变、旧批次快照不变、新批次六页、草稿显式更新。

### 故障注入

- 第 10 节每个提交点至少一条可重复测试；恢复后核对旧修订可读、无半修订、无源文件变化。
- 并发双击保存、重复命令、应用在 ready/DB/marker 间退出、只读模板库和存储断开。

### UI 与人工走查

- T-01 列表/空/详情/删除，T-02 导入，T-03 新建/增量/重复，T-04 拖拽/待复核/窄版。
- 1440x900、1100x760、100%/125% DPI、中文/英文/数字/空格和长路径、UIA、Tab、焦点和键盘绑定。
- 产品负责人只需检查实际窗口的视觉与交互是否符合冻结设计；自动化负责重复状态和边界，不要求人工逐条重跑底层故障注入。

### 真实样本安全证据

- 仅使用 `需求样本/` 中明确批准的只读输入或其受控副本；执行前后记录 SHA-256。
- 证据文档只记录脱敏名称/对象 ID、hash、结构摘要和结果，不提交样本本体或包含商品图的截图。

### 必需验证记录

实施时创建 `docs/验证/TPL-01模板组纵向实现验证记录.md`，记录实际 SDK、依赖及许可证、构建/测试命令、测试环境、样本 hash、故障注入矩阵、UI 证据、未完成项和结论。命令必须先真实运行，再写入文档。

## 16. 需求追踪

| 基线 | 实现模块 | 必需证据 |
| --- | --- | --- |
| PRD 7.2、9、10 | 导入、页面设置、绑定、校准 | 内部副本、单框单图、规则/布局、保存与重启恢复 |
| PRD 7.6、8.3、18、22.2、25；ADR-010 | 增量修订、批次快照 | 五加一、原页不变、旧批次不变、草稿显式更新、失败回滚 |
| PRD 7.6、19、21.3、22.6 | 导入/导出/删除 | 包完整性、敏感内容排除、活动引用阻断、源/输出保护 |
| ADR-012 | SQLite/manifest/marker | 原子提交、崩溃恢复、单 writer、迁移失败保留旧数据 |
| ADR-014 | `FrameBinding`/RenderPlan | renderer-neutral 目标、结构门、Schema 正反例 |
| 交互 v1.2 T-01 至 T-05；HIFI-01/HIFI-02 | WPF Presentation | 代表状态、宽窄、DPI、UIA、键盘和产品走查 |

## 17. 待确认与待验证汇总

### 【待产品业务确认】

当前无。任务行为已能从 PRD 1.5、交互规格、ADR-010 和已冻结高保真确定；实现人员不得再次向产品负责人询问已经确认的页面数量、只校准新增页、示例可跳过或旧批次快照规则。

### 【待技术验证】

1. PSD 只读检查 adapter：按第 11 节依次验证 A/B/C，推荐 A，通过不了再采用 B；C 只作兼容性探针。
2. 本地校准预览与 Photoshop 几何一致性：推荐本地合成预览 + WPF 覆盖层；结构证据不足时明确阻断或按需兼容性检查。
3. 文件 ready/commit marker 与 SQLite 指针切换的崩溃恢复：推荐按第 10 节故障注入证明，不以代码审查代替运行证据。
4. 模板包跨数据根导入和版本拒绝：推荐 `template-package/v1` + manifest/hash + staging 原子导入；未知主版本拒绝。

### 待批准的重大技术决策

1. PSD inspection 正式实现及部署边界：在 TPL-S0 证据后形成 ADR；推荐进程内 .NET 方案，不能满足结构证据时采用隔离 worker。
2. 模板包 `template-package/v1` 的 Schema、冲突策略和版本迁移：在 TPL-S6 编码前形成 ADR；推荐只导出当前有效修订并为导入创建本地新对象身份，不做自动合并。

这些属于技术决策，由技术负责人依据证据作出并提交 ADR，不推给产品负责人选择。若验证会改变产品行为、交付范围或用户可见数据内容，再按 PRD 变更规则请求产品确认。

## 18. 停止与交接规则

- 同一 PSD 技术阻断连续出现两次、资源占用异常或批准方案无法满足结构证据时，停止 workaround 链并提交复现证据。
- 任何源文件 hash 变化立即停止测试，隔离应用自有副本并复核写入路径；不得删除或“修复”用户源文件。
- 发现需要改变 renderer-neutral RenderPlan、模板修订语义、模板包兼容或部署形态时，先形成 ADR，不在实现中静默改变。
- 本任务书已由产品负责人于 2026-08-27 批准。下一步在独立 Codex worktree 任务中开始 TPL-S0；`TPL-01` 验证记录、验收矩阵、当前状态和 reviewed diff 完成前不得交接 `AI-01`。

## 19. 实施记录

### 2026-08-27：TPL-S0 技术门通过

- 已确认执行起点包含 `217ec0b docs: approve TPL-01 implementation brief`，且开始前工作区干净。
- 已完整读取任务书要求的 PRD、当前状态、验收矩阵、ADR-010/011/012/014、交互规格和 T-01 至 T-05 高保真交付记录；阶段 0 工程未重做。
- 人工审批链恢复后完成候选 A/B/C 的证据性比较：`PhotoshopFile 1.0.0` 不存在；`Aspose.PSD 26.8.0` 的层树、评估预览、资源和商业许可未满足全部门槛；正式采用 `psd-tools 1.18.0` 隔离 worker，C 仅保留为兼容性探针。
- 8 个批准 PSD 副本全部读取成功，G03/G05 结构与批准证据一致；损坏/协议/JSON 负例、client 路径/缺失/畸形响应/超时/响应超限均稳定阻断。用户源 PSD、源图和人工结果 hash 不变。
- 打包 worker 不依赖客户机 Python；本地 cover/缩放/偏移预览与 Photoshop 人工参考的几何门通过。正式决策见 `ADR-017`，完整证据见 `docs/验证/TPL-S0-PSD只读检查技术门记录.md` 和 `docs/验证/TPL-01模板组纵向实现验证记录.md`。
- NuGet restore/漏洞索引经人工批准联网成功；solution 构建 0 警告/0 错误，50 项测试通过。下一具体动作是进入 TPL-S1 的领域模型、SQLite/manifest 和 repository，不把 S0 证据冒充为模板业务完成。
