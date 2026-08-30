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
5. 缓存键为 `SHA-256(sorted asset content_hashes + analysis_kind + model_config_version + prompt_contract_version + response_schema_version + compression_profile_version + requirements_digest)`；人工选择、状态和本地路径不进入键。缓存值记录 consent、版本、用量和来源，活动任务引用受保护。显式或自动失效只在业务 checkpoint 内把引用单向标为不可复用，保留历史 graph；同 fingerprint 不原地重算，只有内容/合同/配置变化形成新 fingerprint 才允许新请求，force-reanalysis generation 不在当前 S3 范围。
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

### Windows 分析副本编码实施补充（2026-08-30）

- 首发 Windows 实现使用 .NET WindowsDesktop framework 自带的 WPF/WIC 编解码栈，不新增第三方图像包。`Desktop.Infrastructure` 与其测试目标改为 `net10.0-windows`；Domain、Application、Contracts 和 renderer-neutral `RenderPlan` 保持 `net10.0` 且不引用 WIC/WPF。
- 新建副本只接受 INPUT-01 已登记并校验的 JPEG、PNG、TIFF 源。JPEG 输出 JPEG，PNG 输出 PNG 并保留 alpha，TIFF 输出 JPEG；EXIF orientation 1–8 在缩放前归一，输出为单帧且不复制 EXIF、注释或本机路径元数据。
- `wic-bounded/max1600/jpeg-q82/png/v1-candidate` 只是可审计的候选配置：最长边 1600、JPEG quality 82、源 64 MiB、输出 8 MiB、源像素 100M。尺寸和像素门在展开像素前检查，编码写入使用硬字节上限；源 hash 在编码前后复核，副本/sidecar 失败回滚只删除应用自有文件。
- compression profile 版本与全部影响编码字节、尺寸或资源门的参数必须成对变更；正式候选版本不得在最长边、JPEG quality、源/输出字节或像素门漂移后继续复用同一 cache fingerprint。
- 产品负责人已确认 V1 当前源图业务范围：常见单图约 20–30MB，约 50MB 必须可用；接受每张源图 `64 MiB` 的本地读取上限，暂不向上兼容 67–100MB 单图。该上限按单文件计算，不是文件夹或批次总量；超限必须在读取/解码前明确拒绝，不能静默降质或改写源图。
- 上述确认只冻结 V1 的单图源文件读取上限。`max1600`、JPEG q82、输出 8 MiB、源像素 100M、单款请求上限及 S3 并发/重试/费用参数仍需在逐次批准的最少量正式方舟探测中记录脱敏尺寸、字节、tokens、延迟和费用摘要后冻结或修订。

### 2026-08-30 真实参数探测结果补充

产品负责人批准使用三张本地图片进行最小探测；本轮仅选第 1 张 JPG，在应用自有临时副本上通过正式 `VolcArkAiAdapter` 执行 2 次单图请求。两次均返回 `TerminalError`，第 2 次稳定错误码为 `AI_PROVIDER_RESPONSE_INVALID`；输入 192,586 bytes，耗时约 11.5 s / 8.2 s，adapter 未解析到 tokens 或费用，临时副本和环境变量已清理。相同响应阻断连续两次后按任务规则停止 workaround 链，不把该结果解释为零费用或可用结构化结果。由于没有形成可校验的 `ai-analysis-result/v1`，本轮不冻结或修订模型、WIC compression profile、单款请求上限、并发、重试、告警线或硬停止线；这些参数继续保持 candidate/pending，待后续批准的技术修正和真实证据评估。

### 响应形状脱敏诊断（2026-08-30）

为定位 `AI_PROVIDER_RESPONSE_INVALID`，`VolcArkAiAdapter` 增加默认关闭的 `ResponseShapeDiagnosticSink`。启用时只允许记录响应字节数、JSON 根类型、受限顶层字段名/数量、状态分类、`output_text` 存在性、输出块类型、固定失败阶段及首个 Schema 失败实例路径/关键字；不记录任何响应值、response ID、媒体、本机路径、Key、认证头或账单原文。`JsonSchemaContractValidator` 通过可选诊断接口使用 Hierarchical evaluation 提供首个路径，正式 `IJsonContractValidator` 行为与 `ai-analysis-result/v1` 严格 Schema 保持不变；诊断回调异常不影响 provider 结果。

已通过线程工具向产品负责人申请下一次仅 1 次真实请求（批准图片 #1、应用自有临时副本、约 192,586 bytes、图片必传、严格 Schema）；明确审批返回前不调用方舟。若获批，调用结束立即清理临时副本、环境变量和媒体；本轮费用事实仍为未知，所有生产参数继续 candidate/pending。

### 获批执行的凭据前置阻断（2026-08-30）

审批已通过，但正式 probe 在 HTTP 请求前由 `WindowsCredentialSource` 返回 `AI_PROVIDER_CREDENTIAL_MISSING`；`AI-01-VolcArk-Test` 目标当前不存在。故 provider 请求数为 0、未产生响应或费用证据，临时副本和环境变量已清理。补齐凭据后必须再次确认单次调用门；不允许以无图片请求或放宽 Schema 规避。

### 2026-08-30 针对性 Schema 诊断请求

凭据可读取后，产品负责人批准同一张批准 JPG 的应用自有临时副本，通过正式 adapter 发出唯一 1 次真实请求。请求保持图片必传和严格 `ai-analysis-result/v1`，调用后清理副本、环境变量和媒体。结果为 `TerminalError` / `AI_PROVIDER_RESPONSE_INVALID`，约 9.6 s，输入 192,586 bytes；usage/tokens/cache hit 和费用均未知。

默认关闭的形状诊断给出首个失败阶段 `schema`、实例路径 `/requirement_matches/0/matched`，关键字未提供。该证据只证明 provider JSON 到达严格本地合同但字段形状不匹配，不授权放宽合同、删除图片输入或冻结任何费用/并发参数。后续必须先完成字段语义评审，再重新申请单次真实请求。

## 退出条件

若方舟合同不能满足隐私、延迟、结构输出或费用门，保留 `IAiAnalyzer` 端口并由新 ADR 选择兼容供应商；不得把供应商响应直接暴露给 Domain，也不得降低 PRD 准确率和原图保护要求。
