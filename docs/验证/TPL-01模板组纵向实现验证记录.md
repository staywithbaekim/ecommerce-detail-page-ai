# TPL-01 模板组纵向实现验证记录

## 当前结论

截至 2026-08-27，`TPL-S0`、`TPL-S1`、`TPL-S2` 已通过，`TPL-01` 仍在实施中，下一阶段为 `TPL-S3`。本文会随 S3-S7 持续追加；当前不得把 PSD 技术门、领域/持久化合同、源 PSD 导入、Figma 上下文、历史 Photoshop 探针或测试数量写成模板纵向功能完成。

## 起点与范围

- 起点：`217ec0b docs: approve TPL-01 implementation brief`；阶段 0 工程骨架未重做。
- 开始前工作区干净；已按任务书顺序读取 PRD 1.5、当前状态、任务书、验收矩阵、ADR-010/011/012/014、交互规格和 T-01 至 T-05 高保真交付记录。
- 已按 `figma-design-to-code` 技能读取冻结 TPL 代表节点；S3/S4 实际 WPF 页面仍须以冻结设计实现并进行真实窗口人工走查。
- 用户源 PSD、源照片、人工结果和尺码图只读；实验只使用工作区 `tmp` 中的明确副本。

## TPL-S0：已通过

- 候选 A：`PhotoshopFile 1.0.0` 不存在；`Aspose.PSD 26.8.0` 虽能读目标 ID，但层树计数与批准证据不一致，评估预览有水印，且商业许可/资源代价不满足门槛，因此拒绝。
- 候选 B：采用 `psd-tools 1.18.0` 隔离 worker。8 个批准 PSD 副本全部成功；G03/G05 层树、路径、ID、剪贴关系、蒙版状态和结构签名与批准证据一致。
- 候选 C：保留 Photoshop 2025 / UXP v5 作为显式兼容性探针，不作为默认校准实现。
- 打包 worker 与 .NET client 真实端到端通过；路径、缺失文件、畸形响应、超时和响应超限均稳定返回错误码。
- 四框默认 cover 与 Photoshop 人工参考差异低且几何一致；缩放/偏移探针无白边，换算误差不超过 `0.5 px`。详细证据见 `docs/验证/TPL-S0-PSD只读检查技术门记录.md` 和 `ADR-017`。

## TPL-S1：已通过

- 新增品类中立的 `TemplateGroup`、不可变 `TemplateRevision`、稳定 `TemplatePage`、显式 `FrameBinding`、`CalibrationDraft`、`CalibrationSample`、`TemplatePreviewState` 和 `BatchTemplateSnapshot`。Domain 未引用 WPF、SQLite、PSD 库、HTTP、Photoshop 或外层项目。
- 页面守卫拒绝非组合模式的 `variant_capacity`，组合模式要求正整数；PSD 层路径固定 `psd_bottom_to_top`，注册框必须携带稳定 layer ID、可见性、剪贴、蒙版、透明度、混合模式和结构签名。
- 修订状态机按任务书主流程和失败终点推进；提交需要相对 manifest 引用、规则 digest 和一致提交证据，`Committed` 后拒绝任何转换或草稿覆盖。增量修订继承原 `page_id`、资源 ID 和绑定，只给新增页分配新 ID。
- 批次快照创建时冻结修订、已选页面顺序、生成规则、模板资源和 frame ID；分析开始后切换返回 `TPL_BATCH_SNAPSHOT_FROZEN`。SQLite repository 对相同 batch 的冲突快照也稳定拒绝。
- 冻结 `template-revision/v1` JSON Schema 和正例；未知主版本、绝对/逃逸路径、缺少结构签名、非组合容量和 Photoshop 私有字段负例均拒绝。
- SQLite 从既有 checksum `desktop-v1-schema-001` 单调迁移到 v2；启动验证所有已应用 checksum，迁移在同一事务中执行。预置不兼容 v2 结构的故障注入证明失败后版本仍为 1、旧数据可读、v2 表未半创建。
- `ITemplateRepository`、`IBatchSnapshotRepository` 都映射到已有 `SqliteDesktopStore` singleton，继续使用唯一 `SqliteWriteQueue`；模板修订、绑定、草稿和快照重启恢复通过。

## TPL-S2：已通过

- 新增只读源 PSD fingerprint、应用自有 staging 副本和复制前/中/后长度与 SHA-256 核对。目标路径只能由 `IPathPolicy` 解析为应用自有相对路径；源绝对路径只存在于当次 Application 请求，不进入 SQLite、manifest、日志或 RenderPlan。
- 流程固定为“只读 fingerprint → 复制到 staging → 内容 hash 判重 → 明确选择”。重复时不删除用户源 PSD，也不自动写成第二个资源；保留现有副本或取消时只删除本次应用自有 staging，继续则经过 S0 inspection 后保存独立资源。
- `command_id` 提供写入幂等；同命令不同资源返回 `TPL_IMPORT_COMMAND_CONFLICT`。SQLite v3 新增 `template_resources` 与 `template_import_commands`，继续复用唯一 `SqliteDesktopStore`/`SqliteWriteQueue`，重启后可按命令和 hash 恢复。
- inspection 失败、inspection hash/长度不一致、重复查询失败、SQLite 外键/提交失败和无效重复选择均清理内部副本且不留下数据库半对象。临时 `.part` 清理失败不会覆盖原错误，而返回 `TPL_IMPORT_CLEANUP_REQUIRED`。
- 真实文件端口测试比较源 SHA-256、内部副本 SHA-256 和清理结果；应用/文件/SQLite 串联测试证明成功重启恢复、inspection 失败零资源、重复源文件均保留、数据库事务失败回滚。测试使用临时 4-byte `.psd` 字节文件和 fake inspection，不冒充真实 PSD 结构验证；真实结构读取仍由已通过 S0 的隔离 worker 证据承担。

实际命令：

```powershell
dotnet test tests/Desktop.Domain.Tests/Desktop.Domain.Tests.csproj -c Debug --no-restore
dotnet test tests/Desktop.Contracts.Tests/Desktop.Contracts.Tests.csproj -c Debug --no-restore
dotnet test tests/Desktop.Infrastructure.Tests/Desktop.Infrastructure.Tests.csproj -c Debug --no-restore
dotnet test tests/Desktop.Architecture.Tests/Desktop.Architecture.Tests.csproj -c Debug --no-restore
dotnet format EcommerceDetailPage.Desktop.slnx --no-restore --verify-no-changes --verbosity minimal
dotnet build EcommerceDetailPage.Desktop.slnx -c Debug --no-restore --no-incremental
dotnet test EcommerceDetailPage.Desktop.slnx -c Debug --no-build --no-restore
```

## 源文件与仓库安全

- 8 个源 PSD、4 张源照片和 1 张人工参考在复制前后 SHA-256 均不变；解析、预览、打包和损坏注入仅发生在 `tmp/tpl-s0-validation`。
- Git 不提交 PSD/JPG、人工参考、预览、数据库、缓存、日志、凭据、虚拟环境、PyInstaller 制品或其他生成物。
- worker 只接受应用自有路径；PSD 读取前后再次核对 hash。所有图片框仍需用户显式注册或完整结构证据，未把全部智能对象自动注册。

## 当前验证结果

| 检查 | 结果 |
| --- | --- |
| NuGet restore + 漏洞索引 | 人工批准联网后 11 个项目恢复；无 `NU1301`、`NU1900` 或漏洞告警 |
| Python 语法 | worker 与本地预览验证工具通过 `compileall` |
| 打包 worker | 102 文件；58,631,966 bytes；EXE SHA-256 `112D4119...C99A` |
| 真实 PSD | 8/8 成功，源/副本 hash 不变；损坏/协议/JSON 负例稳定阻断 |
| .NET client 聚焦测试 | 6/6 通过 |
| TPL-S0 solution build | 0 警告、0 错误 |
| TPL-S1 聚焦测试 | Domain 26/26；Contracts 15/15；Infrastructure 21/21；Architecture 5/5 |
| TPL-S2 聚焦测试 | Application 12/12；Infrastructure 31/31；源 hash、重复确认、重启恢复、inspection/SQLite 失败清理均通过 |
| 当前 solution build | 0 警告、0 错误 |
| solution test | 92/92 通过：26 + 12 + 15 + 31 + 5 + 3 |

## 未完成项

- TPL-S3 至 TPL-S7 的 T-01 至 T-04 WPF、校准、文件/数据库原子修订、五加一、模板包和最终交接。
- 五页加第六页、旧批次快照隔离、重启恢复、全部提交点故障回滚和真实应用人工走查。
- T-05 Photoshop 准确预览和真实 AI 学习不属于本任务实现范围，且不得用 mock 冒充通过。

## 下一动作

进入 TPL-S3，重新按 `figma-design-to-code` 技能读取冻结 T-01/T-02/T-03 设计上下文，以真实 repository 投影实现模板库、模板详情和新建/增量/重复确认 WPF 纵向链路；完成代表页面后打开实际应用供产品负责人逐项视觉走查。
