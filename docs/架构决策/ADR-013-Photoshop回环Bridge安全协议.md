# ADR-013：Photoshop 回环 bridge 安全协议

## 状态

已批准并冻结 `ps-bridge/v1` 协议边界；UXP 受保护存储、正式 `.ccx` 权限、端口占用和逐机行为仍由 `INSTALL-01`、`RENDER-01` 与 Photoshop 实机验证。

## 日期

2026-08-27

## 背景

桌面端和 UXP 需要在同一台电脑上交换 RenderPlan、进度和恢复查询。仅依赖固定端口、plugin ID 或版本字符串会允许本机其他进程伪造服务，也无法安全处理断线和重复提交。

## 决策

1. bridge 使用仅监听 `127.0.0.1` 的 HTTP；不接受远程网卡、局域网或公网连接。
2. 使用两阶段端口：固定 rendezvous 端口只做发现/握手，延续已验证的 `17842`；握手成功后返回随机高位执行端口，RenderPlan、执行查询、心跳和释放只走该端口。端口占用安全失败，不扫描或连接未知本机服务。
3. 每个安装建立随机 `pairing_secret`：桌面端存 Windows Credential Manager，UXP 端存 Photoshop 受保护存储。共享秘密不得进入代码、manifest、普通文件、模板包、日志或诊断包。
4. 握手挑战使用一次性 32 字节 nonce。UXP 计算 `HMAC-SHA-256(pairing_secret, nonce || session_id || plugin_manifest_version || photoshop_build)`；验证通过后才签发执行会话 token。
5. session token 绑定单任务、单 Photoshop 实例和单 manifest/protocol 版本；默认 15 分钟无活动失效，心跳只可在仍满足绑定条件时续期，绝对最长 8 小时。释放、超时、应用重启、Photoshop/插件实例变化或版本不匹配立即失效。
6. 所有非握手请求携带 `protocol_version、request_id、correlation_id、session_id、token、sent_at、idempotency_key、payload`。普通请求/响应最大 1 MiB，`/plans` 最大 8 MiB，心跳和状态查询最大 256 KiB。超限直接拒绝，不截断解析。
7. `/plans` 只接收已经通过 RenderPlan Schema 的计划和 adapter 私有资源/输出绑定。绑定使用不透明 session handle、用途、内容 hash 和只读属性，不把真实路径或 token 回写 RenderPlan。
8. 事件使用严格递增 `event_seq` 和唯一 `event_id`；重复请求返回同一执行结果，不能因重试再次替换或导出。未知执行结果先查询 `/executions/{id}`，不得盲目重提。
9. 未知 Photoshop 弹窗由桌面端暂停并要求用户处理；bridge 不模拟点击，也不把“连接正常”当作“可生产”。
10. 如果正式 UXP 受保护存储无法可靠建立，唯一回退是 F-01 已批准的一次性、短时有效、使用后失效的人工配对码；不得发布无认证握手。

## 端点边界

| 方法/路径 | 语义 | 超时 | 幂等 |
| --- | --- | ---: | --- |
| `POST /handshake` | nonce 挑战、能力和版本协商 | 5 秒客户端 / 10 秒服务端 | nonce 一次性，失败不重放 |
| `POST /plans` | 提交单页或单款计划 | 10 秒接收；执行异步返回 | `idempotency_key + plan_digest` |
| `GET /executions/{id}` | 断线查询执行状态和事件 | 5 秒 | 只读 |
| `POST /executions/{id}/cancel` | 请求安全边界取消 | 5 秒 | execution 级重复安全 |
| `POST /heartbeat` | 报告宿主、modal 和事件状态 | 5 秒 | event 去重 |
| `POST /release` | 释放会话和锁 | 5 秒 | 重复调用安全 |

## 统一错误结构

```json
{
  "code": "BRIDGE_PROTOCOL_MISMATCH",
  "retryable": false,
  "scope": "session",
  "user_action": "升级配套组件",
  "details": { "expected": "ps-bridge/v1", "actual": "ps-bridge/v0" }
}
```

`details` 只允许脱敏的 allowlist 字段；不得包含 API Key、认证头、绝对路径、图像字节或 Photoshop 原始异常对象。

## 候选与取舍

| 方案 | 优点 | 风险 | 结论 |
| --- | --- | --- | --- |
| 回环 HTTP + rendezvous/随机执行端口 + HMAC | 延续现有握手证据，状态可查询，跨语言简单 | 必须严格实现 token、nonce 和本机伪造防护 | **采用** |
| 固定单端口 HTTP | 实现简单 | 易被本机进程抢占或伪造，长任务隔离弱 | 不采用 |
| Named Pipe helper | Windows ACL 可细化 | 增加 helper 制品、安装和 UXP 连接边界 | 仅在正式 UXP 权限阻断 HTTP 时重新评估 |

## 影响

- UXP 适配器只执行已校验的计划，不读取 AI 结果、不决定页面状态、不生成业务规则。
- bridge 断线会进入 `BridgeDisconnected`/`RecoveryRequired`，不会自动重复有费用或有副作用的请求。
- 协议版本、插件版本和 Photoshop 构建必须进入握手与诊断，但版本号本身不是认证凭据。

## 退出条件

若 UXP 正式权限无法实现受保护配对、回环监听或状态查询，暂停客户交付并形成新 ADR；不得退回无 token 的固定端口通信。
