# SIZE-01 SIZE-S1 至 S6 连续实现验证记录

## 1. 结论

2026-09-01 在基线 `a60b54015cd195be2fc48c187e965bb483f04415` 上完成 SIZE-S1 至 S6 的代码候选和自动化验证。实现已把正式 `BatchDraft`/manifest 投影与 AI-01 的 `AiSizeTableCandidateV1` 接到品类中立领域模型、hash 去重/冲突门、原文校验、原子状态文件、固定 96 DPI WPF PNG、`deterministic_size_table` 资源、动态页 `render-plan/v1` 和 O-03 结构化编辑器。

完整 solution 为 **453 通过、1 个显式真实外部探测跳过、0 失败**；build 为 0 警告/0 错误，format、架构依赖和 diff 检查通过。来源真实图片的收口 hash 与首次盘点一致，仍位于 Git 忽略目录，未修改、未提交。

SIZE-01 **尚未整体通过**。本记录不能替代两张真实金标的正式逐格运行、O-03 在 1100×760/100%/125% DPI 下的实际产品走查、真实模板视觉确认以及任务矩阵要求的 JPG/PSD 集成证据；这些留在 SIZE-S7/后续 RENDER-01 联合验收门。

## 2. 实现范围

### 2.1 SIZE-S1：领域与恢复边界

- `SizeTable`、来源、候选、列、行、单元格、issue、`Extracting/Valid/ReviewRequired/Invalid`、revision/CAS 和稳定数据摘要；
- 最大 32 列、200 行、单元格 256 字符；行列关系、ID、重复表头/尺码、空值和控制字符有界；
- `size-table/v1` 原子状态文件、文档摘要、读取损坏映射 `RecoveryRequired`，损坏记录不会以异常/路径泄漏到 WPF；
- Presentation 只消费 Application 编辑 DTO/工作流，不直接引用 Domain；架构测试已关闭首次回归发现的边界错误。

### 2.2 SIZE-S2/S3：来源与结构

- 从正式 manifest/BacthDraft 生产者资产关系筛选 AI 候选来源，不重扫源目录；
- 同款多个逻辑引用的相同内容按 SHA-256 合并；不同 hash 保持 `SIZE_TABLE_SOURCE_CONFLICT` 并要求显式选择；不同款式独立处理，不构成冲突；
- AI 候选规范化时保留所有单元格原文。`/`、`\`、`-` 均为合法非空值；长度为零是真缺值并形成 blocking issue；
- 低置信、重复表头/尺码、ragged 结构、来源变更、缺失候选和损坏状态均 fail closed，不生成尺码资源或计划。

### 2.3 SIZE-S4：确定性资源

- Infrastructure 使用独立 STA 线程上的 WPF `DrawingVisual`/`FormattedText`/`RenderTargetBitmap`/`PngBitmapEncoder`；
- DPI 固定 96、`pixelsPerDip=1`，字体 `Microsoft YaHei UI`，样式版本绑定模板规则摘要，布局版本 `dynamic_grid_v1`；
- 自然列宽测量后只分配剩余宽度，行高随字形和最小行高决定，总高度随行数变化；超宽/超高 fail closed；
- 资源 ID 包含结构摘要、全部样式参数、栅格器版本、字体版本签名和 DPI；PNG 以内容寻址相对路径原子写入，源图和旧资源不覆盖；
- 架构选择见 `docs/架构决策/ADR-020-SIZE-01确定性尺码表栅格器.md`。

### 2.4 SIZE-S5：O-03 编辑候选

- 结果页仅对动态尺码页显示“编辑尺码数据”；正常清晰页不强制进入编辑器，用户可主动纠错；
- 弹层包含只读来源引用/预览、结构化可编辑表格和模板样式结构预览；支持改单元格、增删行列；
- 真空值、重复表头/尺码和非法结构禁用保存；没有 AI 猜值或生成式图片入口；
- 保存通过 Application DTO 工作流执行 revision CAS，失败保留原数据/旧计划/旧输出；成功后重新准备当前款当前尺码页。

该代码已通过 XAML build 和编辑状态自动化测试，但尚未做本任务要求的真实窗口、中文 IME、键盘焦点、UIA、窄窗口及 125% DPI 产品走查，故 S5 视觉质量门仍开放。

### 2.5 SIZE-S6：RenderPlan 和局部隔离

- 动态页计划登记模板副本和一个 `deterministic_size_table` PNG；`targets=[]`，`size_table` 保存结构化列/行、grid/数据摘要、样式/布局版本和资源引用；
- 计划不包含 PNG 内部相对路径、市场来源路径、token、WPF 类型或 Photoshop 命令；普通页仍保持 `size_table=null`；
- 集成测试先生成普通页+尺码页计划，再把一个袖长从 `/` 编辑为 `\`：普通页 `plan_digest` 不变，尺码页生成新的数据摘要、资源和计划；
- Photoshop 不可用时批次停在 `WaitingForPhotoshop`，没有正式输出。

## 3. 金标与环境

| 项目 | 结果 |
| --- | --- |
| OS | Microsoft Windows NT `10.0.26200.0` |
| .NET SDK | `10.0.400`；测试运行时输出为 .NET 10 |
| 栅格器 | `wpf-size-table-rasterizer/v1`，96 DPI |
| 字体文件证据 | `msyh.ttc` 19,704,352 字节，SHA-256 `d79c55e68b1131eea0cc1c47be4f572d964f28c682e143db2ad09c1e4cb07a3f`；`msyhbd.ttc` 16,883,552 字节，SHA-256 `4508821b3dffe01f0ef5e5326a3e60df705a44633858811f67b6982dce3f6ee6` |
| `size-real-a` 收口 | 98,392 字节，SHA-256 `577fa4d19f919a7a7bbd64f07daa1102a34642c470e0967364f44543271a9e2b` |
| `size-real-b` 收口 | 239,126 字节，SHA-256 `9231ad299120bba65af9391ba8a59097b7ff169197810a38971621df886d9fb0` |

两张真实金标均命中 `.gitignore` 的 `需求样本/`，`git ls-files` 无输出。当前自动化使用同产品规则的确定性结构化输入验证符号、变形表格、异常、资源和计划；没有发起真实 AI/Photoshop 调用，也没有把派生输入冒充真实媒体。

## 4. 验证命令与结果

```text
dotnet build EcommerceDetailPage.Desktop.slnx --no-restore -p:NuGetAudit=false --verbosity minimal
结果：全部项目成功，0 警告、0 错误。

dotnet test tests/Desktop.Domain.Tests/Desktop.Domain.Tests.csproj --no-restore --filter "FullyQualifiedName~SizeTableDomainTests" --verbosity minimal
结果：9/9 通过。

dotnet test tests/Desktop.Application.Tests/Desktop.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~SizeTableServiceTests" --verbosity minimal
结果：6/6 通过。

dotnet test tests/Desktop.Infrastructure.Tests/Desktop.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~SizeTableFileRepositoryTests" --verbosity minimal
结果：2/2 通过。

dotnet test tests/Desktop.Infrastructure.Tests/Desktop.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~WpfSizeTableRasterizerTests|FullyQualifiedName~TrustedSizeCandidatePersistsRasterizesAndBuildsDynamicRenderPlan" --verbosity minimal
结果：6/6 通过。

dotnet test tests/Desktop.App.UiTests/Desktop.App.UiTests.csproj --no-restore --filter "FullyQualifiedName~SizeTableEditorPreservesLegalSymbolsAndBlocksTrueEmptyCells" --verbosity minimal
结果：1/1 通过。

dotnet test tests/Desktop.Architecture.Tests/Desktop.Architecture.Tests.csproj --no-restore --verbosity minimal
结果：5/5 通过；Presentation 不再直接引用 Domain。

dotnet format EcommerceDetailPage.Desktop.slnx --no-restore --verify-no-changes --verbosity minimal
结果：通过。

dotnet test EcommerceDetailPage.Desktop.slnx --no-restore -p:NuGetAudit=false --verbosity minimal
结果：453 passed / 1 skipped / 0 failed。跳过项为显式 opt-in 的 RealProbeThroughFormalAdapter，本轮未联网、未产生外部请求。

git diff --check
结果：通过。
```

## 5. 差异与安全审查

- 变更限于 SIZE-01 的 Domain/Application/Infrastructure/WPF、对应测试、任务/ADR/状态/验收文档；没有修改 RenderPlan Schema、UXP 插件、源 PSD、源图片、模板包或历史输出；
- 新增媒体、PSD、数据库、压缩包、日志、dump 等可提交产物为 0；
- 变更文件中的原始聊天缓存绝对路径和常见凭据模式扫描为 0；
- 两张真实图片只存在于忽略目录，收口长度/hash 与 S0 一致；
- 代码差异已通过 architecture、format、build、完整测试和 `git diff --check`，但在 SIZE-S7 实际 UI/真实金标/JPG-PSD 联合验收前不宣称任务完成。

## 6. 下一动作

1. 用 `size-real-a`、`size-real-b` 和批准派生场景执行正式逐格结构回归，记录候选/结构/PNG/RenderPlan 摘要；
2. 在实际 WPF 窗口完成 1440×900、1100×760、100%/125% DPI、中文输入、键盘和 UIA 走查，必要时修正布局；
3. 与 RENDER-01 的 UXP v5 adapter 联合验证真实模板中的智能对象嵌入、JPG/PSD 原子输出和失败无覆盖；
4. 完成 reviewed diff 和产品走查后再更新 SIZE-01 为通过。
