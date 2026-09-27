# Photoshop 回环 bridge `ps-bridge/v1` 契约

## 1. 目的与边界

本文是桌面应用与 Photoshop UXP 执行适配器之间的 V1 通信合同。机器可读信封位于 `schemas/bridge-v1.schema.json`，渲染语义位于 `schemas/render-plan-v1.schema.json`。

bridge 只负责发现、认证、资源绑定、提交已校验的 RenderPlan、执行状态查询、心跳、取消和释放。它不负责 AI 选图、变体分组、绿黄红判断、尺码识别、模板规则或业务状态机；也不接受 Photoshop 命令字符串。

## 2. 网络与会话

- 仅绑定 `127.0.0.1`，拒绝远程地址、IPv6/局域网监听和未知本机服务转发。
- rendezvous 端口固定为 `17842`，只提供版本发现和握手，不接收 RenderPlan。
- 握手成功后服务端分配 `49152-65535` 的随机执行端口。执行端口被占用时安全失败，不扫描其他端口。
- 每个安装建立随机 `pairing_secret`。桌面端使用 Windows Credential Manager；UXP 使用 Photoshop 受保护存储。普通文件、manifest、模板包、日志和诊断包不得保存秘密。
- nonce 是 32 字节随机值，以 base64url 表示为 43 个字符且只能使用一次。UXP 计算：

  `HMAC-SHA-256(pairing_secret, nonce || session_id || plugin_manifest_version || photoshop_build)`

- 成功握手返回 `session_id`、执行端口、短期 token、到期时间和能力集合。token 绑定单任务、单 Photoshop 实例、plugin manifest 和 protocol 版本。
- token 默认 15 分钟无活动失效；有效心跳可续期，绝对最长 8 小时。应用重启、Photoshop/插件实例变化、版本不匹配、释放或超时会立即失效。
- 正式 UXP 受保护存储不可用时，唯一回退是 F-01 已批准的一次性人工配对码：短时有效、使用后失效、不写普通文件/日志。不能回退为无认证固定端口。

## 3. 通用信封

所有非握手请求使用以下信封；`body.payload` 按 `operation` 取对应结构：

```json
{
  "protocol_version": "ps-bridge/v1",
  "envelope_kind": "request",
  "body": {
    "request_id": "req_01JABCDEF",
    "correlation_id": "corr_01JABCDEF",
    "session_id": "sess_01JABCDEF",
    "token": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
    "sent_at": "2026-08-27T12:00:00Z",
    "idempotency_key": "release-session-01",
    "operation": "release",
    "payload": {}
  }
}
```

响应使用：

```json
{
  "protocol_version": "ps-bridge/v1",
  "envelope_kind": "response",
  "body": {
    "ok": true,
    "request_id": "req_01JABCDEF",
    "correlation_id": "corr_01JABCDEF",
    "status": "accepted",
    "server_time": "2026-08-27T12:00:01Z",
    "execution_id": "exec_01JABCDEF",
    "execution_status": "accepted",
    "event_seq": 1
  }
}
```

失败响应必须带 `error`，结构固定为 `code`、`retryable`、`scope`、`user_action` 和脱敏 `details`：

```json
{
  "protocol_version": "ps-bridge/v1",
  "envelope_kind": "response",
  "body": {
    "ok": false,
    "request_id": "req_01JABCDEF",
    "correlation_id": "corr_01JABCDEF",
    "status": "rejected",
    "server_time": "2026-08-27T12:00:01Z",
    "error": {
      "code": "BRIDGE_PROTOCOL_MISMATCH",
      "retryable": false,
      "scope": "session",
      "user_action": "升级配套组件。",
      "details": { "expected": "ps-bridge/v1", "actual": "ps-bridge/v0" }
    }
  }
}
```

## 4. 端点合同

### `POST /handshake`

请求为 `handshake_request`，包含客户端版本、UXP manifest 版本、完整 Photoshop 构建、一次性 nonce、session ID 和 pairing proof。请求不得带 session token。服务端验证 loopback、版本矩阵、nonce 新鲜度和 HMAC 后返回 `paired` 或 `rejected`。

### `POST /plans`

`operation=submit_plan`，只接受单页或单款的已校验 RenderPlan：

```json
{
  "execution_scope": "page",
  "render_plan": { "schema_version": "render-plan/v1" },
  "plan_digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "resource_bindings": [
    {
      "resource_id": "res_template_01",
      "session_handle": "opaque-template-handle",
      "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "purpose": "template_copy",
      "read_only": true
    }
  ],
  "output_bindings": [
    {
      "artifact_set_id": "artifact_page_01",
      "output_root_handle": "opaque-output-handle",
      "overwrite_policy": "never",
      "confirmed_target_ids": []
    }
  ]
}
```

`render_plan` 必须通过 RenderPlan Schema、资源引用闭合、hash/用途、目标结构、输出文件名和 `plan_digest` 重算。资源绑定只使用不透明 handle；真实路径、token 和临时目录不得出现在 RenderPlan、领域对象、日志或 evidence 中。

上例中的 `render_plan` 仅展示信封位置，省略了完整字段；契约测试和实际提交必须嵌入 `schemas/render-plan-v1.valid.example.json` 或同等完整的 `render-plan/v1` 正例，不能把该占位对象直接发送给 bridge。

服务端立即返回 `accepted` 和 `execution_id`，不在 HTTP 请求中持续等待 Photoshop 渲染。

### `GET /executions/{execution_id}`

返回当前状态、最新 `event_seq` 和可选事件。客户端可用 `?after_seq=N` 断线补拉事件。若执行状态未知，只允许查询或人工确认，不自动重新提交。

### `POST /executions/{execution_id}/cancel`

`operation=cancel_execution`，请求带取消原因。Photoshop modal 内不能强制中断，服务端返回 `cancel_pending`；在安全边界生效后发出 `execution_cancelled`。重复取消安全且不生成第二次副作用。

### `POST /heartbeat`

`operation=heartbeat`，每 5 秒发送一次，15 秒无心跳进入 `BridgeDisconnected`。payload 报告 `host_state`、`modal_state` 和 `last_event_seq`。心跳不能代替执行状态查询，也不能在版本/实例变化后续用旧 token。

### `POST /release`

释放 session、执行锁和资源 handle；服务端先使 token 失效，再清理可重建临时资源。重复释放返回成功或已释放，不重新建立会话。

## 5. 幂等、事件与超时

| 操作 | 客户端超时 | 幂等边界 | 失败后动作 |
| --- | ---: | --- | --- |
| 握手 | 5 秒 | nonce 一次性 | 重新创建 nonce，不重放同一 proof |
| 提交计划 | 10 秒接收 | `idempotency_key + plan_digest` | 查询已有 execution |
| 状态查询 | 5 秒 | 只读 | 退避查询 |
| 取消 | 5 秒 | execution 级 | 查询安全边界结果 |
| 心跳 | 5 秒 | `event_id` 去重 | 标记断线并进入恢复检查 |
| 释放 | 5 秒 | session 级 | 本地清理并记录 |

请求体限制：普通请求/响应最大 1 MiB，`/plans` 最大 8 MiB，心跳和状态查询最大 256 KiB。超限返回 `BRIDGE_REQUEST_TOO_LARGE`，不得截断或尝试解析部分 JSON。

事件 `event_seq` 在单 execution 内严格递增，`event_id` 全局唯一。允许的终态为 `completed`、`failed`、`cancelled` 或 `unknown`；重复事件由客户端 reducer 去重。

## 6. 错误码

| 错误码 | 可重试 | 范围 | 说明 |
| --- | --- | --- | --- |
| `BRIDGE_PROTOCOL_MISMATCH` | 否 | session | 协议版本不兼容 |
| `BRIDGE_PAIRING_REJECTED` | 否 | session | nonce/HMAC/受保护存储校验失败 |
| `BRIDGE_TOKEN_INVALID` | 否 | session | token 缺失、过期或绑定不符 |
| `BRIDGE_PORT_UNAVAILABLE` | 否 | host | rendezvous/执行端口被占用 |
| `BRIDGE_REQUEST_TOO_LARGE` | 否 | request | 超过请求体上限 |
| `BRIDGE_PLAN_INVALID` | 否 | request | RenderPlan Schema 或摘要不通过 |
| `BRIDGE_RESOURCE_BINDING_INVALID` | 否 | request | handle、hash、用途或只读属性不符 |
| `BRIDGE_CANCEL_PENDING` | 否 | execution | 资源读取或输出 write/seal 的绑定仍匹配，但桌面已持久化取消并撤销租约；不传输或封存字节，只可安全释放并回报取消，不是终态或可重试凭据（ADR-013） |
| `BRIDGE_EXECUTION_NOT_FOUND` | 否 | execution | execution ID 不存在或已过期 |
| `BRIDGE_DISCONNECTED` | 是 | session | 心跳丢失或 loopback 断开 |
| `BRIDGE_HOST_BLOCKED` | 否 | host | 未保存 Photoshop、未知弹窗或能力探针失败 |
| `BRIDGE_EXECUTION_UNKNOWN` | 否 | execution | 执行结果无法确认，必须查询/人工处理 |
| `BRIDGE_BUSY` | 是 | host | 已有 Photoshop 执行通道占用 |

## 7. 安全禁止项

- 不监听 `0.0.0.0`，不接受来自网络接口的请求。
- 不用 plugin ID、版本号或固定端口作为身份验证。
- 不把 API Key、认证头、绝对路径、PSD/source image 字节或原始 Photoshop 异常写入协议/日志。
- 不接受任意 Photoshop `batchPlay`/JSX 命令、任意本地路径或 `analysis_copy` 作为正式渲染资源。
- 不因 HTTP 超时直接重提计划；先查询 execution 状态。
- 不模拟点击未知 Photoshop 弹窗；由用户处理后重新探针。

## 8. 契约通过标准

协议冻结只表示本文和 JSON Schema 已成为开发输入，不表示运行环境已通过。实现进入 `RENDER-01` 前必须提供：正反请求/响应样例、重复提交不重复替换、token 过期/轮换、端口占用、断线查询、未知执行、请求超限、未知弹窗和实际 PS2025 `26.8.1` 客户机证据。任一失败都保留结构化错误并阻断生产发布。

## 9. 2026-09-19 实现澄清

- 根 `body` 的联合约束由 `oneOf` 修为 `anyOf`：握手拒绝体和普通拒绝体有合法重叠；仍以 `envelope_kind` 的条件分支选择精确 Schema，未知字段规则不变。正反合同测试已证明修正，没有新增端点、版本或业务字段。
- 实现额外执行 endpoint/operation、错误码及本地安全文案、事件执行身份/序号、RenderPlan 摘要和资源绑定闭合校验。错误的本地输出以 `BridgeWireErrors` 为准，不透传远端原始错误文案。字节流读取有界，但真实 HTTP/认证/执行端尚未接通。
- 本合同没有完整定义桌面监听拓扑中的挑战下发、UXP 领取/ACK、认证事件回传以及 GET 认证 header。候选设计见 [UXP 主动连接传输增补草案](RENDER-S1-UXP主动连接传输增补草案.md)，尚未冻结或装入 v1 Schema。正式实现前须补齐机器合同、双方认证、去重/未知结果语义和测试。
- 此次离线证据见 [RENDER-S1 验证记录](../验证/RENDER-01-S1合同校验与提交边界验证-20260919.md)；不关闭 S1 整体、S2 实机或 S4–S7 输出门。
