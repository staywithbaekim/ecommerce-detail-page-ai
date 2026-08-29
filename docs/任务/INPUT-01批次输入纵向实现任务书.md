# INPUT-01 批次输入纵向实现任务书

## 1. 任务信息

| 项目 | 内容 |
| --- | --- |
| 任务 ID | `INPUT-01` |
| 状态 | 已批准，已完成；待主任务复核提交与合并 |
| 建立日期 | 2026-08-28 |
| 执行分支 | `codex/input-01-batch-input` |
| 起始基线 | `3c5d70d docs: gate AI work on INPUT baseline` |
| 产品批准 | 产品负责人已在源任务批准建立并持续执行本任务；本任务只映射既有 PRD/方案/B-01/验收矩阵，不新增产品选择 |
| 后续门 | 本任务通过、reviewed diff 并合并 `codex/desktop-foundation-01` 后，主任务才可恢复 `AI-01` |

## 2. 目标、约束与非目标

目标是交付从真实 WPF B-01 到可供 AI-01 直接消费的持久化 `BatchDraft`、目录扫描状态和 `asset-manifest/v1` 的正式纵向链路。完成不是建立类型、页面骨架或测试夹具，而是普通用户能够选择模板、输入目录、输出目录和页面，执行只读扫描，看到真实识别结果，并在重启后恢复同一批次、款式、资产与模板选择。

必须遵守：

- 只读取用户明确选择的目录及其一级子目录，不递归推断更深目录归属；
- 源图片永远只读，不重命名、移动、改写、覆盖或删除；
- 只允许应用自有目录承载 manifest、marker、缩略图、缓存和临时数据；
- 品类、款式和资产模型不得写死服装、颜色、正面、背面或固定素材角色；
- `BatchTemplateSnapshot` 必须绑定已提交的模板修订和本批次已选页面，分析开始后不得静默切换；
- WPF 复用 `src/Desktop.App/Styles/DesignSystem.xaml`，业务规则以 PRD/交互规格为准，冻结 Figma 只作视觉输入；
- 普通界面只显示中文、可行动文案，不暴露绝对路径日志、内部 ID、结构签名或异常堆栈。

本任务不实现 AI 语义分析、变体归组、候选排序、火山方舟调用、动态尺码表、Photoshop 渲染、完整 FLOW/RENDER/REPAIR、账号、支付、云同步或平台上传；不重做 WPF/.NET 骨架、模板模型、SQLite/WAL 基础、模板快照或 `DesignSystem.xaml`。

## 3. 输入基线与依赖门

本任务已按仓库唯一入口完整复核：

1. PRD 1.5 第 3.1、6、7.4、8.1、8.2、18、20、21.3、21.4、22.2、22.4；
2. `docs/技术方案/桌面端V1总体技术方案.md` 1.4 的 5、6.2、7、8.2、8.6、9.1、9.2、9.5、9.6、10、11、12.3、13、14；
3. `docs/设计/桌面端页面信息架构与交互规格-v1.2.md` 的全局交互、B-01、B-02、状态模型和 PRD 追踪；
4. `ADR-010` 批次模板快照、`ADR-011` WPF/.NET 10、`ADR-012` SQLite/manifest/恢复；
5. 冻结 HIFI B-01：默认宽版 `162:3`、窄版 `177:671`、页面选择 `170:122`/`181:767`、混合 `173:276`、空目录 `174:377`、零页面 `175:473`、输出冲突 `176:569`。

启动证据：`codex/desktop-foundation-01` 与本任务起点均指向 `3c5d70d`；`TPL-01` 验收行为为通过，代码中存在已提交模板修订、`BatchTemplateSnapshot`、SQLite v1-v4/WAL/单 writer、原子文件写入和全局 WPF 资源。全仓检索确认尚无正式 `BatchDraft`、`ScanReady`、`ScanBlocked`、`IFileInventory`、`IAssetMetadataReader`、`IBatchRepository` 或专用 asset manifest，符合任务启动前提。

总体方案旧的阶段摘要未单列 INPUT；`3c5d70d` 中的项目总览唯一执行顺序已明确以 `INPUT-01 -> AI-01` 覆盖旧路由，本任务不得跳过。

## 4. 正式合同与不变量

### 4.1 领域合同

- `BatchDraft`：稳定 `batch_id`、扫描状态、输入类型、输入根、输出根、模板快照、选图模式、页面选择、PSD 选项、款式和资产集合、manifest 引用、失败分类与时间戳；
- `BatchInputType`：`SingleStyle`、`StyleDirectoryBatch`；未识别时为空，不能猜测；
- `BatchScanStatus`：`Draft -> Scanning -> ScanReady|ScanBlocked`；`ScanReady` 只能在完整 manifest、SQLite 聚合事务和 committed marker 一致后出现；
- `StyleDraft`：稳定 `style_id`、默认款号、相对来源目录、排序和资产关联；
- `SourceAssetMetadata`：稳定 `asset_id`、`style_id`、根内相对来源、文件名、长度、SHA-256、像素宽高、格式、MIME、可读性和来源关联；
- `ScanBlockReason`：至少区分用户可整理的目录结构问题、源文件损坏/不可读、路径/权限/目录消失、取消和数据库/manifest 系统故障。

稳定 ID 由批次意图和规范化的相对来源事实确定，同一草稿重试和重启不得随机重建款式/资产关联。asset manifest 只保存相对来源引用和 hash，不保存客户绝对路径；数据库保存恢复所需输入根，但 UI/日志不得回显完整绝对路径。

### 4.2 目录识别合同

只检查根目录直接文件和一级子目录直接文件：

| 根目录支持图片 | 图片型一级子目录 | 结果 |
| --- | --- | --- |
| 有 | 无 | 单款批次，默认款号为所选目录名 |
| 无 | 一个或多个 | 正常批次，每个图片型一级子目录为一款 |
| 有 | 一个或多个 | `ScanBlocked`，混合结构，零款式、零资产、零 ready manifest |
| 无 | 无 | `ScanBlocked`，无可用图片，零款式、零资产、零 ready manifest |

支持扩展名为 JPG/JPEG、PNG、常规 TIFF；必须按实际文件头和结构读取格式、宽高与可读性，不能只相信扩展名。更深目录不参与识别和资产归属。根或批准一级范围内的 reparse point/符号链接、路径逃逸和设备路径拒绝读取，不跟随。

### 4.3 原文件与资源合同

- 所有源文件仅以 `FileAccess.Read` 和共享读取方式流式读取，哈希和元数据读取有界并发；
- 不把约 200 款高清图片一次性解码或载入内存，队列只持路径/元数据；
- 扫描、取消、失败、重试、删除草稿和重启恢复前后源文件 hash 不变；
- 缩略图或分析副本只能进入应用自有目录，INPUT-01 不要求为完成而生成缩略图；
- 清理/回滚只能删除应用自有 staging、manifest 和 marker，不能把输入根纳入可写路径策略。

### 4.4 持久化与提交合同

SQLite 只单调新增 v5，不修改 v1-v4 SQL 或 checksum。v5 至少保存批次、款式、资产、扫描失败和 manifest digest，并保持现有 TPL 表/快照可读。

扫描成功采用一个聚合提交边界：

```text
完整只读 inventory/metadata/hash
  -> 应用自有 asset-manifest staging 写入、flush、hash 读回
  -> 单个 SQLite writer 事务写 BatchDraft + styles + assets + BatchTemplateSnapshot + manifest digest
  -> 写 committed marker
  -> ScanReady
```

混合、空目录、不可读、损坏、权限、目录消失、取消、manifest 或 SQLite 提交失败不得留下部分 style/asset、孤儿模板快照、ready marker 或错误 `ScanReady`。文件已提交而数据库未知时按 marker/digest 恢复或回滚为 `ScanBlocked`，不只相信 UI 内存。`Scanning` 状态在重启发现时必须转为可重试的中文恢复状态；已成功 `ScanReady` 必须恢复同一模板快照、款式、资产和选项。

## 5. B-01 交付范围

真实 B-01 单页包含：

- 已提交模板组选择、批次内新建/导入模板入口的既有导航边界；
- 输入目录与原生 Windows 文件夹选择器；
- 单款/正常批次识别结果和款式数量；
- 正式输出目录、本次确认状态；
- AI 自动/人工选图批次默认值（只保存选择，不执行 AI）；
- 模板快照中的页面范围、至少一页门和 PSD 开关；
- `Draft/Scanning/ScanReady/ScanBlocked` 投影、取消、重试和草稿恢复；
- 混合、空目录、不可读、权限/目录消失和系统提交失败的中文可行动提示。

工作台“新建批次”从阶段 0 只读占位改为真实导航入口。扫描成功停留在 INPUT-01 范围的“扫描完成，可进入后续分析”状态；不得伪装 AI 分析或跳到 `ReadyToRender`。

## 6. 实施阶段与完成门

### INPUT-S0：任务书、依赖门与合同冻结

- 建立本任务书并同步项目总览、当前状态和验收矩阵；
- 核对基线、上游 TPL 生产物、无现成 INPUT 实现和工作区状态；
- 冻结本任务领域、目录、提交、恢复、B-01 与错误分类合同。

完成门：路由一致，无新增产品选择，实施计划可从仓库恢复。

### INPUT-S1：领域与应用扫描编排

- 实现品类中立模型、目录判定、稳定 ID、状态守卫；
- 新增只读 inventory/metadata、批次仓储和 manifest 端口；
- 实现有界并发扫描、取消传播、模板快照构建和聚合提交编排。

完成门：Domain/Application 聚焦测试覆盖四类目录、一级边界、取消、失败分类和稳定 ID。

### INPUT-S2：文件、manifest、SQLite 与恢复

- 实现用户输入根只读策略、reparse/逃逸门和流式 JPEG/PNG/TIFF 元数据/hash；
- 实现 `asset-manifest/v1` 原子 staging/marker；
- 新增 SQLite v5 和同事务批次/款式/资产/模板快照提交；
- 实现 DB/manifest 故障回滚与重启恢复。

完成门：Infrastructure 聚焦测试覆盖源 hash 不变、中文/英文/数字/空格/合法长路径、损坏/不可读、权限/目录消失、取消、迁移与故障注入；既有 TPL 数据不变。

### INPUT-S3：WPF B-01

- 接通真实入口、ViewModel、命令、状态投影、文件夹选择器和草稿恢复；
- 复用全局视觉资源，落地宽/窄关键状态、AutomationId、键盘焦点和中文文案；
- 不建立 INPUT 私有视觉系统，不把技术错误直接展示给普通用户。

完成门：WPF UI 聚焦测试覆盖代表状态、禁用门、1100x760、中文输入和 UIA；实际应用可进入并完成真实扫描。

### INPUT-S4：纵向验证与故障门

- 聚焦测试稳定后运行完整 solution；
- 覆盖约 200 款有界资源行为、重启恢复、源 hash、迁移、DB/manifest 故障和 reviewed diff；
- 打开实际应用，由产品负责人检查单款、正常批次、混合、空目录及窄窗/关键状态。

完成门：自动证据与人工页面走查均通过；同一阻断连续两次或资源异常时停止 workaround 链并报告。

### INPUT-S5：记录、审查与交接

- 创建 `docs/验证/INPUT-01批次输入纵向实现验证记录.md`；
- 更新本任务实施记录、验收矩阵、当前状态和项目总览；
- 执行 format/build/test、`git diff --check`、生成物/敏感文件/源文件安全审查；
- 提交清晰 commit 并报告 SHA，不自行恢复或合并 AI-01。

## 7. 测试与人工走查

自动化至少覆盖：

- 单款、正常批次、混合零部分任务、空目录；
- JPG/JPEG、PNG、常规 TIFF；损坏/不可读、扩展名与实际格式不符；
- 中文、英文、数字、空格和合法长路径；
- 权限失败、目录消失、reparse point/符号链接、路径逃逸；
- 扫描中取消、重试、删除草稿、应用重启恢复；
- manifest staging/marker、SQLite 事务和两侧故障；
- v1-v4 到 v5 单调迁移，现有模板修订和旧批次快照不变；
- 约 200 款只持文件引用和元数据、有界并发、不一次载入高清图；
- 所有成功、失败、取消和恢复路径的源 hash 不变；
- B-01 默认、扫描中、单款、正常、混合、空目录、系统阻断、恢复及窄窗/键盘/UIA。

人工走查明确检查：

1. 单款目录显示“已识别为单款批次 · 1 款”；
2. 一级款式目录显示“已识别为正常批次 · N 款”；
3. 混合目录显示 PRD 原文含义、主操作禁用且零部分任务；
4. 空目录显示可行动中文提示；
5. 扫描状态、取消/重试、重启恢复无虚假完成；
6. 1440×900 与 1100×760 关键内容、焦点、状态灯和错误文案可见。

## 8. 完成证据与停止规则

完成证据必须同时包含真实代码、聚焦与完整测试、迁移/故障/恢复证据、源文件 hash、实际 WPF 页面走查、reviewed diff 和验证记录。脚手架、mock、任务书、测试数量或截图单独都不能关闭 INPUT-01。

以下情况停止继续绕过并报告：同一阻断连续出现两次；内存/CPU/磁盘资源异常；批准的一级目录/聚合提交方案无法满足结构证据；需要新的产品行为、范围或数据边界选择；需要联网安装依赖、GUI 权限或外部能力而未获批准。

## 9. 实施记录

### 2026-08-28：INPUT-S0 启动

- 硬门通过：`codex/desktop-foundation-01` 最新指针和本任务起点均为 `3c5d70d`；工作区无用户改动；
- 上游通过：验收矩阵 `TPL-01` 为通过，生产代码中存在不可变模板修订、批次模板快照、SQLite/WAL 单 writer、manifest/marker 和 `DesignSystem.xaml`；
- 缺口确认：正式 INPUT 领域、扫描端口、asset manifest、v5 表和 B-01 页面均不存在；阶段 0 通用 `Scanning` 与 `AppManifest` 不得冒充；
- 产品批准：本任务严格映射现有基线，记录为已批准并持续执行；当前无待产品确认项；
- 分支：已从 `3c5d70d` 建立 `codex/input-01-batch-input`。

### 2026-08-28：INPUT-S1 至 INPUT-S4 自动化实现

- S1：品类中立 `BatchDraft`、严格四类目录判定、稳定 ID、扫描端口、有界并发、取消和失败分类已实现；
- S2：流式 JPEG/PNG/TIFF 校验与 hash、路径/reparse 门、`asset-manifest/v1`、SQLite v5、聚合提交、故障补偿和重启恢复已实现；草稿自动保存复用同一模板快照，扫描开始后冻结；
- S3：真实 WPF B-01、原生文件夹选择器、页面/目录/选项、中文状态、取消/重试/恢复、宽窄布局和 UIA 已接通；Presentation 通过 Application facade 使用批次状态，不直接引用 Domain；
- S4 自动化：format 通过；隔离 `OutDir` 完整构建 0 警告/0 错误，solution 测试 148/148；真实 200 款、合法长路径、源 hash、损坏/不可读、路径异常、SQLite/manifest 故障、旧 TPL 数据和重启恢复均有证据；
- 实际应用已用隔离临时数据启动并显示主窗口；产品负责人完成单款、正常、混合、空目录、恢复和宽窄关键状态走查，并明确确认“走查了一遍没什么问题”；
- 详细证据见 `docs/验证/INPUT-01批次输入纵向实现验证记录.md`。

### 2026-08-29：INPUT-S4 至 INPUT-S5 验收收口

- 经产品负责人授权，Codex 再次打开隔离实际应用检查，确认 B-01 入口、中文页面和隔离数据根正常；
- 复核发现共享自绘模板下拉框回显 `TemplateLibraryCardViewModel` 对象字符串。该显示违反普通界面不暴露内部字段的既有合同，已改为显式 `Name` 数据模板，并增加实际 WPF 渲染树断言；聚焦 UI 测试 5/5 通过；
- 主任务复核 `e11cd96` 时补正 ready 状态合同：恢复适配器保留 `ScanReady`，应用重启恢复同一 ready 批次；ready 页面显示“扫描完成”并禁用扫描、模板、目录、页面、选图和 PSD 配置，WPF 回归覆盖完成态与 ready 恢复；补正后聚焦测试 5/5、完整 solution 测试 148/148；
- Domain 继续作为 `BatchDraft`、扫描状态和业务不变量的唯一来源；Application facade 暴露 Presentation DTO/枚举，WPF 不直接引用 Domain，架构测试保持通过；
- 产品走查、自动化、故障/恢复、源文件安全和纵向生产者合同均已满足；任务状态转为已完成，等待最终 reviewed diff、提交 SHA 和主任务复核合并；
- 本分支不自行合并 `codex/desktop-foundation-01`，不恢复 `AI-01`。
