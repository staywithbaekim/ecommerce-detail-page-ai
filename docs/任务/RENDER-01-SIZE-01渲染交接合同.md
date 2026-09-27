# SIZE-01 → RENDER-01 渲染交接合同

## 1. 合同目的

本合同规定动态尺码表从 SIZE-01 交给 RENDER-01 的最小、可验证边界。SIZE-01 负责结构化数据、确定性资源和 renderer-neutral `render-plan/v1`；RENDER-01 负责在受认证的 Photoshop/UXP 通道中安全执行、报告状态和原子提交输出。任何一方都不能用另一方的文档、fixture 或历史 UXP 探针证据替代自己的完成门。

## 2. 交接前提

2026-09-22 本款联验通过：第一组 `A馨妈6015-58` 的 7 页普通/动态 JPG+PSD 完整提交，25 单元格原文/确定性 PNG 嵌入/ICC/非目标层/源保护与新进程 0 心跳恢复通过。用户确认本款图片无问题，V1 固定文案保留、后续优化，见 [完整款联验](../验证/RENDER-01-S7完整款素材联验-20260922.md)。此证据不替代缺失的两份旧 SIZE 金标或 O-03，也不改变合同字段、资源用途与完整联合退出条件。

2026-09-21 联验增量：两款真实商品8页普通/动态JPG+PSD、独立校验和0心跳新进程恢复通过，各款尺码原文55个单元格与正确嵌入PNG闭合；见 [S6/S7证据](../验证/RENDER-01-S6-S7稳定性与产品收口-20260921.md)。第二款普通页外观需适配模板；未替换缺失旧金标，未关闭SIZE-S7/O-03或完整产品门。交接合同字段和资源用途保持不变。

- `render-plan/v1` Schema 和 `ps-bridge/v1` 协议仍以仓库冻结版本为准；
- 批次、款式、模板修订、页面和计划的身份必须来自当前批次快照；
- 动态尺码页必须已经由 SIZE-01 生成有效 `SizeTable`、`deterministic_size_table` PNG 和 `render-plan/v1`；
- 真实动态尺码 JPG/PSD 已有受控样本、ICC/可编辑性/提交恢复的限定证据；SIZE旧金标和完整产品联合门未关闭，不能据此关闭 SIZE-01 S7 或 RENDER-S5；
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

2026-09-21：按ADR-012/014接通相同冻结事实的单页追加attempt、独立artifact和事务化任务重开；普通/动态计划事实等价测试通过。普通页已提交/已取消两种真实重生成及新进程恢复通过；最新原生 WPF 动态页首次 JPG、正常重启不重渲染、单页 attempt2 也已通过。两 attempt 的模板/资源/尺码/放置事实相同，旧输出、源hash和历史journal不变，output_count=1。见 [原生动态流程](../验证/RENDER-01-S6原生动态尺码流程-20260921.md) 与 [S6重生成](../验证/RENDER-01-S6单页重生成与终态兼容-20260921.md)。正式输出仍遵循冻结的 JPG-only 设置；生产 verification_psd 不作为已发布 PSD。当前会话资源/输出暂存清理已有实机证据，崩溃遗留回收、完整故障与SIZE/产品联合门保留。合同/Schema 未变，后文历史候选状态不覆盖本条。

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

2026-09-20 批次增补：[S6 批次编排与桌面恢复](../验证/RENDER-01-S6批次编排与桌面恢复验证-20260920.md) 已覆盖生产注册、真实三页技术批次两成功一失败及新进程恢复；v15 变体 checkpoint、v16 追加 journal 与准备事实恢复保持历史不可覆盖。桌面只读正式预览保留业务构图检查，执行 completed 不代表业务视觉通过。真实多颜色、WPF 发起/暂停/取消、重生成与完整故障/资源生命周期、SIZE 旧金标/产品联合门仍未完成；旧 Unknown 页未重投。

2026-09-20 动态页增补：按 ADR-014 已实现两种互斥显式能力，已有尺码 SO 使用 calibrated-target，无尺码 SO 使用根层锚点/旧组隐藏/新 SO 插入；旧空 targets 计划仍可读但不可执行。新 run `ee952d73f1e641c394079c7ff7ef29c4` 已完成真实动态 JPG/PSD、114 层/69 文字层、ICC、结构化 digest/资源 hash 独立验证、原子提交和新进程恢复；输出副本 SO/原文字实际编辑通过，正式输出和源 hash 不变。原失败 attempt 保留，生产校准/批次入口、金标/视觉及完整 S5-S7 门仍开放。详见 [S5 合同与实机调试](../验证/RENDER-01-S5尺码插入合同与实机调试-20260920.md)。

2026-09-20 最新普通页证据：[替换栈与 JPG-only](../验证/RENDER-01-S4-S6替换栈与JPG-only验证-20260920.md) 已完成真实提交及新进程恢复。JPG-only 也经显式能力生成内部 verification_psd 用于独立结构检查，不改变正式输出选项。这些证据不替代动态尺码目标/旧表替换/放置合同与 SIZE 真实金标联验。

| 项目 | 当前状态 |
| --- | --- |
| SIZE-01 代码/RenderPlan 生产者 | 已并入 `codex/desktop-foundation-01`，候选提交 `df7505d`，主线合并提交 `ebd2c14` |
| 动态尺码离线消费合同 | SIZE-01 既有自动化覆盖；2026-09-19 RENDER 补充普通/合成动态计划的信封、摘要、资源及输出绑定校验，未冒充真实 PNG/金标证据 |
| 正式 UXP 动态尺码执行 | 显式插入候选已完成真实结构审计、输出验证、原子提交/恢复及副本编辑；生产校准入口和完整批次验收待完成 |
| 正式 bridge 接入 | 已真实配对、领取/ACK/受控资源、普通页 JPG+PSD 独立验证及原子提交/checkpoint，新进程恢复不重渲染。最新 run 260 次心跳、published=true、completed、exit0。生产 owner/受保护分发、动态页和其余故障/产品门仍开放。见 [S6 持续证据](../验证/RENDER-01-S6普通页原子提交与恢复验证-20260920.md)，不关闭 S1-S7 或真实尺码输出门 |
| 办公室 Photoshop 动态尺码 JPG/PSD | 当前新增真实样本的技术子门通过；旧金标联验、产品视觉和故障矩阵未关闭 |
| 正式 `.ccx` / 签名安装 | 由 `INSTALL-01` 后续负责 |

本轮离线结果见 [RENDER-S1 验证记录](../验证/RENDER-01-S1合同校验与提交边界验证-20260919.md)。S1、S2/S3 安全通道与真实绑定、SIZE-S7/RENDER-S5 输出门均未据此关闭。

后续实现与实机加载结果见 [S1-S3 候选验证](../验证/RENDER-01-S1-S3传输与资源候选验证-20260919.md)。新增真实尺码来源见 [20260919 样本登记](../验证/RENDER-01-S0新增真实尺码样本登记-20260919.md)，不替代旧金标 hash。另已确认动态页 builder 的 `targets=[]` 且 size_table 无校准位置；S5 前必须以 ADR-014 的显式合同演进/迁移补齐模板目标与旧表替换证据，不能把“资源 hash 闭合”当作“可安全嵌入模板”。

继续执行补充：localhost 仅完成无秘密 HTTP 对照，真实 UXP `redirect:error` 的跨端口跟随已构成新安全阻断，见 [ADR-032](../架构决策/ADR-032-UXP回环主机名兼容候选.md)。新增 session 生命周期 owner 与临时资源 staging 模块不替代受保护首次分发、main/实际文件或正式输出证据；原验收门保持开放。

## 9. 交接签字门

2026-09-22 交接增量：完整款既有图片/动态尺码验收保持有效。A阶段新增执行中桥接中断、修复后宿主清理与恢复证据；尺码编辑窄窗布局/焦点/按钮重叠已修复并通过UI3/3。窗口工具重试恢复后，新版宽窄、键盘、输入门禁及不保存退出实测通过；另修复有效输入后的旧提示残留，状态/ViewModel6/6与新构建原生复验通过，持久化数据不变。125%/真实IME/完整UIA与视觉、旧金标仍开放。详见 [A阶段记录](../验证/V1-A阶段故障与尺码窗口收口-20260922.md)，不将上述代码和限定实机证据替代整个SIZE-S7签字。

只有以下条件同时满足，SIZE-01 的动态页才可申请 RENDER-01 真实执行：

1. 计划 Schema、资源用途、digest 和页面身份闭合；
2. 动态尺码 PNG 实际字节 hash 与计划一致；
3. 模板副本和输出根目录经过路径/权限/源文件保护检查；
4. RENDER-01 已通过配对、token、单通道和未知结果安全门；
5. 办公室 Photoshop 2025 `26.8.1` 真实运行记录可追溯。

真实 JPG/PSD 成功、失败清理、恢复和产品视觉确认仍需分别回填 `SIZE-01 S7`、`RENDER-01 S5-S7` 和验收矩阵，不能由合同签字本身代替。
