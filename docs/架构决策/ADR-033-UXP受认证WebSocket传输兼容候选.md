# ADR-033：UXP 受认证 WebSocket 传输兼容候选

日期：2026-09-19，2026-09-20 补充实机结果。状态：开发候选的最小真实配对通过，未关闭 RENDER-S1/S2，未开放生产入口。

## 原因与实机依据

ADR-032 的 HTTP localhost 候选被真实重定向行为阻断。UXP 8.3 的 fetch 未实现 `redirect:error`；进一步直接调用 XHR，在 `HEADERS_RECEIVED` 检查 3xx 并 abort，仍已有请求到达跳转目标。不能以响应完成后的 URL 检查代替防泄漏。

同一正式插件 ID 的无秘密 WebSocket 对照中，localhost 的固定/随机端口直连均成功，301/302/303/307/308 均返回 error，目标 listener 没有收到跳转请求。numeric 回环构造仍失败。UXP 的实际 Origin 为精确 `file://`。证据位于忽略目录 `tmp/render01-takeover-20260919-194414/xhr-1789831643307/`；全部请求仅含公开诊断协议，listener 已关闭。

用户提及的历史成功插件已核对：`uxp-validation-plugin` 0.4.7 使用 `network.domains: "all"` 和无认证验证握手，不作为正式最小权限或认证依据。其 Photoshop 执行与导出证据仍供 S4/S5 核对。

## 候选边界

仅替换现有 adapter 的网络载体，不新建 renderer、helper、会话、业务队列或恢复账本。复用 `BridgeLoopbackHost`、`BridgeAdapterSessions`、`BridgeExecutionBroker`、既有 `AdapterClient` 和全部认证转录。

- 服务器仍只绑定 IPv4 `127.0.0.1` 的 17842 与一个随机执行端口，端口占用直接失败，不扫描。
- 插件正式候选只声明 `ws://localhost`。URL 由内部固定端点和验证过的端口构造，不接受调用方任意主机、userinfo、fragment 或外部 URL。
- HTTP 原检查保留。WebSocket Upgrade 使用独立、显式的兼容检查：精确 `Host=localhost:实际端口`、精确单值 `Origin=file://`、固定子协议 `ps-adapter-rpc.v1`；拒绝其他/缺失/重复 Origin、转发、Cookie、Authorization 和压缩。`file://` 只是运行时兼容条件，不是身份凭证；不能仅凭 Origin/plugin ID/版本获得会话。
- 这是 ADR-013 HTTP 载体及 Origin 规则的显式候选演进，不静默改写冻结合同，也不把它描述为零协议变化。只有实际负例、认证及恢复证据闭合后才能替代正式载体。
- 握手前仅发送既有公开挑战字段。新 nonce、双向 HMAC、短期 token、实例/版本/任务绑定、请求/响应 MAC、序号和过期规则不变。秘密、token、认证字段不进入 URL、原生 Upgrade headers、日志或文件。
- 每个连接只进行一次请求/响应，不自动重连或重放。GET 查询语义及其 MAC 元数据仍完整保留；未知执行先查账本，不能因 WebSocket 关闭而重新执行。
- 载体采用严格小型文本头及有界二进制块：头最多 8 KiB，每块最多 128 KiB，总字节数使用原端点限制；UTF-8、声明长度、帧类型、字段集合和 MAC 校验后才使用响应。5 秒截止覆盖连接、读取和写入，出错关闭连接/清会话。

UXP 原生 WebSocket 在 JS 回调之前的内部缓冲不是本应用可配置的限制。候选须验证超大帧在回调处立即拒绝且不进入计划/资源消费；不能声称 JS 已限制宿主底层分配。正式发布仍需支持构建上的资源/截止/断线故障实测。

## 验证门

需覆盖 Host/Origin/转发/原生认证头/压缩/子协议拒绝，头重复键/未知字段、错误帧类型、超大块、总量/UTF-8 错误、超时、MAC/版本/重放失败、请求查询和资源生命周期；复跑受影响 HTTP 回归，避免新增载体改变其拒绝语义。

实机必须以新内存一次性码完成真实配对及至少 3 次认证 ready 心跳。载体通过也不等于生产分发、资源临时副本、Photoshop 执行、JPG/PSD/ICC、checkpoint 或 RENDER 完成。候选失败时保留诊断并恢复备份，禁止扩大到 all 或取消认证。

## 2026-09-22 关闭回执顺序

完整款运行在资源领取前记录了服务端 `response_close` 取消、执行转 Unknown，随后资源绑定按 Unknown 拒绝。见 [完整款联验](../验证/RENDER-01-S7完整款素材联验-20260922.md)。保持现有客户端在完整响应后发起正常关闭并等待确认的行为；服务端改为先读取该正常关闭帧，再回复正常关闭，避免两端同时发起 Close。仍只有一次请求/响应；额外帧、异常关闭、未确认关闭和 5 秒总截止失败均拒绝，不将“发送完字节”视作接收确认，不增加重试、不放宽认证。错误诊断增加固定操作类别、毫秒耗时、请求取消/socket/关闭状态；不记录路径、请求体或认证字段。历史 Unknown 不重提；本次修正是否消除实机故障以新批次证据为准，旧 PSD400 仍没有原始关闭分支证据。

## 2026-09-22 提交查询与领取交错

真实完整款首页已经提交、execution_completed 已持久化，但插件先读到 running，再领取时读到 has_work=false，因交错被错误转为 Unknown。此情况下只为已完成导出并上报 page_completed 的当前页再查询一次既有执行状态；必须核对同一 execution ID、连续 event_seq、唯一 execution_completed 事件和成功状态，才释放临时资源并发送 ready 心跳。空领取本身不代表成功，不接受未知/错身份/缺事件，不重发渲染或输出。确定性竞态与拒绝测试已覆盖；恢复仍由原 manifest/checkpoint 校验链负责，不修改历史事件。该修复不增加网络重放或修改认证合同。

## 首次真实配对结果

2026-09-22 执行中断清理补充：认证会话丢失后，执行失败分支关闭受控 Photoshop 文档时不再依赖失败的网络心跳；重新进入本地 modal 后，仍由原 `current()` 同时确认文档 ID、名称、唯一文档与活动文档身份才允许无保存关闭。该操作不确认服务端执行成功、不发送终态、不重投页面；桌面继续按 Unknown 保留恢复资源。持续断线及四项身份变化拒绝已有回归，新的实机复验见 [A 阶段记录](../验证/V1-A阶段故障与尺码窗口收口-20260922.md)。

2026-09-20 00:05（本地时区），正式 ID/版本 0.1.0 在 PS `26.8.1 (20250624.r.8 5e0d05d)`、UXP `uxp-8.3.0-uxp-b1d8c85` 中完成双向认证及 **4 次认证 ready 心跳**。原 `RenderBridgeProbe` 正常退出 0，`output_executed=false`。source/dist 为单条 `ws://localhost`，没有修改 HTTP numeric 检查、认证规则或业务合同。

实机还暴露浏览器 UTF-8 globals 的隐含依赖；现由局部严格编解码模块替换，已与 Node 原生编码逐个核对全部 Unicode scalar，并拒绝过长编码、代理值、截断和越界字节。不是给宿主安装全局 polyfill。当前完整 Node 54/54。证据及限制见 [WebSocket 实机配对验证](../验证/RENDER-01-WebSocket实机配对验证-20260920.md)。
