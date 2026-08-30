# ADR-015：火山方舟 AI 适配、缓存与费用控制

## 状态

已批准并冻结供应商无关端口、V1 火山方舟适配边界、凭据和费用控制。2026-08-30 的 S2/S3 真实用量与产品决策已形成生产参数配置 v1；模型真实准确率、金标和整体产品验收仍留在 AI-S5/S7，本 ADR 不把候选模型写成已验收结论。

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

- AI-S2/S3 已冻结压缩、传输、并发、重试、阈值和单批费用预算配置 v1；候选模型仍以 `proposed` 配置参与后续 S5 金标/P95/数据处理验收，未通过前不得标为完整生产模型结论。
- 人工选图仍可跳过候选判断；需要主体级智能适配时只对最终选中的压缩图分析并优先命中缓存。
- 该 ADR 不授权设置页增加模型或供应商选择。

### Windows 分析副本编码实施补充（2026-08-30）

- 首发 Windows 实现使用 .NET WindowsDesktop framework 自带的 WPF/WIC 编解码栈，不新增第三方图像包。`Desktop.Infrastructure` 与其测试目标改为 `net10.0-windows`；Domain、Application、Contracts 和 renderer-neutral `RenderPlan` 保持 `net10.0` 且不引用 WIC/WPF。
- 新建副本只接受 INPUT-01 已登记并校验的 JPEG、PNG、TIFF 源。JPEG 输出 JPEG，PNG 输出 PNG 并保留 alpha，TIFF 输出 JPEG；EXIF orientation 1–8 在缩放前归一，输出为单帧且不复制 EXIF、注释或本机路径元数据。
- 正式分析副本配置为 `wic-bounded/max1600/jpeg-q82/png/v1`：最长边 1600、JPEG quality 82、单源文件 64 MiB、单副本输出 8 MiB、源像素 100M。尺寸和像素门在展开像素前检查，编码写入使用硬字节上限；源 hash 在编码前后复核，副本/sidecar 失败回滚只删除应用自有文件。历史 `.../v1-candidate` 仅用于识别既有记录，不得作为新请求的默认 profile。
- compression profile 版本与全部影响编码字节、尺寸或资源门的参数必须成对变更；正式候选版本不得在最长边、JPEG quality、源/输出字节或像素门漂移后继续复用同一 cache fingerprint。
- 产品负责人已确认 V1 当前源图业务范围：常见单图约 20–30MB，约 50MB 必须可用；接受每张源图 `64 MiB` 的本地读取上限，暂不向上兼容 67–100MB 单图。该上限按单文件计算，不是文件夹或批次总量；超限必须在读取/解码前明确拒绝，不能静默降质或改写源图。
- 该 WIC 配置只生成应用自有分析副本；用户原图和 PSD 永不上传。单副本 8 MiB 与 HTTP 序列化请求体 16 MiB 是两个独立硬门，后者为 JSON/Base64 膨胀和结构开销预留；任一超限均在 HTTP 前拒绝，不静默降低画质或改写源图。

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

### 2026-08-30 Provider 请求 Schema 对齐

针对上述失败证据，复核确认 provider 请求中的 `ResultSchema()` 曾只声明数组类型，未约束数组元素，和本地 `ai-analysis-v1` 合同不一致。现已在 adapter 内联完整的结果元素结构与 `additionalProperties=false`，并在提示中禁止 `matched` 等额外字段；离线测试覆盖请求体结构。本修正不改变供应商无关端口、图片必传门或本地严格校验；必须取得新的单次审批并获得可校验真实结果后，才能评估是否需要进一步合同调整或冻结任何成本参数。

### 2026-08-30 Schema 对齐真实结果

产品负责人通过工具批准后，正式 adapter 使用同一批准 JPG 临时副本完成唯一 1 次请求：`Completed`，响应 5,280 bytes，约 14.1 s，输入 2,745/输出 205/合计 2,950 tokens，cache hit=false，严格本地合同通过且无 Schema 失败路径。该证据确认 provider 请求 Schema 对齐方案可行，不改变供应商无关合同和 renderer-neutral 边界。由于只有单图且没有账单金额事实，不据此冻结压缩、多图/并发、重试、告警或硬停止参数。

### S2/S3 生产参数配置 v1（2026-08-30）

产品负责人结合正式 WIC 单图成功路径、三图 provider 计量、官方模型限制/刊例价和离线恢复证据，批准以下 V1 参数。该决策冻结运行保护参数，不等于模型准确率或 AI-01 整体验收：

- 分析素材：只上传应用本地生成的 `wic-bounded/max1600/jpeg-q82/png/v1` 副本；最长边 1600、JPEG q82、单源 64 MiB、单副本 8 MiB、源像素 100M，原图/PSD 永不上传。
- 传输：序列化 HTTP 请求体最大 16 MiB；单请求最多 24 张素材；请求超时 180 秒。24 张是数量上限，不覆盖 16 MiB 字节门，较大的副本组合会更早被字节门拒绝。
- 调度：本地 AI 工作缓冲容量 32，真正同时联网最多 8 笔；单款最多 3 个逻辑请求（联合理解、已选素材主体、确有尺码来源），每个请求最多 2 次实际尝试。约 200 款可全部进入持久化批次任务，但不会无界同时提交 HTTP。
- 重试：只有明确安全的 429、临时 5xx 或已确认未被 provider 接受的失败才有限重试；`AiResultUnknown` 永不自动重发。第 2 笔三图请求虽然已由外部用量证明被 provider 接受/处理并计量，客户端结果仍保持 `AiResultUnknown`。
- 单批 runaway 门：最多 1,200 次实际尝试（包含重试）、1,000 万输入 tokens、200 万输出 tokens；10 CNY 告警、100 CNY 硬停止。该门限制单批继续发请求，不读取或限制账户余额。
- 费用估算采用当前官方刊例价：输入 6 CNY/百万 tokens、输出 30 CNY/百万 tokens；缓存命中 1.2 CNY/百万 tokens 仅作外部价格事实记录，当前本地估算公式按实际输入/输出 usage 计算。正式单图 `2,745/248` 估算 0.02391 CNY；三图 `5,296/372` 估算 0.042936 CNY；两笔合计估算 0.066846 CNY。金额均为客户端按刊例价估算，不是账单事实，折扣、资源包和最终账单金额仍未知。
- pricing 注入必须使用 `CNY`，输入/输出单价分别不得低于 `0.006/0.030 CNY/千 tokens`；零价、低价或其他币种会延后/绕过冻结的 10/100 CNY 门，必须在 scheduler 构造前 fail closed。允许更高 CNY 单价，因为它只会让告警/硬停止更早发生；未来降价或币种变化必须通过新的参数配置版本重新批准，不得静默覆盖本版本。
- 当前候选模型官方限制页记录 500 RPM、100 万 TPM；并发 8 远低于该账户无关上界，同时为 UI 响应、持久化、网络抖动和未知结果收口保留有界空间。并发可在后续 `PERF-01` 以新配置版本调整，不允许“全部 submit 后全部 collect”的无界 HTTP。

实现必须在 ledger/provider 前执行素材数和单款逻辑请求数门，在构造 HTTP 后执行精确 16 MiB 字节门，并以 semaphore/固定 worker 实际限制并发；预算 request count 在每次可能发 HTTP 前预留，因此 1,200 统计实际尝试而非逻辑款式数。

### reviewed diff 并发与传输闭合补充（2026-08-30）

- buffer 32 是进程内 ready 水位，不是 durable admission 上限。合法批次的约 200 款全部先进入 request ledger、scheduler snapshot 和 lease 图；channel 满时留在持久化 backlog，由 worker 完成后续投，重启只装填前 32 笔并继续补位，不能返回不可恢复 `QueueFull` 或释放仍待处理副本。
- full-batch checkpoint 的 scheduler snapshot、非目标 graph 重读、命令构造和 SQLite/manifest commit 必须在同一 scheduler 持久化 gate 内完成。该 gate 不覆盖 provider 调用，8 路 HTTP 并发保持不变。
- budget reservation 与持久化共用同一 gate；保存成功后才可越过 HTTP 边界，失败只撤销本次未生效 reservation。provider 已返回的 usage 只能单调累加；保存失败保留完整累计并 fail closed，不得用 stale snapshot 覆盖其他 worker。
- 生产 `HttpClient` 自身不设置早于 180 秒的 timeout，统一由 adapter CTS 分类。`VolcArkAiOptions` 的 24 张、180 秒、16 MiB 均为不可放宽上界，测试和受控环境只能收紧。
- HTTP 前只接受正式 WIC profile 或参数相同的历史 candidate 恢复记录；request、asset 和实际 copy 的 profile、SHA-256、长度、MIME 必须三方闭合。正式 profile 不得上传 provisional/candidate 偷换字节，TIFF 只允许作为本地 WIC 源，不能作为 provider 分析副本 MIME。

### reviewed diff durable 状态机闭合补充（2026-08-30）

- full-batch checkpoint 的 terminal graph 与 scheduler pending 删除必须作为同一 durable snapshot 提交；成功后仍在同一 persistence gate 内同步内存镜像。并发 provider 不受该短临界区影响，仍按冻结值最多 8 路。
- ready buffer 补位和恢复必须以 request ledger 为准，只允许 `Queued/RetryableError`。`Uploading/Analysing` 可能已经到达 provider，必须保留为未决证据，禁止重新入队或提前释放分析副本 lease。
- hard-stop 不删除 durable pending，但停止自动补位；reservation 保存失败只阻断该次可能 HTTP 的 work item，不能形成 dequeue/requeue 热循环。真实 hard-stop 快照在重启后也不自动发请求。
- retry 的递增 attempt 必须和 `RetryableError` 状态放在同一个 checkpoint。即使在回到 `Queued` 前崩溃，恢复也只能从已递增的 attempt 继续；每个逻辑请求最多 2 次实际 HTTP 的硬门不得因崩溃被重置。
- pending 取消必须在当前进程释放其 work lease；`Uploading` 在 scheduler 中仍位于 provider 调用前，可与取消在同一 persistence gate 内安全闭合。`Analysing` 则按可能已越过 HTTP 处理，不猜测取消或结果。
- `AiAnalysisQueueOptions` 和 `AiAnalysisBudgetPolicy` 的正式构造边界只允许从 V1 冻结值收紧，不能通过依赖注入扩大 32/2/8/24/3 或 1,200 次/1,000 万/200 万/10 CNY/100 CNY。真实 probe 与生产组合根都禁用 `HttpClient` 自身 timeout，由 adapter 的 180 秒 CTS 统一分类。

## 退出条件

若方舟合同不能满足隐私、延迟、结构输出或费用门，保留 `IAiAnalyzer` 端口并由新 ADR 选择兼容供应商；不得把供应商响应直接暴露给 Domain，也不得降低 PRD 准确率和原图保护要求。
