# SIZE-01 → RENDER-01 渲染交接合同

## 1. 合同目的

本合同规定动态尺码表从 SIZE-01 交给 RENDER-01 的最小、可验证边界。SIZE-01 负责结构化数据、确定性资源和 renderer-neutral `render-plan/v1`；RENDER-01 负责在受认证的 Photoshop/UXP 通道中安全执行、报告状态和原子提交输出。任何一方都不能用另一方的文档、fixture 或历史 UXP 探针证据替代自己的完成门。

## 2. 交接前提

- `render-plan/v1` Schema 和 `ps-bridge/v1` 协议仍以仓库冻结版本为准；
- 批次、款式、模板修订、页面和计划的身份必须来自当前批次快照；
- 动态尺码页必须已经由 SIZE-01 生成有效 `SizeTable`、`deterministic_size_table` PNG 和 `render-plan/v1`；
- 当前候选中的真实动态尺码 JPG/PSD 尚未在 Photoshop 联验收，交接合同可以先用于离线消费测试，但不能据此关闭 SIZE-01 S7 或 RENDER-S5；
- 所有源 PSD、市场尺码图和源照片均为只读输入，实机必须使用副本。

## 3. 必须交接字段

### 3.1 RenderPlan 顶层

RENDER-01 只接受通过 Schema 的完整 JSON，至少核对：

| 字段 | 要求 |
| --- | --- |
| `schema_version` | 固定为 `render-plan/v1` |
| `plan_id` / `idempotency_key` / `correlation_id` | 合法、稳定、按当前页面 attempt 唯一；不得用路径或 token 生成 |
| `batch_id` / `product_id` / `template_revision_id` | 与当前冻结批次和模板快照一致 |
| `plan_digest` | 按批准 canonical 规则重算一致 |
| `resources` | 每项只有逻辑 ID、角色、MIME、hash、长度；不含绝对路径、临时路径、token |
| `document` | 模板资源、页面 ID、内容类型、画布和颜色位深闭合 |
| `targets` | 普通页按校准结构闭合；动态尺码页不得附带互相冲突的商品 target |
| `output` | 只有逻辑 artifact ID、文件名和导出语义；`atomic_page_commit=true` |

### 3.2 动态尺码页 `size_table`

动态页必须满足：

```text
document.content_type = dynamic_size_table
size_table != null
size_table.embed_as = smart_object
size_table.render_resource_id -> resources[].role = deterministic_size_table
size_table.grid_digest = sha256:<PNG实际字节hash>
size_table.structured_data_digest = 当前 SizeTable.DataDigest
size_table.columns / rows = 已确认的原文结构，列顺序和单元格关系一致
```

RENDER-01 必须再次验证 `render_resource_id` 的实际字节 hash、长度、MIME 和用途；不能只相信计划中的字符串。`deterministic_size_table` 是 SIZE-01 生成的无损 PNG，不是市场尺码图，不是 `analysis_copy`，也不是生成式图片。

### 3.3 普通页

普通套图页必须满足 `size_table=null`，目标列表和高分辨率源图资源分别按模板校准结构闭合。RENDER-01 不得因为同一批次另有动态尺码页而向普通页注入尺码资源。

## 4. 资源绑定合同

桌面端在 bridge 执行信封中把逻辑资源绑定为不透明 handle；绑定过程必须同时核对：

1. 资源属于当前批次、模板修订和页面；
2. 文件位于已授权的应用内部副本/输出根目录，不能路径逃逸；
3. 实际长度、SHA-256、MIME 和只读属性与计划一致；
4. `template_copy`、`high_resolution_render_source`、`deterministic_size_table` 用途不能互换；
5. 绑定失败时不提交 Photoshop，不产生正式 JPG/PSD；
6. 绑定句柄、真实路径和 token 只存在于短期执行信封/受保护运行时，不进入 RenderPlan、SQLite 业务对象、日志或 evidence。

## 5. 执行结果合同

RENDER-01 返回页面级执行状态和证据，至少闭合：

| 结果 | 必须证明 |
| --- | --- |
| `accepted` | bridge 已接收计划，未宣称 Photoshop 完成 |
| `running` | execution ID、最后事件序号和阶段可查询 |
| `completed` | Photoshop 已完成；JPG/PSD 实际 hash、长度、ICC/PSD 结构检查通过；checkpoint 与 manifest 原子收口 |
| `failed` | 错误码、范围、可重试性、用户动作和当前页清理结果；其他成功页不受影响 |
| `cancelled` | 取消在安全边界生效；不删除既有正式输出 |
| `unknown` | 未确认执行结果；只能查询或人工处理，禁止自动重提产生副作用 |

动态尺码 `completed` 还必须记录 `size_table_id`、结构化数据 digest、PNG resource hash、模板副本 hash、目标页 ID 和源文件保护结果。证据不能保存完整路径、图片字节、Key、认证头或原始 Photoshop 异常。

## 6. 原子输出与恢复合同

- 每个页面独立暂存和提交；JPG 与可选 PSD 只有在各自校验通过后才进入同一页面的正式提交结果；
- `atomic_page_commit=true` 不允许半成品伪装成成功；任一必需输出失败，当前页进入失败/恢复，不覆盖旧正式输出；
- 页面 checkpoint 在输出 flush/hash、manifest 和 SQLite 事务按批准顺序成功后才能推进；
- 重启只跳过 hash、manifest、checkpoint 一致的成功页；未知页先查询 bridge；
- 当前页重生成使用新的 attempt/计划身份，只影响当前款当前页，不改模板修订、其他页面或其他款式。

## 7. 验收样例矩阵

| 样例 | 预期 |
| --- | --- |
| 有效普通页 | 目标结构通过，JPG/可选 PSD 原子提交，源 hash 不变 |
| 有效动态尺码页 | 确定性 PNG 作为智能对象嵌入，JPG/PSD、ICC、结构 digest 和页面隔离通过 |
| 动态页缺资源/错 hash | 提交前阻断，不打开或不修改 Photoshop 正式副本 |
| 同一页重复提交 | 由 `idempotency_key + plan_digest` 去重，不重复替换或产生第二套输出 |
| 第三页执行失败 | 清理第三页临时产物，保留前两页成功结果，批次按分因收口 |
| bridge 断线 | 进入可查询/恢复状态，不自动重提未知计划 |
| Photoshop 未知弹窗 | 暂停并要求用户处理，不模拟点击 |
| Photoshop 重启 | 旧 token 失效，重新配对；按 checkpoint 只继续未确认页 |
| 输出目录有非目标文件 | 仅操作本页已确认目标，不清理或覆盖其他文件 |

## 8. 合同状态

| 项目 | 当前状态 |
| --- | --- |
| SIZE-01 代码/RenderPlan 生产者 | 已并入 `codex/desktop-foundation-01`，候选提交 `df7505d`，主线合并提交 `ebd2c14` |
| 动态尺码离线消费合同 | 已由 SIZE-01 自动化测试覆盖 |
| 正式 UXP 动态尺码执行 | 未实现 |
| 正式 bridge 接入 | 未实现；历史验证 bridge 仅作技术证据 |
| 办公室 Photoshop 动态尺码 JPG/PSD | 未验收，等待实机 |
| 正式 `.ccx` / 签名安装 | 由 `INSTALL-01` 后续负责 |

## 9. 交接签字门

只有以下条件同时满足，SIZE-01 的动态页才可申请 RENDER-01 真实执行：

1. 计划 Schema、资源用途、digest 和页面身份闭合；
2. 动态尺码 PNG 实际字节 hash 与计划一致；
3. 模板副本和输出根目录经过路径/权限/源文件保护检查；
4. RENDER-01 已通过配对、token、单通道和未知结果安全门；
5. 办公室 Photoshop 2025 `26.8.1` 真实运行记录可追溯。

真实 JPG/PSD 成功、失败清理、恢复和产品视觉确认仍需分别回填 `SIZE-01 S7`、`RENDER-01 S5-S7` 和验收矩阵，不能由合同签字本身代替。
