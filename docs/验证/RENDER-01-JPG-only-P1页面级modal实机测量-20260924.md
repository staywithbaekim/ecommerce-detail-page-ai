# RENDER-01 JPG-only P1 页面级 modal 实机测量（2026-09-24）

## 结论

P1 第一项“同一页面多个 target 合并到一个页面级 UXP `executeAsModal`”已完成真实 Photoshop 2025 批次验证：批次正常完成，7/7 页面 `Committed`，正式输出和清理门通过。该证据关闭 P1 第一项的真实行为门，但没有证明整体性能获得显著改善；下一项模板副本复用暂不实施，P2 不启动。

## 隔离根与执行

- 隔离根：`tmp/r01-render-perf-20260924-p1-r1`
- 批次：`batch_6aaa27c74fab45a2bba1f6695772cce6`
- 命令：`RenderBridgeProbe --run-paired-render-batch-performance`
- 页面：7 页、11 个 target，`include_editable_psd=false`
- 多 target 页面：`page_details=4`、`page_display1=2`、`page_display2=2`
- 单 target 页面：`page_front`、`page_back`、`page_fabric`
- 尺码页：`page_size` 无替换 target

## 真实结果

- `batch-execution-f408e7e1bdf645c3981b47c65249fd08.json`：`status=Completed`、`output_count=7`、`authenticated_heartbeats=157`
- `transport_fault=null`、`binding_fault=null`，无 Photoshop host interruption
- 7/7 页面均为 `Committed`，没有失败页或未知 execution
- 正式输出：7 个 JPG、0 个正式 PSD、0 个 verification PSD
- 暂存资源副本：0；输出 `.part`：0

独立只读核验使用项目固定 Python 3.10.11 环境和 Pillow：

- 7 个 manifest、7 个 `committed.marker` 均存在
- 每个 manifest 的 plan digest、JPG byte length 和 SHA-256 与实际文件一致
- 7 个 JPG 均完整解码，尺寸与页面 canvas 一致
- 7 个 JPG 均嵌入 3144-byte sRGB ICC
- 没有 PSD 文件进入正式输出树

## 性能对比

| 指标 | P0 `p0-r6` | P1 `p1-r1` | 说明 |
|---|---:|---:|---|
| `batch.run` | 193.732 s | 409.099 s | P1 含 272.533 s 首次连接/ready 等待 |
| `page.wait_ready` | 56.517 s | 272.533 s | 按计划口径从有效批次时间排除 |
| 排除连接等待后的有效批次 | 137.215 s | 136.565 s | P1 约少 0.650 s（约 0.47%），不足以宣称显著收益 |
| `resources.prepare` 合计 | 1.583 s | 1.808 s | P1 没有模板复用 |

多 target 页的 `page.run` 合计约从 P0 的 81.143 s 变为 P1 的 80.689 s，差异约 0.454 s；单页差异处于当前实机波动范围。P1 首页的额外等待来自连接/ready 边界，不把它归因于 UXP modal 改动。

## 模板副本复用决定

本批次 7 页的模板 `resource_id` 和 SHA-256 均各不相同：`template_page_front`、`template_page_back`、`template_page_fabric`、`template_page_details`、`template_page_display1`、`template_page_display2`、`template_page_size`。因此本批没有可复用的同 revision/hash 模板副本，不能用本次实测证明批次级模板 lease 能带来收益。

当前仍保留每页独立的 `PreparedRenderResources`、`BridgeResourceLease`、`RenderTemporaryFileLedger` 和终端释放语义，不改为未经证明的全局缓存。页间准备/验证重叠和 P2 仅在后续出现明确收益证据后再启动。

