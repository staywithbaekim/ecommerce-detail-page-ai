# ADR-032：UXP 回环主机名兼容候选

日期：2026-09-19。状态：**地址替换候选被真实重定向安全门阻断，未实施服务端或正式客户端变更，未批准为生产方案**。

## 问题与实际证据

正式插件 `local.ecommerce.detail-renderer` 0.1.0 在 Photoshop `26.8.1 (20250624.r.8 5e0d05d)`、UXP `uxp-8.3.0-uxp-b1d8c85` 上，对 numeric loopback 的首次 fetch 多次在 HTTP 前失败：`Permission denied to the url [loopback] Manifest entry not found.`。

2026-09-19 通过原生 UDT Debug Console，对已备份的仅 `http://localhost` dist manifest 候选进行无秘密对照：

- `POST http://localhost:17842/adapter/v1/challenges`，body 为 `{}`：收到 HTTP 400。
- 同一加载实例，请求 `http://127.0.0.1:17842/adapter/v1/challenges`：`permission=true`、`manifest=true`，没有 HTTP 状态。
- 尝试在 localhost 请求中设置 numeric `Host`：UXP 报 `Refused to set unsafe header host`，响应仍为 400；此路径不采用。

这些只证明运行时的地址权限差异；400 不证明挑战、配对或认证成功。localhost 候选未进入源 manifest，调查后 dist 已由原 source 重新构建，诊断实例已 Unload。服务端只允许 `127.0.0.1:{localPort}`、拒绝 Origin/Forwarded 的检查始终保留。

## 曾拟议的具体改动（当前不执行）

以下是完成地址对照后形成的候选。后续重定向实验发现它单独不足以满足批准合同，故当前不请求用户仅批准 Host 变更，也不把本 ADR 视为批准：

1. 仍只在 IPv4 `127.0.0.1` 监听原 rendezvous 端口和一次随机执行端口。不得监听 `0.0.0.0`、IPv6、局域网或新增代理。
2. UXP manifest 仅声明 `http://localhost`；正式客户端 URL 仅由固定主机 `localhost`、批准端口和固定路由构成，不接受外部提供的 URL。
3. 服务端精确 Host 从 `127.0.0.1:{localPort}` **替换为** `localhost:{localPort}`。不得同时扩大成主机列表、通配、后缀、忽略端口、跟随转发或接受任意 Host；numeric Host 在此候选中拒绝。
4. 远端实际地址必须仍为 `127.0.0.1`。Origin、Forwarded、X-Forwarded、压缩、非法方法和未知路由继续拒绝。
5. nonce、双向 HMAC、响应 MAC、短期 token、实例/版本/任务绑定、认证序号、随机执行端口、资源权限和未知执行恢复保持不变。
6. 先用无秘密请求核对实际 Host/Origin 与重定向能力，再使用新内存一次性码验证真实配对和至少 3 次认证 ready 心跳。未通过不开放生产可用性守卫。

该候选使用已有 bridge 和 adapter，不引入第二套 renderer 或 helper。不是互联网访问授权，不使用 `network.domains="all"`。

## 仍须解决的安全与兼容门

- localhost 解析必须落到本机；握手前不发送资源、token 或任务内容。须审查解析变化/冒充服务的失败行为，不能把收到 HTTP 当作服务身份通过。
- 本机 `String(fetch)` 观察显示其为 XHR wrapper，未见处理 `redirect` 选项。随后两个受控 IPv4 回环 listener 的实机实验确认：显式 `redirect: "error"` 时，GET 对 301/302/303/307/308 全部跨端口跟随；POST 对 301/302/303 以 GET 跟随，307/308 拒绝。目标端口 GET/POST 直连阳性对照均 200。不能依据首个 POST 307 拒绝结果外推完整禁止重定向。
- 以上实验仅使用 `{}` 和公开测试路由，没有 secret/token/认证头/图片/任务内容；请求落点证据在忽略目录 `tmp/render01-takeover-20260919-194414/redirect-observations.json`。两端 listener 已关闭，localhost 诊断 manifest 已备份并由 source 重建为 numeric，插件实例已 Unload。
- 必须采用运行时实际支持的拒绝重定向能力，或形成新的、保持批准安全性质的传输决策。不能发送真实会话材料后才检查最终 URL 并称已防泄漏；也不能把用户手动批准当作修复运行时行为。
- 响应大小和请求截止仍须覆盖真实运行时。离线模拟器、HTTP 客户端测试和宿主加载均不能代替 UXP 实测。
- 受保护首次分发/轮换、生产编排、实际资源临时副本、Photoshop 执行、页面原子提交和 SIZE 校准合同不由本提案关闭。

## 验证与退出

受影响 listener/client/manifest 测试必须覆盖精确 localhost 成功，numeric/错端口/后缀/外部 Host/Origin/转发拒绝，以及旧 token/错 MAC/重放/版本/端口占用仍失败。实机证据记录完整 build、候选配置 hash、阶段、HTTP 状态和认证心跳计数，不保存秘密/原始日志。

任一安全门无法满足，停止此候选、恢复预先备份的配置，保留证据，RENDER-S1/S2 继续开放。若 localhost 兼容也不可行，再按 ADR-013 的退出条件评估新的传输 ADR；不得自行替换为无认证通信。
