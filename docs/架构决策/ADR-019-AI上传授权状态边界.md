# ADR-019：AI 上传授权状态边界

## 状态

已批准并实施（2026-08-31）

## 背景

PRD 1.5 第 11.2 节要求首次启用 AI 前进行一次明确上传授权，授权保存在本地，用户可在设置中查看和撤回；撤回后不得发起新的 AI 分析请求，已有本地结果不受影响。V1 不引入登录、账号、云授权或在线计费系统。请求中的 `consent-{batchId}` 只能表示请求关联，不能证明用户授权事实。

## 决策

1. 授权生产者采用应用内一次性显式操作：F-01 首次使用配置或首次点击“扫描并分析”时显示默认未勾选的“AI 分析与图片上传授权”说明和同意操作。说明明确只上传应用生成的必要压缩分析副本到火山方舟，不上传 PSD、原始高清图片、本地路径或正式输出；日常批次不重复弹窗。
2. 在 Application 层定义供应商无关 `IConsentPort`。V1 Infrastructure 实现为 `SqliteDesktopStore`，使用 SQLite v12 的 `ai_upload_consents` 当前状态表和 `ai_upload_consent_events` 追加事件表，在同一事务中保存作用域、consent ID、`Granted/Revoked`、策略版本 `ai-upload-consent/v1` 和时间。
3. `AiBatchAnalysisWorkflow` 在新批次入队前检查授权；scheduler 在预算预留、转为 `Uploading` 和调用 provider 前再次读取当前状态。状态缺失或 `Revoked` 时 fail closed：返回可行动错误、保持请求 `Queued`，不预留预算、不调用 provider、不把请求记为 `AiResultUnknown`。
4. S-01 设置页显示未授权/已授权/已撤回，提供重新读取、撤回和重新授权。撤回不删除已有本地结果；重新授权后可显式恢复原待处理队列。`consent-{batchId}` 保留在请求合同中但不作为授权来源。

## 后果

- 授权事实由本机持久化状态和事件历史提供，可跨重启审计，provider DTO、账号和云服务均不进入领域或 Presentation。
- 授权门与预算、上传和未知结果安全门分层，撤回不会污染已有结果，也不会产生重复计费。
- 未来如接入受批准的网关授权，可替换 `IConsentPort` 实现而不改变 AI 请求合同；任何新授权范围或跨设备同步仍需单独产品批准和 ADR。

## 验证

- `AiConsentStoreTests`：授权/撤回跨重启、consent ID 保留、事件历史和缺失默认空。
- `AiAnalysisSchedulerTests`：缺失/撤回在预算和 provider 前暂停，重新授权后恢复；provider 调用和预算请求数在 fail-closed 前均为 0。
- 完整 solution：355 通过、1 个 opt-in `RealProbeThroughFormalAdapter` 跳过、0 失败；应用 build 0 警告/0 错误。
