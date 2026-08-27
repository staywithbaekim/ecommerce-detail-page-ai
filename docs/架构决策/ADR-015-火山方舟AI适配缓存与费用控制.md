# ADR-015：火山方舟 AI 适配、缓存与费用控制

## 状态

已批准并冻结供应商无关端口、V1 火山方舟适配边界、凭据和费用控制；具体模型、压缩参数、并发、阈值和费用上限在 `AI-01` 金标验证后形成配置版本并记录为后续 ADR/修订。此 ADR 不把候选模型写成已验收结论。

## 日期

2026-08-27

## 背景

V1 使用火山引擎/方舟 API，客户提供自己的 API Key，由项目方协助配置；设置页不开放供应商、模型、Base URL 或高级参数。未来可替换为项目方统一网关，但不能让领域层依赖供应商 DTO，也不能因为重试或缓存造成不可控费用。

## 决策

1. Domain/Application 只依赖 `IAiAnalyzer`、`IAiCredentialSource`、`IAiUsageRecorder` 和版本化 `AnalysisRequest/AnalysisResult/UsageEvent`；火山方舟 SDK、端点、认证头和原始响应只存在于 Infrastructure adapter。
2. V1 adapter 固定使用项目批准的火山方舟服务配置；候选模型 `doubao-seed-2.1-pro-260628` 仅作为 AI-01 首轮验证候选，必须由真实金标、P95、费用和数据处理说明共同冻结后才可标为生产配置。
3. Key 只从 Windows Credential Manager 按请求作用域取出，禁止写入领域对象、日志、缓存、模板包、诊断包或 RenderPlan。撤回上传授权后不得发起新请求。
4. 上传只使用必要的压缩分析副本；不上传原始 PSD、高清源图、市场尺码图或本地路径。请求中使用内部 `analysis_copy_ref`，adapter 在内存/受控临时目录读取字节并在生命周期结束后清理。
5. 缓存键为 `SHA-256(sorted asset content_hashes + analysis_kind + model_config_version + prompt_contract_version + response_schema_version + compression_profile_version + requirements_digest)`；人工选择、状态和本地路径不进入键。缓存值记录 consent、版本、用量和来源，活动任务引用受保护。
6. 每次请求先写 request ledger 和 fingerprint；仅在官方合同确认未接受请求或明确安全的网络短断、429、临时 5xx 时有限重试。响应未知时标记 `AiResultUnknown`，不得盲目重发可能已计费请求。
7. 响应进入 Domain 前必须校验 Schema、资源/需求引用、rank 唯一性、置信度范围、几何有限值、尺码结构和版本。非法结果进入红色/待处理路径，不以模型“看起来合理”代替校验。
8. AI-01 记录请求数、缓存命中、重试、延迟、输入/输出计量和供应商费用摘要；客户端费用只作诊断，未来统一网关账单以服务端计量账本为权威。
9. 未来统一网关只替换 `VolcArkAiAdapter + CustomerCredentialSource`，保持领域合同和缓存语义兼容；账号、租户、配额、账单、支付和服务端隐私政策不提前进入 V1。

## 候选与取舍

| 方案 | 优点 | 风险 | 结论 |
| --- | --- | --- | --- |
| 客户 Key + 固定方舟 adapter + 领域无关 DTO | 符合 V1 试点，迁移网关时边界稳定 | 客户 Key 管理和供应商数据政策需说明 | **采用** |
| 客户 Key 直接进入 Domain/UI | 初期调用简单 | 泄露面大、供应商耦合、无法迁移 | 不采用 |
| V1 直接建设项目方统一网关 | 客户操作更简单 | 扩大账号/计费/隐私/运维范围，超出 PRD | 后续版本 |

## 影响

- `AI-01` 必须冻结模型配置版本、压缩配置、并发、重试、阈值、费用预算和供应商图片保留/训练政策证据；在此之前只能使用 `proposed` 配置。
- 人工选图仍可跳过候选判断；需要主体级智能适配时只对最终选中的压缩图分析并优先命中缓存。
- 该 ADR 不授权设置页增加模型或供应商选择。

## 退出条件

若方舟合同不能满足隐私、延迟、结构输出或费用门，保留 `IAiAnalyzer` 端口并由新 ADR 选择兼容供应商；不得把供应商响应直接暴露给 Domain，也不得降低 PRD 准确率和原图保护要求。
