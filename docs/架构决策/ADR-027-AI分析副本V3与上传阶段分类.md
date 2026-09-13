# ADR-027：AI 分析副本 V3 与上传阶段分类

## 状态

已批准并实现（2026-09-12）。

## 背景

在实际 Windows 网络上，旧 V2 组合的 24 图最终 JSON 为 `13,116,193` bytes。传输
探针显示该请求在北京时间 17:09:44 开始发送，180 秒截止时请求体仍未写完，火山
后台没有对应账单或供应商请求标识。把这类失败统称为“结果未知”会错误地阻止安全
重试，也会让用户误以为火山已经开始推理。

同一组源照片可以在不修改源文件的前提下生成更小的应用内部分析副本。V3 需要和
缓存 fingerprint、请求合同及既有 V1/V2 读取兼容性明确隔离。

## 决策

1. 新请求默认使用应用内部 `wic-bounded/max1280/jpeg-q76/png-opaque-jpeg/v3`：
   最长边 1280、JPEG quality 76；不透明 PNG 可确定性转 JPEG，透明 PNG 仍保留
   PNG。源照片、PSD 和正式输出永不改写；V1/V2 副本只读兼容已有恢复记录，不作为
   新请求默认 profile。
2. 任何压缩 profile 影响最长边、质量、MIME、像素门或字节门时，必须升级 profile
   版本并使 cache fingerprint 失效，禁止用旧缓存冒充新字节。
3. 适配器记录 `payload_ready`、`send_started`、分块 `upload_progress`、
   `upload_completed`、响应头和响应体阶段。请求体未完成的超时/网络中断映射为
   `AI_PROVIDER_SUBMISSION_TIMEOUT` 或 `AI_PROVIDER_SUBMISSION_NETWORK_FAILURE`
   （可安全重试）；上传完成后未收到响应头或响应体不完整继续映射为未知结果，禁止
   自动重发。
4. 默认仍保留单请求 16 MiB、24 张和 180 秒硬门；V3 只是降低正常网络下越过上传
   截止的概率，不放宽供应商、隐私、预算或未知结果边界。

## 证据

- 旧 V2：`13,116,193` bytes；V3：`7,056,022` bytes，减少约 46.2%。24 张源照片
  前后哈希一致。
- V3 真实请求 `probe_daily_alias_11_39c099ffd226`：北京时间 17:49:50.498 开始，
  17:51:51.087 上传完成（121.074 s），17:52:28.526 收到 HTTP 200（158.516 s），
  完整响应 13,062 bytes；供应商请求标识存在。
- V2 失败请求 `probe_daily_alias_11_4044b6978169`：北京时间 17:09:44.820 开始，
  17:12:44.860 截止；`upload_completed=false`、HTTP/供应商标识均为空，错误码为
  `AI_PROVIDER_SUBMISSION_TIMEOUT`，与火山后台无账单相互印证。
- 脱敏日志位于
  `tmp/ai01-transport-probe-20260912-v1-live/logs/ai-transport.jsonl` 和
  `tmp/ai01-transport-probe-20260912-v3-live/logs/ai-transport.jsonl`。

## 影响

- 失败 UI 可明确显示“AI 请求未送达 / 火山未受理 / 重试分析”，不再显示“结果未知、
  核对账单”这类不适用于未完成上传的文案。
- 真实账单仍以火山后台为准；客户端 usage 和刊例金额只作诊断，不作为扣费事实。
- 多款并发仍受上行带宽限制；后续 PERF-01 必须记录上传阶段耗时，并评估上传并发门，
  不能仅看 HTTP 并发数。
