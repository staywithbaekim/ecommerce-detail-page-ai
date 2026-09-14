# TPL-LEARN-01 生产目标匹配链路验证记录

## 范围与结论

2026-09-08 对 `6d98149` 工作树的 reviewed diff 发现：首版 `ReferenceLearningAiService.MatchTargetGroupAsync` 及 v2 目标匹配类型只有测试调用，没有进入日常新批次。该实现不能证明 PRD 7.3(6)-(9) 和 LEARN-S5 的生产消费，并会在未来形成第二套目标排序、缓存、usage 和计费事实。

本轮已收口为唯一生产链：

```text
BatchDraft / asset-manifest
  → AiAnalysisInputProjector
  → AiAnalysisScheduler（request fingerprint / cache / usage / checkpoint）
  → AiBatchAnalysisWorkflow（逐框候选与绿黄红）
  → 人工锁定（selection revision / CAS）
  → AiRenderPlanFactory
  → renderer-neutral RenderPlan
```

`reference-learning-ai/v2` 只在学习阶段用整组参考照片生成开放式 `selection_requirement`，未知操作 `match_target_group` 在 HTTP 前拒绝。日常每个目标款仍只有一笔 `product_joint_understanding` 首层请求，不叠加第二次参考学习 v2 调用。

## 实现证据

- 学习提交把语义来源、请求/结果/模型/提示词版本、`reference-audit/v1`、参考组 digest 和完整参考内容 SHA-256 集合写入 `FrameBinding.SelectionRequirement.Criteria`。SQLite 提交/重启和 `template-package/v1` 导出/导入测试明确核对了这些字段。学习会话 JSON 也增加稳定的 `semantic_metadata` 快照并通过 schema 与重启往返，避免重启后丢失模型、提示词和证据来源；无需改 SQLite 表或破坏旧修订。
- `AiAnalysisInputProjector` 在 scheduler、授权准入、预算与 provider 之前验证学习绑定的审计闭包；任一目标素材 hash 命中参考集合即以 `AI_INPUT_REFERENCE_ASSET_FORBIDDEN` 失败关闭。审计字段参与 `requirements_digest`，但不进入供应商看到的选图文字。
- 日常提示词升为 `ai-prompt/v2`，要求整款素材联合排名、允许同图多框，并规定每项 `evidence_codes` 必须且只能含 `exact`、`safe_similar`、`material_difference` 或 `unsafe` 之一。旧 `ai-prompt/v1` fingerprint/cache 不会冒充新结果。
- 旧版验证记录曾把 exact/safe_similar 的候选分差写成绿色必要条件；该门槛已由 2026-09-13 批准的 ADR-030 supersede。当前规则为：exact `≥0.85` 直接为绿，safe_similar `≥0.90` 且差异不影响用途时为绿；候选分差或同分不单独降级。exact/safe_similar/material_difference `≥0.60` 在未达到绿色门槛或存在实质差异时为黄；unsafe、低于 `0.60` 或无候选为红。缺主体几何为黄；缺分类/重复分类、结果损坏、AI 未知或失败为系统恢复态。
- 人工锁定覆盖 AI 默认选择，刷新不覆盖锁定且不触发新的 provider 请求。同一资产可进入多个图片框；每框继续使用各自 `LayoutIntent`，所以相同主体几何可产生不同 scale/offset/safe bounds。
- RenderPlan 的 source 只来自当前 `BatchDraft` 的目标款资产。测试明确断言参考 hash、`reference_only` 与参考路径不出现于 RenderPlan。

## 已运行验证

本轮聚焦命令及结果：

- `dotnet build EcommerceDetailPage.Desktop.slnx --no-restore`：0 警告，0 错误。
- `dotnet test tests/Desktop.Contracts.Tests/Desktop.Contracts.Tests.csproj --no-restore --filter "FullyQualifiedName~ReferenceLearningAiV2ContractTests"`：4/4 通过。
- `dotnet test tests/Desktop.Application.Tests/Desktop.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~ReferenceLearningAiServiceTests|FullyQualifiedName~AiAnalysisInputProjectorTests|FullyQualifiedName~ReferenceLearningCommitServiceTests"`：20/20 通过。
- `dotnet test tests/Desktop.Infrastructure.Tests/Desktop.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~AiBatchWorkflowRenderPlanIntegrationTests"`：22/22 通过。
- `dotnet test tests/Desktop.App.UiTests/Desktop.App.UiTests.csproj --no-restore --filter "FullyQualifiedName~WpfShellTests"`：25/25 通过。

最终复核：

- `dotnet build EcommerceDetailPage.Desktop.slnx --no-restore`：0 警告，0 错误。
- `dotnet test EcommerceDetailPage.Desktop.slnx --no-build`：549 通过、3 个环境探针跳过；仅 2 个既知 SIZE-01 外部金标缺失失败（`size-real-a-577fa4d19f919a7a.jpg`、`size-real-b-9231ad299120bba6.jpg`）。
- 参考学习 AI、生产目标匹配、SQLite/学习会话/模板包往返聚焦：79 通过、2 个真实环境探针跳过；WPF：25/25 通过。
- `dotnet format EcommerceDetailPage.Desktop.slnx --verify-no-changes --no-restore`、`git diff --check`、变更文件媒体/生成物检查和变更行凭据扫描通过；`git diff --check` 仅输出既有 LF/CRLF 提示。
- 新测试目录：`tmp/tpl-learn-s3-local-app-20260908-target-match-v2`，1,217 个文件、173,113,197 bytes；`Desktop.App.dll` SHA-256 为 `95EB449365BB2D55B19C0660CF58B79F217B8A07C543F837D12F9C8CE654133A`，`Desktop.App.exe` SHA-256 为 `AFA55F64DB049B67D80747F8E6BD073DE45EA7C233AD12E8C18E3AE49277BDB4`，复用的已验证 worker SHA-256 为 `7C0E7E6B8AF86EBE00474452616EB475E5B45070FBE0E9ABFD55945FB35686A3`。
- 使用 `tmp/tpl-learn-s3-smoke-data-20260908-target-match-v2` 和正确的 `--data-root=<路径>` 启动：进程保持响应，正常退出码 `0`，SQLite `integrity_check=ok`、migration v12、最新 `safe_exit=1`，无启动诊断日志和残留进程。
- 将既有数据副本再复制到 `tmp/tpl-learn-s3-existing-data-target-match-v2-smoke` 后用同一新包启动：进程保持响应并正常退出码 `0`，SQLite `integrity_check=ok`、migration v12、最新 `safe_exit=1`；副本中原有的旧版 `IAiUploadAdmissionPort` 启动失败诊断文件 hash 未变化，证明本次没有追加同类错误。

测试未联网、未调用 Photoshop、未修改源 PSD、参考照片或目标照片。一次烟测最初误用应用不支持的分离参数 `--data-root <路径>`，应用因此按默认数据根启动并正常安全退出；该次只新增应用实例/安全退出记录，未改模板、批次或素材。发现后立即停止，后续烟测改用正确单参数并只写入上述隔离数据根。全过程未执行 reset、clean 或 checkout。

## 未关闭项与实际操作路径

自动化证明的是生产链闭包，不是不同目标款的真实准确率。仓库当前仍缺至少一个与参考款不同的已批准目标款照片文件夹，因此 `TPL-LEARN-01` 保持进行中，LEARN-S4 至 S7 不关闭。

新测试包发布后，产品负责人按以下实际路径验收：新建批次 → 选择已提交的学习模板 → 选择不同目标款文件夹 → 选择输出文件夹 → 点击“扫描并分析” → 在 B-03 查看逐页/逐框候选和状态 → 对黄色项选择并锁定候选 → 点击开始生成。进入正式生成前的选图验收不需要 Photoshop；正式 JPG/PSD 仍属于 RENDER-01。
