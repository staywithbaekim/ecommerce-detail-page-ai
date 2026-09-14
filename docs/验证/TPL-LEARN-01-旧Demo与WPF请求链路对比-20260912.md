# TPL-LEARN-01：正确旧 Demo 与当前 WPF 请求链路对比

## 结论

本记录只比较用户后来纠正的 UAT5 包：

`C:\Users\79122\Documents\ChatGPT\全新实现\tmp\legacy-0.2.8.2-recovery\dist\详情页自动化-0.2.8.2-双布局-UAT5-最终优化版.zip`

该 ZIP 的实际 SHA-256 为 `1ADD29EF82E70CED67389D85E68C64D1D9FF015599F4FC3676DB3C5A1B86963F`。
包内 `release-manifest.json` 的 `source_zip_sha256` 是它构建时引用的原始 0.2.8.2 源包哈希（`3DA06F...201910`），不是本次收到的 ZIP 自身哈希，二者不能混用。

旧 Demo 能成功，不是因为火山给它特殊待遇，也不能由此证明两套提示词、模型和结果合同完全相同。已闭合的主要差异是：

1. 旧 Demo 的 24 图请求体约 `8.524–8.526 MB`（模式 2 运行时提示词补丁后的实测值为 `8,526,303` bytes）；当前 WPF 旧 V2 请求体为 `13.116 MB`，V3 已降到 `7.056 MB`。
2. 旧 Demo 调用 `requests.post(..., timeout=180)`。Requests 的单个 `180` 是连接/读取无数据超时，不是整个上传、推理和读响应的 wall-clock 总截止时间。
3. 当前 WPF 的 `HttpRequestScope` 是显式单调时钟硬截止：从 `SendAsync` 前开始，覆盖 HTTP 内容序列化/上传、等待响应头和读取响应体；180 秒到达即结束本次传输。它与旧 Demo 的 `timeout=180` 不是同一种语义。

因此，14:25 后那次 WPF 失败的直接原因仍是本机上传未在 180 秒内完成，不是火山已经推理了 180 秒：日志没有 HTTP、供应商 request reference 或 `upload_completed`，且用户在火山后台没有看到对应账单。

## 核验方法

- 对正确 ZIP 的恢复目录 `tmp/legacy-0.2.8.2-recovery` 做只读字节码和配置检查。
- 加载恢复出的 `core/vision.pyc`，将 `requests.post` 替换为本地假 HTTP 捕获函数；真实执行旧 Demo 的图片读取、最长边缩放、JPEG 编码、Base64 和 JSON 组装，不连接网络、不调用真实 API。
- 使用同一组 24 张目标照片副本：`tmp/tpl-learn-independent-target-20260912-exact24/简唏7707-75`。
- 对当前 WPF 只读取已存在的脱敏传输日志，没有重新发起真实请求。

## 正确旧 Demo 的实测证据

恢复字节码位置：

- `recovered-bytecode/core/vision.pyc`
- `recovered-bytecode/application/photo_selection_service.pyc`

从 `core.vision` 的常量和指令可确认：

| 项目 | 旧 Demo 实现 |
| --- | --- |
| endpoint | `https://ark.cn-beijing.volces.com/api/v3/responses` |
| 单次图片上限 | 40 张 |
| 压缩总量门槛 | 24 MiB（Base64 解码后的估算值） |
| 图片副本 | Pillow 转 RGB，最长边 1280，JPEG quality 82，`optimize=True` |
| 调用方式 | 一次 `requests.post`，`json=payload`，`timeout=180` |
| 结果处理 | `response.raise_for_status()` 后读取 JSON |

`PhotoSelectionService.recommend()` 会把当前款式全部未命中缓存的图片收集到一次 `analyzer.analyze(tuple(...))` 调用中；不是每张图片各发一次请求。对同一 24 张照片执行假 HTTP 捕获得到：

| 捕获字段 | 实测值 |
| --- | ---: |
| 图片数 | 24 |
| 原文件总大小 | 115,669,269 bytes |
| endpoint | 与当前 WPF 默认 endpoint 相同 |
| `timeout` 参数 | 180 |
| Base64 字符总量 | 8,520,192 |
| 紧凑 UTF-8 JSON 请求体 | 8,523,950 bytes（核心提示词）；模式 2 运行时提示词补丁为 8,526,303 bytes |

捕获函数没有返回供应商结果，只用于验证请求组织；没有产生费用。

旧 Demo 的任务是通用角色分类（`front/back/sleeve/collar/fabric/care_label/...`），随后由本地固定模板 profile 将角色绑定到 PSD 插槽。它不是当前 WPF 的“每个图片框带动态 `selection_requirement`、严格引用闭合和结果 schema”的请求。
旧 Demo 会把当前款式目录中所有可分析图片放入这一次分类请求；因此它的请求成功只证明“整组传输和角色分类可以完成”，不证明 PSD 中每个有效图片框与源照片的反向绑定一定正确，也不证明目录里未被 PSD 使用的多余照片已被排除。

## 当前 WPF 对照证据

来自现有脱敏日志：

| 项目 | WPF V2 失败请求 | WPF V3 成功请求 |
| --- | ---: | ---: |
| 请求体 | 13,116,193 bytes | 7,056,022 bytes |
| 图片数 | 24 | 24 |
| 上传完成 | 否（180 秒时仍在上传） | 是，121.074 s |
| HTTP | 无 | 200 |
| 供应商 request reference | 无 | 有 |
| 完整响应 | 无 | 13,062 bytes |
| 终态 | `AI_PROVIDER_SUBMISSION_TIMEOUT`，可安全重试 | `Completed` |

证据文件：

- `tmp/ai01-transport-probe-20260912-v1-live/logs/ai-transport.jsonl`
- `tmp/ai01-transport-probe-20260912-v3-live/logs/ai-transport.jsonl`

当前实现位置：

- `src/Desktop.Infrastructure/Ai/VolcArkAiAdapter.cs`：`BuildProviderPayload`、`HttpRequestScope`、上传阶段日志和 180 秒校验。
- `src/Desktop.Infrastructure/Ai/FileAiAnalysisCopyPreparer.cs`、`WicAnalysisImageCodec.cs`：V3 分析副本。

## 对用户问题的直接回答

### 为什么旧 Demo 能成功，当前 WPF 会失败？

在已观测的失败中，首要差异是请求体大小和截止时间语义：按同一网络实测上行速度，`13.116 MB` 的 V2 请求预计需要约 225 秒，超过 WPF 的 180 秒硬截止；旧 Demo 的请求体约 `8.524 MB`，并且 Requests 不把 180 当作整个上传流程的总墙钟期限。当前 V3 已将请求体降至 `7.056 MB`，同一网络实测上传 121 秒并收到 HTTP 200。

提示词和结果合同也不同：旧 Demo 是固定角色分类，当前 WPF 是动态规则/严格 schema。它们可能影响供应商处理时间，但不能把它们混为同一请求，也不能仅凭旧 Demo 成功推断模型 ID 一样。旧包没有随包提供真实 `.env`，所以旧 Demo 当时实际使用的 model ID 无法从 ZIP 只读确认。

### 上传时间算不算在当前 WPF 的 180 秒里？

算。WPF 的硬截止从 `SendAsync` 前开始，HTTP 请求体的序列化/写入、上传、等待响应头和读取响应体共享这一个截止时间。图片扫描、WIC 副本准备和本地 payload 构建发生在进入该传输截止前，不占 `HttpRequestScope` 的 180 秒，但会体现在总业务耗时里。

旧 Demo 的 `requests.post(timeout=180)` 不能这样解读；它主要是连接和读取“无数据等待”保护，不是同一类总计时器。

### 要不要直接改成 300 秒？

当前不建议直接把生产硬门改成 300 秒。`ValidateOptions` 明确拒绝超过 180 秒，ADR-026/027 也把 180 作为未知结果、重试和费用保护边界。直接放宽会带来两个风险：

- 上传完成后供应商已受理、但客户端迟迟没有响应时，结果可能变成未知，盲目重试可能重复计费；
- 更长的单请求占用会放大批量并发时的上行拥塞和 UI 等待。

目前已经采取更低风险的修复：默认 V3 分析副本、上传/响应阶段分类、上传未完成时才允许安全重试、上传完成后未知结果禁止自动重发。若实测仍有大量慢线路失败，应另立“上传阶段预算 + 推理阶段预算”的设计，连同幂等/查询、状态机、费用门和 UI 文案一起验收，而不是只把常量改成 300。

项目其他文档中的“300 秒”是 PRD/性能验收口径：从用户点击扫描到整批文件原子提交的端到端目标；它不是把单次火山 AI provider 请求的 180 秒传输/未知结果边界改成 300 秒，二者不能混用。

## 当前状态与下一步

- 没有修改源 PSD、源照片、ZIP，也没有在本轮重新调用真实 API。
- 旧的 `buyershow_v14_dev/ClothingBatchTool_V1.4-final.zip` 已排除，不再作为对比证据。
- 当前生产默认保持 V3 + 180 秒；失败批次不要复用，使用最新调试包建立新批次验证。
- 若用户要复测，优先观察 `ai-transport.jsonl`：`upload_completed=false` 是“请求未送达/未受理”；`upload_completed=true` 后无响应才是“结果未知”。
