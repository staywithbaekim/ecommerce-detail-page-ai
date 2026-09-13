# ADR-026：AI 适配器硬截止与迟到 HTTP 任务清理

## 状态

已批准并实现（2026-09-12；含 2026-09-12 传输阶段分类补充）。

## 背景

`VolcArkAiAdapter` 原先只把 `CancellationTokenSource.CancelAfter(180s)` 传给
`HttpClient.SendAsync` 和响应体读取。正常的 `HttpClientHandler` 会响应取消，但
手动 WPF 运行证据显示，某次参考学习请求从 `2026-09-11T12:41:39.8845517Z`
到 `2026-09-11T13:09:22.386355Z` 才落盘，约 1662 秒，远超批准的 180 秒边界。
本地副本准备探针只用约 4.3 秒，因此阻塞发生在 HTTP/响应读取等待，而不是 PSD
解析或 WIC 压缩。结果最终被安全保存为 `AiUnavailable`，但用户长时间看不到结果。

这也解释了为什么隔离验证成功而手动操作失败：隔离根已有可复用的成功缓存和不同
的页面/frame 指纹；手动根没有 cache，重新建立了独立请求，恰好落入未能及时响应
取消的 HTTP 路径。两条路径不是同一个请求，也不是 UI 把成功语义覆盖成占位文字。

## 决策

1. `AnalyzeAsync` 与 `AnalyzeReferenceLearningAsync` 共用一个私有
   `HttpRequestScope`。scope 以单调时钟计算总 deadline，`Task.WaitAsync` 对发送和
   响应体读取分别施加剩余时间；不再只依赖底层 handler 是否执行取消回调。
2. deadline 到达时按已观察到的传输阶段分类，而不是把所有超时都归为未知结果：
   - 请求体尚未完整写入传输通道（`upload_completed=false`）时返回
     `AiProviderOutcome.RetryableError` / `AI_PROVIDER_SUBMISSION_TIMEOUT`，因为没有
     形成可被供应商处理的完整请求，可按既有安全上限重试；
   - 请求体已完整写入但尚未收到响应头时返回
     `AiProviderOutcome.AiResultUnknown` / `AI_PROVIDER_RESPONSE_HEADERS_TIMEOUT`，
     因为供应商是否接收仍未知，禁止自动重发；
   - 已收到响应头但响应体未完成时返回
     `AiProviderOutcome.AiResultUnknown` / `AI_PROVIDER_RESPONSE_BODY_TIMEOUT`。
   外部调用方主动取消仍返回 `Cancelled`。网络异常使用同一阶段边界，分别映射为
   `AI_PROVIDER_SUBMISSION_NETWORK_FAILURE`、`AI_PROVIDER_RESPONSE_HEADERS_NETWORK_FAILURE`
   或 `AI_PROVIDER_RESPONSE_BODY_NETWORK_FAILURE`。
3. 前台停止等待后，取消底层 token，并以后台 observer 观察迟到的 HTTP/body task。
   只有 task 被观察到后才释放 `HttpRequestMessage`、`HttpResponseMessage` 和关联
   CTS，避免未观察异常及“handler 仍在使用已释放请求”的竞态。响应体读取使用同一
   总 deadline，不为第二阶段重新开一个 180 秒窗口。
4. 适配器本身不增加重试；只有提交阶段的 `RetryableError` 交由 scheduler 的既有
   安全上限处理。未知结果仍不自动重发、不删除未知请求记录、不绕过 cache/ledger，
   也不改变请求体、授权、费用或 16 MiB/24 张安全门。

## 取舍与边界

- 极端 handler 永不结束时，后台 observer 可能暂时保留该请求资源；这是为了不在
  前台返回后破坏仍运行的 transport。正常 handler 会在取消后完成并释放。
- 硬截止只保证适配器调用方不再被底层 task 无限等待；提交未完成时可明确判断为未
  形成完整请求，上传完成后的阶段仍不证明供应商是否已经接受，因此保持未知且不可
  自动重发。
- 该决策覆盖普通 `ai-analysis/v1` 和参考学习 `reference-learning-ai/v2`，不改变
  WPF 对失败结果的 `AiUnavailable`/人工复核投影。

## 2026-09-12 账单边界与真实传输证据补充

产品负责人核对火山后台后确认 14:25 之后对应失败时段没有账单。脱敏传输日志与此
一致：请求 `probe_daily_alias_11_4044b6978169` 在北京时间 17:09:44.820 生成
13,116,193 字节请求体并开始发送，至 17:12:44.860（180.644 秒）仍为
`upload_completed=false`、`response_headers_received=false`、HTTP 状态为空、
`provider_request_reference=null`，最终为 `AI_PROVIDER_SUBMISSION_TIMEOUT`；迟到任务
只报告 `TaskCanceledException/OperationAborted`，没有供应商响应标识。因此这次是本机
发起了上传尝试，但没有形成“火山已受理/可计量调用”，不能称为火山推理超时。

同一网络下，后续唯一的 V3 请求 `probe_daily_alias_11_39c099ffd226` 使用
7,056,022 字节请求体，于北京时间 17:51:51.087 完成上传，17:52:28.526 收到 HTTP 200
和供应商请求标识 `021789206591875ab536ac757ac87e581a3dec3853327178234f5`，并完整读回
13,062 字节响应。这是另一笔已被供应商受理的请求，客户端记录 usage `33,568/2,413`
仅为估算/诊断，最终金额以火山账单为准。

V3 请求体比旧 V2 的 13,116,193 字节减少约 46.2%；实测上传速率约 0.47 Mbps，按该
速率旧体积仅上传就约需 225 秒，超过 180 秒硬截止。该证据同时支持“账单无记录 =
该失败请求未被供应商受理”的本次判断，以及保留 180 秒保护、降低分析副本体积的方案。

## 验证

- `VolcArkAiAdapterTests` 新增发送阶段和响应体阶段的“忽略取消”探针；短 timeout
  均在 1 秒内返回 `AI_PROVIDER_TIMEOUT`，迟到 task 释放后无未观察异常。
- `ReferenceLearningAiInfrastructureTests` 对参考学习同样覆盖发送/响应体两阶段。
- 聚焦基础设施回归：4 个新硬截止测试通过；适配器与参考学习基础设施合计
  72 通过、3 个显式真实探针跳过、0 失败。
- 真实手动 UI 尚未由 Computer Use 走查：当前控制面 `sky.list_apps` 返回
  `Trusted RPC service is not configured: sky`，因此不把自动化测试冒充窗口验收。
