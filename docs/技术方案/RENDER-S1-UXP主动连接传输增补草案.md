# RENDER-S1：UXP 主动连接传输增补草案

日期：2026-09-19。状态：实现候选，已有独立 `adapter-transport-v1.schema.json`、DTO、Kestrel listener 和 UXP client；§7 固定本轮实现的认证转录。真实 UXP 握手尚未验证，S1/S2 不标通过；不改写 `ps-bridge/v1` 或 RenderPlan 的既有字段。最新证据见 [S1-S3 候选验证](../验证/RENDER-01-S1-S3传输与资源候选验证-20260919.md)。

## 1. 拓扑与责任

总体技术方案 §9.4 指定桌面端分配执行端口。采用桌面监听、UXP 主动请求：

```text
Application 已校验计划 -> 桌面进程内 submission port -> 接收/查询账本
                                                        ^
                                                        | loopback HTTP
                                               正式 UXP 主动领取/报告
```

桌面提交端口和 HTTP listener 共享同一个接收服务与执行账本；不让桌面把计划 POST 给一个假定存在的 UXP HTTP server。UXP 只执行已校验的计划，不拥有业务队列、数据库、页面完成判定或原子发布权。

既有 `/plans`、查询、取消、心跳、释放和 `ps-bridge/v1` 信封保留原语义。UXP 拉取所需的新字段、端点和认证映射必须放在独立的 adapter 传输合同中，候选标识为 `ps-adapter/v1`；不得把未知字段塞入严格的 v1 信封。只支持 v1 的旧组件不具备新传输能力时拒绝建立可执行会话。

## 2. 候选端点和交付顺序

下表为传输目标。挑战、握手、领取/ACK、事件、心跳、释放、GET 查询和资源读取已有候选 HTTP 实现；`/plans` 当前仅为进程内 port，取消当前仅为 broker 本地操作。输出上传尚未实现。

| 端口/操作 | 发起方与接收方 | 最小语义 |
| --- | --- | --- |
| rendezvous `17842` / `POST /adapter/v1/challenges` | UXP → Desktop | 提交新客户端随机量、插件实例标识和版本信息；返回一次性服务端 nonce、session、过期时间和绑定上下文 |
| rendezvous / `POST /handshake` | UXP → Desktop | 消费挑战，保留冻结 v1 proof 公式；完成双方身份验证后签发执行 token/端口/能力 |
| 随机执行端口 / `POST /adapter/v1/claims` | UXP → Desktop | 在认证会话和唯一宿主门下原子领取一项计划；无任务明确返回空，不能伪造 execution |
| 随机执行端口 / `POST /adapter/v1/claims/{id}/ack` | UXP → Desktop | 证明收到指定 execution、摘要与领取标识；明确进入执行前的未知结果边界 |
| 随机执行端口 / `POST /adapter/v1/events` | UXP → Desktop | 回传绑定到该领取的有序事件；原 `event` 信封放在认证容器中，不能裸 POST 未认证 event |
| 随机执行端口 / `POST /adapter/v1/resources/read` | UXP → Desktop | 按已领取 execution/claim/digest/handle 和字节范围读取已打开的只读租约；每块最多 128 KiB，完整请求/响应由会话 MAC 认证 |
| 随机执行端口 / 既有心跳、查询、取消、释放 | UXP/桌面控制端 → Desktop | 遵守原有 v1 语义；禁止把桌面控制权限自动授予 UXP 报告端 |

执行端口只尝试安全分配的一次绑定；占用即返回 `BRIDGE_PORT_UNAVAILABLE`，不扫描或连接占用者。必须核验本地地址、Host、方法和路径，禁用代理/重定向，不启用通配 CORS；未知 Origin 在确定正式 UXP Origin 行为前拒绝。

## 3. 配对与双方身份验证的闭合要求

冻结 proof 只明确 UXP 向桌面证明共享秘密。新增的服务端挑战证明、会话密钥与响应 MAC 见 §7，已完成跨语言公开向量；生产通道继续等待真实运行时及余下执行/提交边界闭合。

新增合同必须同时固定以下内容，并提供跨 .NET/UXP 的公开测试向量（使用专用假秘密，不读取真实凭据）：

1. 服务端挑战如何绑定客户端新随机量、任务/插件实例、版本/能力和有效期；未配对挑战不披露任务内容、路径、资源或执行 token。
2. UXP 如何验证服务端持有配对秘密，以及如何验证握手响应的 session、端口、token 和能力未被篡改；客户端新随机量应阻断旧服务端响应重放。
3. 原 `nonce || session_id || plugin_manifest_version || photoshop_build` 的字节编码、拼接顺序、base64url padding 规则与常量时间比较。不能由两端各自猜测编码。
4. 新增双方认证上下文使用独立的、带用途标识的认证转录；精确定义长度和序列化，不能直接签一个未经规范化的任意 JSON 字符串。它不悄悄替换已冻结 proof 公式。
5. 挑战只能消费一次，失败也不能重放；候选有效期 30 秒。会话仍遵守 15 分钟无活动、8 小时绝对上限和实例变化立即失效的既定约束。
6. 配对秘密的首次分发及重新配对必须使用受保护存储/已批准一次性配对码路径；不能在技术实现中补一个明文文件约定。

版本、权限和身份的这部分补充应连同机器 Schema、错误映射及测试向量形成 ADR-013 的显式增补；本草案不自行声称安全协议已冻结或 Adobe 运行时能力已验证。

## 4. 领取、执行和断线

计划接收先以 `idempotency_key + plan_digest` 建立唯一 execution。相同键和相同摘要返回既有 execution；相同键配不同摘要拒绝，不能覆盖账本。适配器领取绑定 session、插件实例、execution 和摘要，只允许一个 Photoshop 通道。

领取响应可能丢失，ACK 响应也可能丢失。双方均不得把超时当作“未执行”：

- 领取响应丢失：查询同一领取状态，不能将其自动发给第二实例。
- UXP 发送 ACK 后未收到结果：查询同一领取的授权状态，不能再领取一份计划并执行。
- Desktop 收到 ACK 后失联：execution 保留 unknown/recovery 事实，不能仅因 lease 超时自动重新入队。
- Photoshop 副作用只能发生在确认本次领取仍被授权且本地宿主检查通过之后；重启导致旧 token/实例绑定失效时停止执行并查询持久化恢复记录。
- 取消在 modal 外安全边界生效；领取查询须能看见取消/释放，不能仅在 UXP 内存记一个标记。
- event 重发必须保留原 `event_id`、序号及内容；相同 ID 内容不同、跨 execution、倒退或不连续序号拒绝并查询恢复，不能用“取较大值”掩盖缺失事件。

`completed` 事件只描述执行端事实。业务完成仍须桌面核验实际 JPG/PSD、ICC、结构、hash、manifest 和 checkpoint。暂不定义“token 到期就删除全部临时文件”之类行为；可能包含未知输出时按 S6 恢复规则保留证据。

## 5. GET 查询映射候选

`GET /executions/{execution_id}?after_seq=N` 不发送请求体，URL 只含非秘密 execution ID 和非负 32 位事件序号。认证和请求元数据通过单值 header 映射回 v1 request，再用 `BridgeWireValidator` 校验；重复 header、未知 query、控制字符和 URL/信封 execution 不一致均拒绝。

| HTTP 来源 | v1 字段 |
| --- | --- |
| `Authorization: Bearer <token>` | `body.token`，禁止记录 header |
| `X-Bridge-Protocol-Version` | `protocol_version` |
| `X-Bridge-Request-Id` | `body.request_id` |
| `X-Bridge-Correlation-Id` | `body.correlation_id` |
| `X-Bridge-Session-Id` | `body.session_id` |
| `X-Bridge-Sent-At` | `body.sent_at` |
| `X-Bridge-Idempotency-Key` | `body.idempotency_key` |
| 精确路由 `{execution_id}` | `body.execution_id` |
| 固定路由值 | `envelope_kind=request`、`operation=get_execution`、`payload={}` |

`after_seq` 属于 HTTP 查询投影，不能添加进冻结的空 payload。header 总量上限候选 8 KiB，拒绝 token 多来源和压缩 body；响应与读取器仍受 256 KiB 上限。具备响应能力不等于已授权查询另一个任务，listener 必须再核对任务/session/execution 归属。

## 6. 下一实现门

1. 将第 2–5 节变成完整的传输 DTO/Schema、跨端认证向量和正反例；审查领取/ACK/断线状态转移，不提前写生产监听或放宽旧 v1。
2. 基于冻结增补实现有界 HTTP listener、受保护凭据端口、会话、单通道和 UXP 主动客户端；现有 `BridgeWireMessageReader` 可承担字节读取，但不能替代 HTTP/鉴权/状态机。
3. 同时覆盖 header/URL 认证、nonce/服务端响应重放、token 过期/轮换、实例变化、版本拒绝、抢占端口、领取/ACK 丢失、重复/错配事件；在支持机器取得真实握手证据。
4. 随后进入 S3 的实际资源绑定及 S4/S5 输出。此处不依赖真实尺码金标即可做离线设计与实现，也不要求重新安装 Photoshop 或重复授予本地开发权限。

以上是最初的实现顺序；1-3 的代码与离线子项已推进，当前余项以最新验证记录为准，不重复重建已有实现。

## 7. 当前候选的精确认证转录

`S(key, fields...) = base64url(HMAC-SHA256(key, concat(U32BE(UTF8(field).length), UTF8(field))...))`。每个字段独立带 4 字节大端长度，不含终止符；数字为无前导零的十进制 ASCII，能力按 ordinal 排序后以 LF 拼接。base64url 无 padding，nonce/key/token 必须是规范 32 字节编码。`H(raw)` 是原始 HTTP JSON UTF-8 字节的 SHA-256 小写 hex；不是解析后重新序列化摘要。请求方签名和接收方校验同一原始文本，因此 JSON 转义差异不会改变接收方所验字节。

| 用途 | `S` 的字段顺序 |
| --- | --- |
| 服务端挑战 | `ps-adapter/v1`, `server-challenge`, request_id, client_nonce, nonce, session_id, plugin_instance_id, task_binding, app_version, plugin_id, plugin_version, photoshop_build, uxp_runtime_version, issued_at_unix_seconds, expires_at_unix_seconds, sorted_capabilities |
| 会话 key | `ps-adapter/v1`, `session-key`, server_proof, client_nonce, nonce, session_id, plugin_instance_id, task_binding；结果解码为 32 字节 |
| 握手上下文 | `ps-adapter/v1`, `client-handshake`, server_proof, H(raw_handshake_request)；header `X-Adapter-Handshake-Proof` |
| 握手响应 | `ps-adapter/v1`, `handshake-response`, request_id, H(raw_handshake_response)；使用会话 key，header `X-Adapter-Mac` |
| POST 请求 | `ps-adapter/v1`, `request`, method, exact_path_and_query, request_id, session_id, plugin_instance_id, sequence, token, H(raw_body)；使用会话 key |
| GET 请求 | POST 行的字段（body 为空），再追加 `get-metadata`, protocol_version, correlation_id, sent_at 原始 header 值, idempotency_key |
| 请求响应 | `ps-adapter/v1`, `response`, request_id, session_id, plugin_instance_id, sequence, HTTP_status, H(raw_response)；使用会话 key |

冻结 v1 `pairing_proof` 保留独立公式：`HMAC-SHA256(secret, base64url_decode(nonce) || UTF8(session_id + plugin_manifest_version + photoshop_build))`。新增上下文 MAC 消除其可变长字段歧义，不悄悄替换原字段语义。服务端挑战 task_binding 是随机不透明值，不返回业务 task ID。

请求 headers：`Authorization: Bearer <token>`、`X-Adapter-Version: ps-adapter/v1`、`X-Bridge-Request-Id`、`X-Bridge-Session-Id`、`X-Adapter-Instance-Id`、`X-Adapter-Sequence`、`X-Adapter-Mac`；GET 另含 §5 元数据 headers。sequence 为 1 起严格递增、上限 9007199254740991 的整数。仅缓存最新一次已认证请求的完整签名响应；完全相同的 MAC/sequence 重放返回缓存，不执行、不续心跳。旧序号、跳号或相同序号内容变化拒绝。

挑战有效期 30 秒、最多 4 项并存，可辨识 session 的 Schema 拒绝也消费该挑战。正式安装 secret 路径尚未完成；探针显式使用 5 分钟一次性码模式，成功后销毁配对 secret 并清除其他挑战，不能当成安装级可复用 secret。

每 5 秒发送认证心跳；15 秒无新心跳进入断线，单通道未完成项持久化为 unknown。token 仍遵守 15 分钟/8 小时规则供恢复查询，不以 token 尚未过期断言宿主可执行。第一次有效 ready/idle 心跳前不能领取或 ACK。未知事件、断流、持久化故障不产生自动重提。

机器合同：`schemas/adapter-transport-v1.schema.json`、`BridgeAdapterValidator`；假密钥向量：`uxp-render-adapter/tests/auth-vector.cjs` 与 `BridgeAdapterCryptographyTests`。真实 secret/认证 headers 不写入运行日志或本文。S2 实机完成门仍开放。

## 8. 资源分块读取候选

新增独立 adapter kinds `resource_read` / `resource_chunk`，不改变 RenderPlan 或冻结 bridge 信封。POST 请求体包含 `claim_id、execution_id、plan_digest、session_handle、offset、count`；offset 是 0 至 9007199254740991 的安全整数，count 为 1 至 131072。响应包含相同 execution/digest/handle/offset/count、该块 `sha256` 小写 hex，以及无 padding 的规范 base64url `data`。请求与响应仍使用 §7 的原始 UTF-8 MAC，真实路径和字节不进入 URL、日志、journal 或诊断。

桌面 owner 先创建 `BridgeResourceLease`，通过 `RegisterResourcesAsync` 将所有权转给 broker，再经 Application 提交服务发送使用这些 bindings 的计划。新执行接收前逐一匹配已打开租约；缺少租约或伪造句柄返回 `BRIDGE_RESOURCE_BINDING_INVALID`，不写 accepted。相同 key/digest 的恢复查询不重新打开资源或重新执行。

读取必须有新鲜心跳，并匹配 session/claim/execution/digest/handle；只允许 claimed/authorized/running 且尚未取消的执行。一次读取最多 128 KiB，base64url 后及完整信封总长仍小于 256 KiB；越界拒绝，不截断。终态、unknown、取消、断线、释放、host Dispose 或 journal 写失败会关闭租约。最新已认证请求的精确重放仍返回缓存响应，不重新读取文件或续心跳。

UXP 下载模块逐块校验响应身份、规范编码、长度与块 hash，增量计算整资源 hash；仅在完整 hash 与 binding 一致时返回成功。写入目标必须为插件自有的未发布临时副本；本轮 `main.js` 尚未接入实际文件写入/领取/Photoshop 执行，不把模块单测视为运行时完成。下载失败清会话、不重试；临时文件的受控生命周期和输出上传由后续执行 owner 接通。

## 9. 2026-09-20 输出字节回传候选

新增 adapter kinds `output_write`、`output_seal`、`output_receipt`，经同一认证 dispatcher 使用 HTTP 或 ADR-033 WebSocket；不向 RenderPlan 填入机器路径。`POST /adapter/v1/outputs/write` 绑定 `claim_id/execution_id/plan_digest/output_root_handle/artifact_set_id/output_kind/byte_length`，另带 `offset/count/sha256/data`。output_kind 仅 `jpg` 或计划启用的 `editable_psd`；总长 1 至 2147483648 字节，每块 1 至 131072 字节，data 为规范 base64url，sha256 为当前块的小写 hex。

必须匹配本 session 仍处于 running、未取消的 execution 和其桌面 reservation。只允许连续偏移；首次用 CreateNew 创建本页私有 `.part`，验证实际打开句柄路径，拒绝重解析路径与既有正式输出。只有协议层最新相同 MAC/sequence 的重放能返回缓存；新 sequence 的重复块不再写入。完整上传后 `POST /adapter/v1/outputs/seal` 携带相同绑定和整文件 hash，桌面 flush 到磁盘后重算，返回 next_offset 和 sealed。错误的整 hash/IO 会毒化本次 reservation，不能继续或重试覆盖。

receipt 只证明字节接收，**不是媒体有效、正式发布或 checkpoint 成功**。正式 owner 带 reservation 的 delivery 在提交协调器接通前拒绝 UXP execution_completed；page_completed 仍为 running。取消/断线释放文件锁但保留任务暂存用于恢复，不删除源或旧正式输出。目标卷 `.app-staging`、媒体/ICC/PSD 校验、manifest/marker/SQLite 原子提交按 ADR-012 继续实现。

UXP 使用文档中的 fs.open/read/close 有界读取自身新输出；每块及 seal 校验 MAC 与 receipt 归属。丢收据或错配后清会话、不重试；正常安全边界取消保留会话供取消事件确认。正式资源 main 的领取/ACK/暂存与准备后取消已有真实证据，见 20260920 S3 验证；本节输出回传与 Photoshop 执行需独立实机证据，不由资源通过代替。

### JPG-only 显式能力增补（2026-09-20）

`output_write/output_seal/output_receipt` 新增 `output_kind=verification_psd`。仅当计划禁止正式 PSD 且声明 `output/verification-psd-v1` 时允许；它与 editable_psd 严格互斥，沿用全部认证、归属、连续偏移、长度/hash、seal 和故障门。JPG-only 也必须有 JPG 与所需验证 PSD 两张 sealed receipt，不能凭 JPG 收据越过结构验证。

提交 manifest.files 只包含 JPG；manifest.media.files 同时包含 JPG/verification_psd。暂存验证 PSD 不进入正式目录，不能由客户端要求更名为 editable_psd。真实传输、独立检查、提交、资源释放和新进程恢复已取得 [限定证据](../验证/RENDER-01-S4-S6替换栈与JPG-only验证-20260920.md)，生产编排和完整故障矩阵仍未关闭。
