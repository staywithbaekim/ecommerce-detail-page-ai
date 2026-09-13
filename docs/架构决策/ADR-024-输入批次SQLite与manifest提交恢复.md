# ADR-024：输入批次 SQLite 与 manifest 提交恢复

## 状态

已批准并实现。2026-09-09。该 ADR 是对 `INPUT-01`/`AI-01` 输入边界的本地持久化修正，不重新打开已完成的 AI-01 S0-S7，也不改变 `asset-manifest/v1`、AI provider 合同或用户源文件保护规则。

## 背景

真实目标款复测出现过“manifest 已存在但 SQLite 中没有对应批次、素材和分析记录”的本地提交链路不一致。旧实现把 SQLite 批次提交、manifest 提交和 UI 取消视为一个可取消的连续操作；进程退出、写入异常或 WAL/文件提交时序变化可能留下半提交状态。后续 AI 读取到孤立 manifest 时，必须停止并报告本地状态不一致，不能把它误报成图片不匹配，也不能在缺少完整 `BatchDraft` 时调用 provider。

## 决策

### 1. 输入批次采用应用自有 journal 闭合提交

`SqliteBatchScanCommitter` 按以下顺序执行：

1. 将经过 Schema、digest 和长度校验的 `asset-manifest/v1` 写入暂存文件；
2. 写入 `input-commit-journal/v1`，包含可重建的批次元数据、manifest 引用/digest 和暂存 token；不保存源照片、PSD 或其字节；
3. 在不可取消的 SQLite writer/事务屏障中提交完整 `ScanReady` 批次图；
4. 更新 journal 为 `DatabaseCommitted`，提交 manifest 和 committed marker；
5. 更新 journal 为 `Committed`，再做 SQLite 与 manifest 三方核验。

journal 状态为 `Staged`、`DatabaseCommitted`、`Committed` 或 `RecoveryRequired`。所有状态更新采用应用原子文件写入；SQLite 使用单 writer、WAL、`synchronous=FULL`，正常退出执行 WAL checkpoint。

### 2. 重启恢复以最强证据为准

- journal 与 committed manifest 都存在、SQLite 行缺失：按 journal 重建完整批次图，再核验 digest 和字段一致性；
- SQLite 已有 ready 行、journal 仍未完成、manifest 仍为 staged：保留暂存证据，重启时完成 manifest 提交并收口 journal；
- SQLite 已有 ready 行但 manifest/journal 无法闭合：返回 `INPUT_MANIFEST_DATABASE_MISMATCH`，阻止 AI；
- 只有孤立旧 manifest、没有 SQLite 行和 journal：报告本地状态不一致，不显示为图片匹配失败，也不自动猜测或导入；
- `RecoveryRequired` journal 的暂存文件已安全回滚且没有可重建证据：不伪造 ScanReady，保留恢复状态，要求用户建立新批次。

### 3. 提交附近取消和异常的安全边界

写入 journal 后，SQLite/manifest 提交阶段不接受 UI cancellation token。若异常发生在 durable barrier 之后，暂存 manifest 不得被清理掉；结果进入 `RecoveryRequired`，由重启恢复。若取消发生在 barrier 之前且没有 durable 证据，才返回正常的“扫描已取消”状态。这样不会用一个取消操作覆盖可能已经提交的 ready 图，也不会删除恢复所需的暂存证据。

### 4. AI 前置门

AI workflow 在授权、预算和 provider 之前执行 checkpoint/manifest 状态预检。批次清单、SQLite 图、AI manifest 或 journal 不一致时，provider 调用数必须为 0，并向用户显示“本地状态未保存或不一致；请重新扫描并建立新批次”。源照片、PSD、模板规则和正式输出均保持只读。

## 不在本 ADR 中

- 不迁移、修补或复用已经损坏的旧批次；旧孤立 manifest 只作隔离证据；
- 不删除用户源照片、PSD、重复照片或正式输出；
- 不提高单请求 24 张、ready buffer 32、HTTP 并发 8、预算、重试或未知结果安全门；
- 不改变 AI 语义选图、TPL-LEARN-01 动态规则或 Photoshop/UXP RenderPlan 合同。

## 验证

验证记录见 `docs/验证/AI-01本地状态一致性修复与验证-20260909.md`。必须覆盖 journal 重建、manifest/SQLite mismatch、SQLite/manifest 提交失败、取消附近的恢复、AI checkpoint 不一致时 provider=0、SQLite integrity check 和隔离测试包启动/安全退出。完整 `PERF-01` 的 10 款 `machine_total` 仍未因本 ADR 自动通过。

## 2026-09-09 实现补充：集合比较与模板包兼容

真实复核发现两个与本 ADR 直接相关的边界：

1. journal 与 SQLite 对同款素材的 `AssetIds` 可能采用不同的稳定排序。该字段表示素材集合，不表示用户可见顺序，因此三方核验按 Ordinal 排序后比较，避免 Windows 大小写排序差异把已提交批次误判为 `RecoveryRequired`。
2. 模板包的 PSD 检查模型会逐步增加可选证据字段。运行时 `template-package-v1` Schema 必须声明这些字段为可选，并保持旧包缺失字段仍可导入；这不改变来源 PSD、路径、哈希、层结构和 manifest 提交安全门。

新增的集合排序回归、运行时 Schema 回归和全新数据根真实 24 张素材批次复核见 `docs/验证/AI-01本地状态一致性修复与验证-20260909.md`。该补充仍不改变 provider 前零调用门、24 张上限、ready buffer 32、HTTP 并发 8 或 `PERF-01` 的正式性能要求。
