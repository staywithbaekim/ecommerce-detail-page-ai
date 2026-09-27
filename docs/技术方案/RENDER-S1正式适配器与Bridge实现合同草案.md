# RENDER-S1 正式适配器与 Bridge 实现合同草案

日期：2026-09-19。范围：RENDER-01 S1 的正式身份、端口、DTO、校验、Application 调用和迁移。S1 **仍在进行中**；本文没有把合同单测、接口定义或 manifest 草案写成可执行插件/完整 S1 通过。

更新：主动连接增补已有机器 Schema、listener/client、认证向量和领取账本；正式开发插件已在 UXP Developer Tool 加载。本文原 §4 保留首次发现问题的依据，当前余项与实机限制以 [S1-S3 候选验证](../验证/RENDER-01-S1-S3传输与资源候选验证-20260919.md) 和 [传输增补 §7](RENDER-S1-UXP主动连接传输增补草案.md#7-当前候选的精确认证转录) 为准。

## 1. 插件身份与权限草案

机器可读草案：`render-adapter-manifest-v5.draft.json`。拟用 ID `local.ecommerce.detail-renderer`，名称“电商详情页渲染适配器”，版本 `0.1.0`，manifest v5 / Photoshop API v2。与验证插件 `local.ecommerce.uxp-validation` 0.4.7 分离，不复制其实现，也不把它注册为正式生产入口。

- Photoshop 最低版本写为 `26.8.1`，但 manifest 的最低版本不能代替精确支持构建白名单和逐机探针；更高版本不会自动获得正式支持。
- 网络权限草案仅列 `http://127.0.0.1`。仍须用 UXP Developer Tool 实测此声明对 rendezvous 和随机高位端口的覆盖；不得遇到失败就改为 `domains=all`。
- 文件权限已收窄为 `request`，通过认证资源字节写入插件自有副本；真实源路径不交给 UXP。`BridgeResourceLease` 已接入提交前匹配、分块 HTTP 和 broker 生命周期；UXP 下载校验模块存在，但 main/实际临时副本/输出暂存未接通。最小权限门仍待 S2/S3 实测，不能失败后静默扩大 fullAccess。
- 只保留连接状态入口；业务规则、素材选择和日常操作仍在桌面端。没有网页、外部进程、剪贴板、远程域名或任意命令权限。
- 文档 manifest 是草案镜像；可加载开发目录现在由 `uxp-render-adapter/build.cjs` 生成。没有创建 `.ccx` 或签名制品。可交付包的命名/ID/权限确认仍按任务书 §10 与 INSTALL-01 执行。

## 2. 边界与已实现的提交链路

`BridgeProtocol` 固定 `ps-bridge/v1`、rendezvous `17842`、随机执行端口区间 `49152–65535`。现有总体方案 §9.4 明确由桌面端返回执行端口；正式实现应由桌面端拥有回环监听和任务状态，UXP 作为受控执行端，不承担 AI、业务队列或 SQLite。

Application 提交链路现为：

```text
BridgePlanSubmissionService
  -> BridgePlanSubmissionValidator
     -> BridgeWireValidator + RenderPlanValidator
  -> IBridgePlanSubmissionPort（只有校验通过才调用）
  -> 校验响应大小/Schema/错误与 request_id、correlation_id
  -> 返回接收结果或结构化未知状态
```

端口使用冻结 wire JSON，Application 无 UXP、Photoshop 或 HTTP 实现类型。此次没有把尚无正式实现的端口注册到生产流程，也没有将既有 `IBridgeClient/MockBridgeClient` 改成正式适配器。`UnavailablePhotoshopAvailabilityPort` 继续阻断未实现的真实出图。

Infrastructure 的 `BridgeWireMessageReader` 按 endpoint/方向限量读取，在上限外最多读取一个判定字节；不信任 Content-Length，拒绝长度错配、非法 UTF-8、截断和无效 Schema。其有界 raw 读取已被 Kestrel listener 复用；HTTP owner 负责截止时间、认证、内容编码、header 和流生命周期。断流不返回部分 JSON，共享缓冲归还时清零，结果 `ToString()` 不输出原文。

端口必须明确区分 `NotSent`、`ResponseReceived` 和 `Uncertain`：

- 校验失败不调用端口；调用前取消不发送。
- 已开始调用后的取消、响应无法反序列化、响应身份错配或结果不明返回 `BRIDGE_EXECUTION_UNKNOWN`，不自动重试。
- 明确未发送返回 `BRIDGE_DISCONNECTED`；重连后仍由恢复编排决定下一步。
- `accepted` 只代表接收。正式输出完成必须由后续 checkpoint、manifest、JPG/PSD 和结构/ICC 证据闭合，不能用响应字段直接把业务页标成完成。

## 3. DTO、大小与拒绝边界

`BridgeWireV1.cs` 对应握手、计划提交、查询、取消、心跳、释放、响应及事件。token/proof/nonce/绑定句柄所在 DTO 的 `ToString()` 不展开秘密。

| 边界 | 当前校验 |
| --- | --- |
| 协议与路由 | 固定版本、严格 `envelope_kind`、endpoint/operation 对应；未知字段、重复 JSON 键、无效 endpoint 拒绝 |
| 字节上限 | UTF-8，普通消息 1 MiB，计划提交 8 MiB，心跳/执行查询 256 KiB；有界流读取已接入 listener；UXP fetch 流式读取限制另见新报告 |
| 错误 | 仅批准错误码、retryable/scope/本地文案；details 仅版本 expected/actual；不透传原始异常或路径 |
| 接收响应 | execution ID、状态及序号；事件 ID 不重复、序号严格递增且不超过响应水位、执行身份一致 |
| 提交计划 | RenderPlan Schema/摘要；请求与计划的 correlation/idempotency/digest 闭合 |
| 资源绑定 | 一一对应资源 ID、唯一 handle、hash/长度/用途；只读，禁止 analysis_copy、市场图角色及路径句柄 |
| 输出绑定 | 单文档对应单 artifact，覆盖策略一致；资源与输出句柄不串用；never 不接受覆盖确认 |

最后两行是**信封与计划的逻辑闭合**，不是实际文件读取或访问许可。实际根目录、真实字节、模板结构、授权目标 ID 和目标卷权限由 S3 执行前再次校验。真实市场尺码图不是合法的确定性尺码渲染资源。

2026-09-19 修复了冻结 Schema 的可复现歧义：握手拒绝体与普通拒绝体形状重叠，`body.oneOf` 会错误拒绝它们。改为 `anyOf`，仍由 `envelope_kind` 的 `allOf/then` 选择精确 body Schema；不新增字段、端点或版本，也不放宽 `additionalProperties=false`。三类错配 envelope 的反例已验证。

另将错误检查限定在信封声明的 error 和 events[].error，避免尚未经过 RenderPlan 校验的任意 payload 属性触发协议字段读取异常。SHA-256 十六进制大小写比较与既有 RenderPlan 摘要校验保持一致。

## 4. 首次合同缺口记录

本轮对照总体方案 §9.4、`bridge-protocol-v1.md` 和 `bridge-v1.schema.json` 发现三处尚未闭合的 wire 绑定。它们不应被接口或 mock 成功掩盖：

1. **挑战下发**：桌面端产生 nonce/session，UXP 提交 HMAC proof；当前只有提交 proof 的 `POST /handshake` Schema，尚无桌面挑战如何交给 UXP 的机器合同。
2. **任务下发与执行事件回传**：桌面监听而 UXP 发起请求的拓扑下，当前 `/plans` 只定义提交计划，没有定义 UXP 如何领取已接收计划、如何确认领取和如何上报执行事件。不能直接假定 UXP 提供 HTTP server，也不能用未认证轮询或验证插件替代。
3. **GET 查询认证绑定**：通用 request 包含 token/sent_at/idempotency_key，但 `GET /executions/{id}` 尚未规定 header 与信封的具体绑定。不能把 token 放 URL、日志或依赖 fetch 的 GET request body。

已形成 [UXP 主动连接传输增补草案](RENDER-S1-UXP主动连接传输增补草案.md)，明确候选拓扑、挑战、双方认证要求、计划领取/ACK 丢失、事件回传和 GET header 映射。现有 proof 之外的服务端身份验证也必须闭合。下一步将这些候选变成精确 DTO/Schema、认证测试向量及状态转移正反例，再实现 S2 listener/UXP client。新字段或端点须明确能力协商与拒绝策略；不得无文档地塞入已冻结 v1 信封。

这是真实的下一合同工作项，不是缺少用户授权、未装 Photoshop，或要求用户重启 Codex。当前先完成不依赖该合同决策的提交安全边界和测试。S1 整体不关闭，S2 实机握手/S3 实际绑定/S4–S7 出图保持未通过。

## 5. 迁移与实机证据

当前只支持 `ps-bridge/v1`；未知版本不降级、不猜字段、不自动迁移正在执行的请求。Schema 歧义修复属于既有 v1 预期响应的兼容修正，无持久化迁移。未来新增传输合同应先完成对应版本/能力的双向验证；破坏身份、幂等或执行语义的改动须新版本和 ADR，未知执行结果仍只能查询或人工确认。

本机 Photoshop 2025 与 UXP Developer Tool 已安装；EXE 属性为 `26.8 (20250624.r.8 5e0d05d)`，既有 B-20“关于”显示 26.8.1。此次已加载正式开发插件，发现 `app.version` 为 undefined，显示 UXP `uxp-8.3.0-uxp-b1d8c85`；完整构建读取与真实握手仍待核验。未打开 Photoshop 文档或产生实际输出。
