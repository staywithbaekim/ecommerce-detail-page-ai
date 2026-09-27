# RENDER-01 WebSocket 实机配对验证

日期：2026-09-20（本地时区）。同一工作树 `codex/desktop-foundation-01` / `fbdf555`，保留既有未提交改动。按 [ADR-033](../架构决策/ADR-033-UXP受认证WebSocket传输兼容候选.md) 实施载体兼容候选。

## 结论

**此前 manifest/首次通信阻断已有可运行的最小修复；正式插件真实配对及 4 次认证 ready 心跳通过。RENDER-S1/S2 整体与 RENDER 阶段仍未完成。**

- 正式插件 `local.ecommerce.detail-renderer` 0.1.0；PS `26.8.1 (20250624.r.8 5e0d05d)`，UXP `uxp-8.3.0-uxp-b1d8c85`。
- 原有 `RenderBridgeProbe` 复用 `BridgeSessionOwner`，新 32 字节一次性码仅在内存、stdin 与插件密码输入中传递；未写入参数、文件或日志。成功后探针正常退出 0。
- `probe-result.json` 记录 `paired=true`、`authenticated_heartbeats=4`、`connected=true`、`host_ready=true`、`output_executed=false`，时间 `2026-09-19T16:05:21.5279749+00:00`。
- 忽略目录证据：实际运行目录为 `tmp/render01-takeover-20260919-194414/ws-utf8-pair-1789833784401/`；同根 `xhr-1789831643307/successful-pair-evidence.json` 保存脱敏结果与 source/dist bundle hash。没有把截图或加载成功替代认证结果。
- 没有打开/修改 Photoshop 文档、源媒体或正式输出；没有使用浏览器、子代理、提交、推送、安装、重装、清缓存或清理任务数据。

## 根因与修复范围

numeric loopback 的 manifest 拒绝在当前运行时仍存在。localhost HTTP 通过权限层，但 fetch 忽略 `redirect:error`；XHR 在 readyState=2 检查 3xx 并 abort 仍有跨端口目标请求。该路线停止，没有扩大为 all。

无秘密 WebSocket 对照：localhost 固定/随机端口直连成功，301/302/303/307/308 都失败且目标无跳转请求。numeric WebSocket 构造失败。真实 Upgrade 的 Origin 是精确 `file://`，无 Cookie/Authorization；二进制 ArrayBuffer 发送、binaryType 和子协议有效。

既有 listener 新增有界 WebSocket carrier，同一 `Exchange` 继续使用原认证、会话、broker、查询、资源租约和恢复账本。HTTP numeric/无 Origin 检查保持，WebSocket 兼容条件显式分支并拒绝错 Host/Origin/子协议、转发、Cookie、原生 Authorization 与压缩。客户端仅构造已知回环端点，不重连、不重试，头 8 KiB、块 128 KiB、原端点总限额、5 秒截止、严格 UTF-8 与响应 MAC 校验。

第一次新载体实机配对在客户端编码前安全失败。原生 Console 确认 TextDecoder global 缺失；源码也依赖浏览器 TextEncoder。现所有 adapter 字符串编码走局部 `utf8.js`，无新依赖和运行时安装；再次使用新码后完成上述成功结果。先前 `pairing-probe.json` 只在失败时更新，其旧错误不能作为成功运行的状态，结果以有时间的探针 evidence 为准。

用户指出的历史成功插件已在本地提交 `82d3135` 的 `uxp-validation-plugin` 0.4.7 中找到；manifest 使用 `network.domains: all` / fullAccess，PowerShell 验证握手没有当前认证合同。未运行或复制该旧握手；其 Photoshop 换图/导出/ICC 的历史依据可继续用于 S4/S5。

## 当前验证

| 检查 | 实际结果 |
| --- | --- |
| solution build / 探针 build | 最终均 0 警告、0 错误 |
| Infrastructure Bridge | 修复后一次 156/157 通过，唯一失败在测试启动前 `BRIDGE_PORT_UNAVAILABLE`；该 Forwarded 用例与相关反例聚焦重跑 2/2 通过。全部 157 个用例已有通过记录，但不描述为单轮 157/157 |
| Architecture | 26/26 |
| 插件 build / 全 Node | 最终 54/54；包含载体 18 项及 Unicode 编解码 2 项 |
| 真实 UXP | 双向配对 + 4 ready 心跳；探针 exit 0 |
| git diff --check | 载体第一轮通过；后续文档/代码变化仍须最终复核 |

初轮测试发现 ASP0019（已改字典 indexer）、错误路由被 socket 直接关闭而非返回统一拒绝（已修复）、ClientWebSocket 会丢弃 Content-Encoding 测试头（改真实 TCP upgrade 反例）。测试使用 numeric 直连并设置精确 Host，避免 .NET localhost IPv6 连接等待，不改变 UXP 实机路径。

本机 Windows 排除端口中存在 49731–49930、50000–50159、57191–57290、58478–58577 等高位区间。随机执行端口命中占用/保留时仍安全失败，不扫描或自动改连；失败测试没有实际捕获选中端口，故不能断言具体一次失败必然由该区间造成。

## 未关闭的门

受保护配对首次分发/轮换、生产 session owner/availability 编排、UXP main 领取/ACK/实际临时资源、普通/动态页 Photoshop 执行、输出传输与校验、原子提交/checkpoint/取消恢复、真实 JPG/PSD/ICC/源保护故障矩阵仍待实现/验收。动态 `targets=[]` 和放置语义缺口继续按 ADR-014 显式处理。

UXP JS 只能限制回调可见帧和应用总量，不能声称控制宿主内部收帧分配；超大帧/截止/断线还需支持构建上的实测。历史 TV 验证和此次心跳不替代这些门。
