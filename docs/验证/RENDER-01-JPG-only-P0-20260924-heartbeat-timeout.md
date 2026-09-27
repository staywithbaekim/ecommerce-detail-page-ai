# RENDER-01 P0 JPG-only 实机批次心跳超时记录（2026-09-24）

## 结论

P0 真实 Photoshop 复测**未通过**，本次没有形成可验收的正式输出，也没有进入 P1/P2。

批次在第一页 `page_front` 已收到 `execution_accepted` 和 `page_started` 后，生产 bridge 发生 `heartbeat_timeout`。桌面端按未知结果安全收口为 `RecoveryRequired`，没有自动重投未知 execution，也没有把未完成页标记为成功。

## 第二次复测：连接稳定但裁剪旧层页失败

为避免复用未知 execution，使用全新隔离根 `tmp/r01-render-perf-20260924-p0-r5` 重建 7 页计划并重新加载 UXP 插件后执行。批次 `batch_2c7e625180f04c6bbf2bead743c60cb2` 共认证心跳94次，没有再次发生 `heartbeat_timeout`，但最终为 `CompletedWithIssues`：

- `page_front`、`page_back`、`page_size`：3/7 `Committed`；
- `page_fabric`、`page_details`、`page_display1`、`page_display2`：4/7 `Failed`，统一为 `BRIDGE_PLAN_INVALID`；
- 正式输出：3个JPG、0个正式PSD、0个verification PSD；
- 剩余资源副本：0；剩余输出 `.part`：0。

### 根因

UXP `preflightKnownTargets` 在检查 superseded layer 时，把 `descriptor.group === true` 直接作为失败条件；但该字段同时是当前链路用于表达裁剪关系的证据。于是含裁剪旧智能对象的页面在比较 `expected_clipping=true` 之前就被拒绝。非裁剪旧层的 `page_front`、无旧层的 `page_back` 和无替换框的 `page_size` 因此可以通过，失败分布与该条件一致。

已修复 `uxp-render-adapter/src/photoshop.js`：保留 smart object、linked、可见性、身份和裁剪关系校验，移除对 `descriptor.group=true` 的无条件拒绝；新增裁剪旧智能对象回归测试。UXP Node 测试现为236/236通过，UXP build成功，输出更新到 `uxp-render-adapter/dist`。

本次 r5 仍不构成 P0 通过，不进入 P1/P2。下一步须手动从修复后的 `uxp-render-adapter/dist/manifest.json` 重新加载插件，再建立全新隔离根复测；不复用 r5 或任何已完成/失败 execution。

## 第三次复测：P0 真实门通过

使用修复后重新加载的 UXP 插件，在全新隔离根 `tmp/r01-render-perf-20260924-p0-r6` 执行批次 `batch_88209d1eef93436d9f7b5f9300e19632`。结果：

- `status=Completed`，7/7 页面 `Committed`；
- `authenticated_heartbeats=162`，无 `transport_fault`、`binding_fault` 或 Photoshop host interruption；
- 正式输出7个JPG、0个正式PSD、0个verification PSD；
- 资源副本和输出 `.part` 均为0；
- 独立核验通过每页 manifest/marker、JPG byte length/SHA-256、尺寸、sRGB ICC及输入资产 hash；
- `render-performance.json`：批次193.732秒，其中首次连接等待56.517秒，排除等待后的有效批次约137.215秒。

这关闭 P0 7 页 JPG-only 真实门，并允许按任务书进入 P1。该结果不是10款约70 JPG的 `PERF-01` 总门证据；P1先以本批次性能报告作为基线，再评估减少 UXP 往返和同批次模板副本复用，P2只在新的测量证据支持时实施。

## 隔离根与执行身份

- 隔离根：`tmp/r01-render-perf-20260924-p0-r3`
- 批次：`batch_0b7e59a029934e088306c0e0eeccdbc2`
- 执行：`execution_cc0cc706ba494ab1aef8649a34e48d21`
- 生成命令：`RenderBridgeProbe --run-paired-render-batch-performance`
- Photoshop：2025，插件为当前 `uxp-render-adapter/dist` 正式构建
- 计划：7 页、11 个替换框、`include_editable_psd=false`

## 现场证据

`batch-execution-984d7bf439204690b3426f29cf1363c9.json`：

- `status=RecoveryRequired`
- `authenticated_heartbeats=11`
- `transport_fault.Stage=heartbeat_timeout`
- `dispatched=false`
- `output_count=0`
- `page_front=Unknown / RENDER_RECOVERY_REQUIRED`
- 其余 6 页仍为 `Planned`

`render-performance.json`：

- `page.wait_ready=265374.677ms`
- `page.run(page_front)=292083.9897ms`
- `batch.run=292353.7028ms`
- 没有后续页面的 Photoshop/输出验证 span

`render/tasks/.../bridge-executions-v1.json` 只记录第一页：

1. `execution_accepted`
2. `page_started`

没有 `page_completed`、`execution_completed` 或正式输出提交事件。

## 保护核对

- 正式 JPG/PSD：0
- 输出 `.part`：0
- 暂存资源副本：2（第一页受控模板副本和商品 JPG，保留作未知 execution 的恢复证据）
- 输入、模板和旧正式输出仍位于隔离根之外，未被批次覆盖
- 未重投 `page_front`，未复用旧批次正式输出

## 处理决定

当前根保持原状，不用同一根盲目重启或重投未知页。`--recover-render-batch` 在该根因仍有 6 个 `Planned` 页而按探针前置条件拒绝执行（exit 2），这不是已确认完成，不能据此清除未知状态。

下一步必须先确认 Photoshop 2025/UXP 已回到空闲、无用户文档和无 modal，并重新建立已有受保护配对；随后建立新的隔离 P0 根，只执行未开始的全新 7 页批次。P0 真实门通过前不进入 P1/P2，不宣称 `PERF-01`、`RENDER-S6/S7` 或整体 V1 完成。
