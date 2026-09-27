# RENDER-01 JPG-only 7 页性能实测（2026-09-23）

> **历史证据说明（2026-09-27）**：本报告中的旧测时仅保留用于追溯，不代表当前性能结论，也不阻断本轮 V1。当前口径见“RENDER-01 / PERF-01 时间基线收口（2026-09-27）”。


## 目的与边界

按 [V1 交付执行计划](../任务/V1交付执行计划-20260922.md) B 阶段补做一次真实 Photoshop 2025 的 7 页 JPG-only 批次测时。沿用已验收的 `A馨妈6015-58` 输入、模板副本和既有性能事件，不重复 148 项输入测试、既有 148 项测试证据或已有埋点；本轮只建立新的批次、输出根和性能测量边界。

本轮不复用旧正式输出，不修改源照片、源 PSD、模板原件或旧账本。性能口径仍是“从用户生成入口到该款全部 JPG 可用”，并要求区分 AI、页间准备、Photoshop 执行、JPG 回传/校验和原子提交；等待插件连接不计入任何有效性能结果。

## 准备结果

- 隔离根：`tmp/r01-render-perf-20260923-jpg`。
- 批次：`batch_4d30c56d95eb4d04bbaad89e36e55159`。
- 样本：`A馨妈6015-58`，12 张输入，7 页，11 个显式替换框，动态尺码页 1 页。
- 输出策略：`include_editable_psd=false`；7 页 JPG 开启、PSD 关闭；输出根为该隔离根下的 `outputs`。
- 准备阶段：7/7 页面计划通过，模板/源 hash 和尺码资源核验完成；`PAGE_PREPARED candidate=true photoshop_executed=false`。
- 准备阶段没有调用真实 AI，也没有产生正式输出；这是为 JPG-only 实机执行建立的 renderer-neutral 批次，不冒充端到端产品性能。

## Photoshop 实机前置

- 宿主：Photoshop 2025，文件版本 `26.8 (20250624.r.8 5e0d05d)`，产品版本 `26.8`，进程 PID `55988`，路径为 `G:\夸克网盘\Adobe Photoshop 2025\Photoshop.exe`。
- bridge 命令：`RenderBridgeProbe --run-paired-render-batch-performance`，使用既有受保护配对，不保存、轮换或导出凭据。
- 观察到 bridge 进入 `LISTENING` 后一直等待 UXP 插件连接；批次 7 页均保持 `Planned`，没有 execution ID、page checkpoint、JPG、PSD 或临时正式输出。
- Photoshop 进程保持响应，但未进入已认证的 UXP 执行态。原生 CUA 控制面本轮只返回浏览器，无法安全观察或加载插件面板；没有猜测点击、强行接管或把等待时间算入性能。
- 停止等待后重启隔离桌面应用复核：任务仍是 `Rendering`、输出计数为 0、SQLite 中没有 page checkpoint；隔离根没有输出文件。该批次未重投。

## 第二次实机执行（连接成功）

- 新隔离根：`tmp/r01-render-perf-20260923-jpg-3`；新批次：`batch_916c42cd06f7434ba33f8c400f92c759`。没有重投第一次未连接批次，也没有复用其数据库、输出或 execution。
- 正式插件：`local.ecommerce.detail-renderer` / `0.1.0`；Photoshop 2025 `26.8.1 (20250624.r.8 5e0d05d)`；使用已保存的受保护设备配对，插件面板空输入、不勾选记住设备后连接。
- Bridge 认证稳定：探针 `exit=0`，`status=Completed`，`output_count=7`，`authenticated_heartbeats=3806`，`transport_fault=null`，`binding_fault=null`；7 页均为 `Committed`。
- 性能报告 `render-performance.json`：`batch.run=747.495s`，其中 `page.wait_ready=379.577s` 为用户完成插件连接的等待，按口径排除；排除后 `batch.run=367.918s`，7 个 `page.run` 合计排除等待为 `367.564s`。页间准备、Photoshop 执行、媒体验证、提交均已包含在该有效批次时间内。
- 阶段汇总（嵌套 span 不重复相加）：`resources.prepare=64.500s`、`page.submit=65.228s`、`output.validate=99.421s`、`output.wait_ready_ack=16.285s`、`output.commit=0.612s`、`output.release_resources=0.276s`。
- 输出校验：7/7 formal manifest 每页只发布 1 个 JPG；7 个 JPG 的实际长度/hash 与 manifest 一致；ICC 均为 3144 bytes，命中既有两种受控 sRGB ICC 摘要；formal output 根 `jpg_count=7`、`psd_count=0`、`committed.marker=7`。JPG-only 的 `verification_psd` 只作为临时结构验证媒介，不进入正式文件列表。

> 历史证据边界：以上批次发生在 2026-09-23 P0 热路径实施之前；其临时 `verification_psd` 和对应阶段耗时准确记录了旧实现，不是 P0 后行为或性能证据。P0 后是否仍有 `resources.inspect_template`、`media.inspect_baseline`、`media.inspect_output` 和 verification PSD，须由新的隔离 PS2025 批次确认；本报告的输出与计时数据保持原样，不回填或推测新结果。

## 结论

第一次尝试仅完成准备，因插件未连接而无效；第二次取得了真实 Photoshop 2025 7 页 JPG-only 执行、回传、校验和原子提交证据，但未达到完整性能门。因此：

1. 有效 7 页批次的连接等待不计入性能数值；排除等待后的 `367.918s` 仍高于 300 秒参考线，不能把单款结果外推为 10 款约 70 JPG 已通过。
2. `PERF-01`、RENDER-S6/S7 和 V1 的 10 款/约 70 JPG/300 秒门继续开放；本轮关闭“真实 UXP 插件未进入已认证连接态”这一具体阻断。
3. 后续优化应以阶段 span 为依据，优先定位页间准备、Photoshop 执行和媒体验证成本；不得重复 148 项输入测试、既有埋点或第一次未连接批次。

## 证据边界

两次准备材料和运行数据库保留在 `tmp/r01-render-perf-20260923-jpg`、`tmp/r01-render-perf-20260923-jpg-3`，仅作本地可重建证据，不进入正式输出或 Git。没有新增代码、没有新增埋点、没有重复测试套件，也没有修改用户媒体。
