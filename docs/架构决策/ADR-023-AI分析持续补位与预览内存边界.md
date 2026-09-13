# ADR-023：AI 分析持续补位与预览内存边界

**日期：** 2026-09-09
**状态：** 已批准实施
**范围：** AI-01 分析编排、WPF 分析结果预览；不改变 provider 合同、模板规则或 Photoshop 执行适配器

## 背景

本机使用真实目标款复测时发现两个实现问题：

1. 结果页把源照片路径直接绑定给 WPF `Image.Source`。WPF 会按原尺寸解码高分辨率照片；单张约 `5700×3800` 的 RGBA 工作集约 82.6 MiB，多张同时显示会形成 GB 级峰值。
2. AI 工作流在 `StartAsync` 中等待全部款式完成副本准备和 admission，之后才启动消费者。这样会把“准备、提交、收集”变成阶段性串行，第一款尚未准备完时不会先提交，不能满足“准备好一款就开始、完成一款立即补位”。

## 决策

### 1. durable backlog 与有限执行窗口分离

- 100 款是持久化 backlog 的规模示例，不是 100 路 HTTP 并发。
- 工作流在 admission 开始时同时启动消费者；消费者在 admission 尚未结束但 ready queue 暂时为空时等待，第一款进入 ready queue 即可执行。
- admission 完成一款后继续准备下一款；provider worker 完成后由 scheduler 从 durable pending 补位。
- 进程内 ready buffer 仍最多 32，真实 HTTP 同时最多 8；暂停、取消、预算硬停止、checkpoint、lease 和 `AiResultUnknown` 边界不绕过。
- producer 异常必须落为可行动的失败/恢复状态，不能让页面无限显示“分析中”。

### 2. 副本准备并发有界

- WIC 分析副本准备默认最多同时 2 个；只能收紧，不能通过依赖注入扩大。
- 同一 `analysisCopyReference` 的读取、写入和 lease 操作仍串行；不同引用不再被一个全局锁互相阻塞。
- 编码字节只在单个副本生命周期内存在；队列和 durable snapshot 只保存元数据、相对引用、digest 和 lease，不保存原图字节。

### 3. WPF 预览使用有界缩略图

- 所有 AI 结果/候选/批次照片缩略图使用本地有界解码，默认最长边约 320 像素；大预览可使用更高但仍有上限的明确参数。
- `BitmapCacheOption.OnLoad` 配合 `DecodePixelWidth`，冻结后的 `BitmapImage` 进入最多 96 项的进程内 LRU 缓存；缓存按路径、文件长度和修改时间区分，不能无限持有 100 款原图。
- 该边界只影响 UI 预览，不改变上传用 WIC profile、AI 输入字节、源文件 hash 或正式输出。
- 用户打开单张大图的专用查看路径仍可另行使用受限大预览，但不得把原图绑定到批量列表。

### 4. 单请求素材门保持不变

- 每个逻辑请求最多 24 张素材；超过 24 张在副本准备/HTTP 之前明确标记失败，不发 provider 请求，不显示无限“分析中”。
- 100 款的 admission 可以持续登记到 durable pending；不能一次性加载全部原图，也不能无界创建 HTTP 任务。

## 不在本 ADR 中

- 不提高单请求 24 张、HTTP 并发 8、ready buffer 32 或预算硬门。
- 不修改 PSD、源照片、模板规则、RenderPlan/UXP 合同。
- 不自动删除用户认为“视觉相似”的照片；只有明确字节重复且得到单独授权时才可清理。

## 退出与验证门

必须有可复现证据证明：

1. 25 张输入在 provider 前被明确阻断，provider 调用数为 0；24 张仍可进入正常调度。
2. 两款准备耗时不同的测试中，第一款准备完成后即可先进入 provider，第二款仍在准备不阻塞第一款。
3. 约 100 款进入 durable backlog 后，ready buffer 不超过 32、HTTP 不超过 8，完成一款持续补位，重启只恢复允许恢复的状态。
4. 结果列表和候选列表使用缩略图解码，内存不会随总款数线性持有原始 RGBA 图像。
5. 暂停、取消、恢复、预算硬停止、未知结果和 lease 清理回归不被破坏。

## 2026-09-09 实施证据

代码和自动化子门已复核：Application（排除缺失的两个外部 SIZE-01 金标）188/188、Infrastructure 244 通过/4 个真实环境探针跳过、WPF 27/27，`Desktop.App` build 0 警告/0 错误，`git diff --check` 通过。新增回归覆盖 25 张 provider=0、24 张放行、两款交错准备时第一款先进入 provider、100 个请求的 ready≤32、最多 2 路副本准备，以及 320px 有界预览。

以上只关闭本 ADR 的实现/自动化子门；暂停、取消、恢复、重启的真实新包走查、10 款正式 `machine_total` 和完整 `PERF-01` 仍未通过，不得用本地回归替代。当前目标款源目录有 25 个文件，用户删改后必须重新扫描并建立新批次，不能复用旧 manifest/hash。

## 2026-09-09 真实请求体复核补充（已修正）

真实批次 `batch-42ee2db23d2144549aa82bbb031bf581` 进一步证明：本 ADR 中的 16 MiB 边界必须按“最终序列化 JSON HTTP 请求体”解释，而不是只按素材数量或源文件大小解释。该批次有 24 张素材、源文件合计 `115,669,269` 字节；旧 profile 对 PNG 只缩放、不转 JPEG，Base64 与 JSON 构造后的联合请求离线估算约 `51–53 MiB`，因此在 provider 前以 `AI_PROVIDER_REQUEST_TOO_LARGE` 安全拒绝。

这不是 100 款 backlog 同时持有全部图片的问题，也不改变 100 款只进入 durable backlog、WIC 最多 2 路准备、ready 最多 32、HTTP 最多 8 路的决策。修正已在应用自有副本层落为版本化 `wic-bounded/max1600/jpeg-q82/png-opaque-jpeg/v2`：最长边 1600、JPEG q82、源 64 MiB、副本 8 MiB、100M 像素；无实际透明度的 PNG 转为 JPEG，含非不透明像素的 PNG 保留为 PNG。adapter 先构造最终 `JsonObject`，以 `Encoding.UTF8.GetBytes(root.ToJsonString(...))` 的实际字节数执行 16 MiB 预检，超限不调用 `HttpClient.SendAsync`，并以 `AI_PROVIDER_REQUEST_TOO_LARGE` 持久化和投影明确处理动作。

离线真实 24 图回归（全新临时 runtime root、fake HTTP handler）得到副本总字节 `9,224,434`、最终 JSON `13,113,521` bytes、24 张均为 JPEG、provider/HTTP 调用 1 次；源文件长度和 SHA-256 未改变。`Approved24ImageRequestUsesFinalUtf8JsonByteCountAndReachesProvider`、`OversizedFinalSerializedJsonIsRejectedBeforeHttpWithExplicitAction`、SQLite 终态错误恢复和 WPF 明确投影回归均通过。完整真实 10 款 `machine_total`、Photoshop/文件提交和 `PERF-01` 仍保持未通过。
