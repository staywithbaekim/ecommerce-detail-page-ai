# RENDER-01 正式 Photoshop/UXP 渲染适配器纵向实现任务书

## 1. 任务信息与批准门

| 项目 | 内容 |
| --- | --- |
| 任务 ID | `RENDER-01` |
| 任务名称 | 正式 Photoshop/UXP 渲染适配器纵向实现 |
| 状态 | **V1 第一次出图限定已通过；完整 RENDER-01 高级故障、修复与产品/SIZE 联验收暂缓** |
| 创建日期 | 2026-09-03 |
| 更新日期 | 2026-09-27 |
| 执行基线 | `codex/desktop-foundation-01`，当前 HEAD `fbdf555`；保留既有 ARCH/AI/模板与 RENDER 未提交改动，未重置工作树 |
| 直接交接 | `SIZE-01` 的 `render-plan/v1` 动态尺码页计划和既有普通页计划 |
| 相关合同 | `render-plan/v1`、`ps-bridge/v1`、ADR-011 至 ADR-014、ADR-020 |
| 后续协作 | `INSTALL-01` 负责签名、正式 `.ccx`、安装/升级/回滚/卸载；`PERF-01` 负责完整 `machine_total` |

本任务书是 RENDER-01 的执行合同。任务书建立不等于 UXP 已做好，也不等于办公室 Photoshop 已验收。聊天结论只有同步到本任务书、交接合同、项目状态和验收矩阵后，才作为可追溯项目记录。

## 2. 任务目标

在不把业务规则泄漏到 Photoshop/UXP 的前提下，建立唯一的 V1 Photoshop 执行通道：桌面端提交已校验的 renderer-neutral `RenderPlan`，通过受认证的 `ps-bridge/v1` 回环通道交给正式 UXP v5 adapter；adapter 只执行计划、报告进度、处理安全边界和返回可核对的执行证据，最终以页面级 checkpoint 和 JPG/可编辑 PSD 原子提交收口。

RENDER-01 的目标结果是：

- 正式 UXP adapter 能消费普通页和 `dynamic_size_table` 页的 `render-plan/v1`；
- 正式 bridge 完成配对、短期会话、资源绑定、计划提交、状态查询、心跳、取消和释放；
- Photoshop 单通道执行，页面/款式级进度、暂停、断线查询和未知执行结果可恢复；
- JPG 使用 Photoshop 原生导出并核对 sRGB ICC；可选 PSD 保持可编辑并核对目标结构；
- 当前页失败隔离、临时产物清理、旧正式输出保护和重启续跑可验证；
- 证据能区分“已接收”“执行中”“完成”“失败”“未知”，不把 bridge 握手成功伪装成生产成功。

## 3. 前置条件与交接边界

### 3.1 必须先满足

1. `TPL-01`、`INPUT-01`、`AI-01` 已在当前主线通过，其模板快照、资产 manifest 和状态合同不得被重新定义。
2. `SIZE-01` 至少已经提供并通过交接合同中的 RenderPlan、资源 hash、结构化尺码数据 digest 和资源用途校验；在 SIZE-01 的真实动态尺码 JPG/PSD 联验收尚未完成时，RENDER-01 可以做合同/适配器离线验证，但不得宣称完整 SIZE-01 或端到端尺码输出通过。
3. 当前正式支持目标为 Windows 11 x64 + Photoshop 2025 `26.8.1`，每台机器必须记录完整构建号；已有改版构建证据不能外推到其他构建。
4. 办公室真实 Photoshop 是本任务的外部执行环境。没有 Photoshop 的开发机只能完成协议、计划、静态检查和离线测试，不能产生真实 Photoshop 完成证据。

### 3.2 本任务不接管的职责

- AI 识别、候选排序、尺码提取、缺值/冲突判断、业务红黄绿和用户处理路由；
- 模板校准、模板修订、批次快照和源 PSD/JPG 的归属；
- 业务层布局计算。动态尺码布局由 SIZE-01 生成确定性 `deterministic_size_table` 资源；UXP 只按计划嵌入并执行；
- 签名、正式 `.ccx`、客户安装、升级、回滚、卸载和 SmartScreen/杀毒准入；这些属于 `INSTALL-01`；
- 完整 70 页性能承诺；属于 `PERF-01`。

## 4. 硬约束与禁止事项

- 只消费已通过 `render-plan/v1` 的计划，不读取 AI 结果、不做业务判断、不接受 Photoshop 命令字符串；
- 不修改用户源 PSD、源照片、市场尺码图、人工参考或既有正式输出；所有 Photoshop 操作使用应用自有模板副本和目标卷暂存；
- 任何资源必须由逻辑 ID、用途、hash、长度和不透明绑定句柄闭合；真实路径、session token、pairing secret 不进入 RenderPlan、领域记录、日志或验收证据；
- 只监听 `127.0.0.1`，拒绝远程网卡、局域网和未知本机服务转发；不因端口占用而扫描其他端口；
- pairing secret 存储在批准的受保护存储中；不得写入代码、manifest、普通文件、模板包、日志或诊断包；
- Photoshop modal 内不强行点击、不强制中断；取消只在安全边界生效；未知弹窗必须暂停并要求用户处理；
- `idempotency_key + plan_digest + checkpoint` 是执行副作用边界；未知完成状态先查询/人工确认，不能盲目重提；
- 失败时清理当前页临时产物，不能删除其他已成功页、用户目录中的非目标文件或旧正式输出；
- 正式插件不得沿用验证插件 `local.ecommerce.uxp-validation` 的 ID、名称或宽松权限；不得把验证插件 0.4.7 当成正式实现。

## 5. 纵向阶段与质量门

RENDER-S0 至 RENDER-S7 是本任务内部的可审计质量门，不是新增的产品大阶段。每一门可以在同一执行窗口连续完成；任一安全门、真实证据门或批准合同不满足时停止并记录阻断，不用更改文案绕过。

### RENDER-S0：上游合同和实机准入核对

交付：核对 `render-plan/v1`、`ps-bridge/v1`、模板资源副本、SIZE-01 动态资源、支持矩阵、办公室 Photoshop 完整构建号和 UXP Developer Tool 条件；登记测试副本 hash 和不进入 Git 的样本位置。

退出门：所有生产者和合同在当前基线存在；已知验证插件与正式插件的边界写清；无 Photoshop 的本机只能标记为离线准备，不能关闭真实执行门。

### RENDER-S1：正式 adapter 与 bridge 实现合同

交付：正式 plugin ID/名称/manifest 权限草案，bridge server/client 端口和 DTO，严格请求/响应 Schema，版本拒绝、大小限制、错误结构和协议迁移策略；桌面端通过 Application port 调用，业务层不依赖 UXP 类型。

退出门：正反合同、未知字段、超限请求、错误脱敏和架构依赖测试通过；不得把验证插件复制成生产插件。

### RENDER-S2：配对、会话和 Photoshop 单通道

交付：一次性 nonce + HMAC 配对、受保护秘密读取、随机执行端口、短期 token、版本/实例绑定、心跳、释放、单 Photoshop execution gate 和已知 modal/未保存文档阻断。

退出门：nonce 重放、token 过期/轮换、端口占用、错误版本、第二执行请求和 Photoshop 实例变化均 fail closed；真实支持机器完成最小握手证据。

### RENDER-S3：RenderPlan 校验和资源绑定

交付：提交前完成 Schema、`plan_digest`、模板副本、资源用途/hash/长度、目标结构、输出文件名和目标卷权限检查；把逻辑资源绑定为不透明句柄，不把路径回写计划。

退出门：普通页 `size_table=null`；动态页 `size_table` 非空且 `deterministic_size_table` 资源闭合；拒绝 `analysis_copy`、市场尺码图、路径逃逸、红页计划和未知 target。

### RENDER-S4：普通页单页/款式执行

交付：按已校准层路径和 `superseded_layers` 执行智能对象替换、裁切/缩放/移动、固定层保护、JPG sRGB ICC 导出和可选 PSD 保存；返回逐页事件和执行证据。

退出门：使用真实模板副本和真实源照片，与人工参考完成结构/视觉/ICC/可编辑性核对；源文件 hash 不变，非目标图层和非目标文件不变。

### RENDER-S5：动态尺码页执行

交付：消费 SIZE-01 的 `dynamic_size_table` 计划，将确定性 PNG 作为智能对象资源嵌入已校准模板副本；不重新识别、不改表格、不贴市场图；验证普通页与尺码页隔离、当前款当前页隔离。

退出门：至少用批准真实尺码金标和真实尺码表模板完成 JPG/PSD；逐格内容以 SIZE-01 结构 digest/原文为准；输出尺寸、ICC、PSD 可编辑结构、源 hash 和旧输出保护全部有证据。

### RENDER-S6：checkpoint、暂停、失败和未知结果恢复

交付：页面级 checkpoint、款式级安全暂停、断线查询、应用/Photoshop 重启恢复、执行未知状态、单页失败隔离、临时文件清理、旧输出保护和幂等重提策略。

退出门：已成功页面不重复渲染；未知页先查询或人工确认；第三页失败不删除前两页；暂停不打断 modal 内页面；恢复不扩大到其他款式/批次。

### RENDER-S7：真实批次验收与治理收口

交付：办公室支持机器真实批次、动态尺码页和普通页的运行记录，故障矩阵、产品走查、证据 manifest、reviewed diff、源文件安全审查和验收矩阵同步。

完成门：真实 JPG/PSD 原子输出、ICC、可编辑结构、源文件保护、页面隔离、暂停/恢复/失败/未知结果和完整证据均通过；没有真实 Photoshop 证据时只能保持进行中。

## 6. SIZE-01 → RENDER-01 交接输入

RENDER-01 接收的最小输入定义见 [SIZE-01→RENDER-01 渲染交接合同](RENDER-01-SIZE-01渲染交接合同.md)。核心原则是：SIZE-01 交付“该渲染什么”的事实，RENDER-01 负责“在 Photoshop 中安全执行并证明结果”。

| 输入 | 生产者 | RENDER-01 使用方式 | 不允许的行为 |
| --- | --- | --- | --- |
| `render-plan/v1` JSON | SIZE-01 /既有 RenderPlan factory | Schema、digest、页类型和资源闭合后提交 | 修改计划语义、拼接 Photoshop 命令 |
| `template_copy` | TPL-01/模板资源仓储 | 绑定已校验的应用内部副本 | 打开或改写用户源 PSD |
| `deterministic_size_table` | SIZE-01 栅格器 | 绑定 PNG 并嵌入尺码页智能对象 | 重新 OCR、贴市场尺码图、生成式补图 |
| `high_resolution_render_source` | INPUT-01/AI-01 确认选择 | 绑定源图副本并替换已校准目标 | 使用 `analysis_copy` 或未确认素材 |
| checkpoint/manifest | 桌面端持久化编排 | 推进页面执行和原子提交状态 | 只相信 UXP 内存状态 |

## 7. 验收矩阵映射

| 来源 | RENDER-01 覆盖 | 主要证据 |
| --- | --- | --- |
| 总体技术方案 8.4/8.5/9.4/11.2 | 安全接管、单通道、暂停、失败隔离、断线查询、未知结果 | RENDER-S2/S6 |
| PRD 15/16/18/22.3/22.4 | Photoshop 执行、JPG/PSD、ICC、恢复、性能输入证据 | RENDER-S4/S5/S6；PERF-01 负责完整时限 |
| ADR-013 / `ps-bridge/v1` | loopback、配对、token、幂等、事件、错误 | RENDER-S1/S2/S6 |
| ADR-014 / `render-plan/v1` | 计划语义、资源角色、动态尺码、路径边界 | RENDER-S1/S3/S5 |
| SIZE-01 | 动态尺码结构 digest、资源 hash、当前页计划 | RENDER-S0/S3/S5；SIZE-01 S7 真实联验仍需回补 |
| INSTALL-01 | 签名、`.ccx`、安装/升级/卸载/回滚 | 不在本任务内提前实现 |

验收矩阵中的 `RENDER-01` 行在 RENDER-S7 完成前保持“待正式实现与验收”；历史 TV-01～TV-14 只作为能力和风险输入，不得代替正式 RENDER-01 证据。

## 8. 验证与证据要求

- 每次实机运行记录 Windows 11 x64、Photoshop 完整构建号、UXP runtime、正式/验证插件版本、协议版本、模板/资源/源文件 hash 和输出摘要；
- 真实操作必须使用模板、源图和输出的明确副本，源文件前后 hash 必须一致；
- evidence 只保存版本、状态、错误码、阶段耗时、事件序号、结构摘要、ICC 摘要和 hash，不保存 Key、认证头、图片字节、完整路径或原始 Photoshop 异常；
- 自动化测试覆盖 Schema 正反例、握手/会话、资源绑定、普通页/动态尺码互斥、幂等、断线查询、checkpoint、失败清理和未知结果；
- 办公室实机至少覆盖：普通页、动态尺码页、JPG-only、JPG+PSD、真实失败、暂停后恢复、Photoshop 重启、bridge 断线、未知执行和输出目录非目标文件保护；
- 未执行的实机、安装、签名或性能命令必须明确记录为未执行，不用 fixture、截图或历史 TV 记录补写。

## 9. 回滚与停止规则

- 合同或资源绑定失败：不启动 Photoshop，不写正式输出，保留原计划和旧结果；
- 配对/版本/能力/未保存文档/未知弹窗失败：阻断接管，提示用户处理或升级，不盲点继续；
- 页面执行失败：隔离当前页并清理该页临时产物，其他已成功页和非目标文件保持不变；
- 输出或数据库提交结果未知：先查询 checkpoint/bridge/evidence，不能确认时保持 `RecoveryRequired`，不重复产生副作用；
- 发现源文件被触碰、路径越界、秘密泄露、未知命令入口或验证插件冒充正式插件：立即停止，保留证据并回滚候选；
- 新证据表明当前方案无法满足批准合同或将越过安全边界时，停止该方案并记录具体阻断；继续不受影响的工作，不用固定重试次数代替判断，也不通过放宽安全门继续。

## 10. 交接清单

### RENDER-01 开始实现前

1. RENDER-S0 记录当前主线、上游合同、SIZE-01 交接合同和办公室 Photoshop 环境。
2. 产品负责人确认正式插件命名/ID、最小权限和当前支持机器；未确认前不创建可交付 `.ccx`。
3. 明确本轮只做正式执行适配器和 bridge，不顺带实现安装、性能或业务规则。

### RENDER-01 申请治理复核前

1. RENDER-S1 至 S6 的代码、聚焦测试和故障证据存在；正式 UXP 与验证插件边界清楚。
2. 普通页和动态尺码页均有通过 Schema 的计划；资源绑定、输出命名和 checkpoint 可追溯。
3. 真实 PS2025 `26.8.1` 运行证据齐全，源 PSD/JPG hash 不变，输出已原子提交。
4. `docs/验收矩阵.md`、`docs/当前项目状态.md`、`项目总览.md`、本任务书和交接合同同步。
5. reviewed diff、架构依赖、禁止产物、敏感信息和输出目录安全检查通过。
6. 明确列出尚由 `SIZE-01` S7、`INSTALL-01`、`PERF-01`、`FLOW-01` 和 `REPAIR-01` 负责的事项。

## 11. 当前状态与下一步

2026-09-22 第二执行批通过：PS2025第二页modal内异常退出后由生产心跳超时自然收口，402心跳，新进程0心跳保持Committed/Unknown/Planned；真实输出锁导致目录发布0x80070005，788心跳，释放后新进程0心跳直接恢复第二页。原成功4文件、输入副本/哨兵/旧账本不变，新增4文件与暂存一致，输出锁案例资源/part清零。详见 [A阶段第二执行批](../验证/V1-A阶段故障与尺码窗口收口-20260922.md)。本批只增加探针、证据与文档，probe构建0/0；完整原生批次为下一执行项，物理磁盘满/卷不可用、双端轮换、历史PSD400和SIZE剩余门仍开放。下文此前“强杀PS待补”由本条更新，不关闭完整RENDER。

2026-09-22 A 阶段增量见 [执行中断与尺码窗口](../验证/V1-A阶段故障与尺码窗口收口-20260922.md)：第二页 modal 内桥接硬退出及新进程0心跳恢复通过；修复 UXP 断线后受控文档清理仍依赖认证心跳，新独立实机退出86后自动关闭文档、恢复保留Unknown且首4文件/输入/历史账本不变。输出元数据发布失败的自有pending按文件身份清理，Infrastructure115/115、插件226/226。未覆盖的强杀PS/卷/双端轮换及历史PSD400继续保留；按 [V1计划](V1交付执行计划-20260922.md) 继续A阶段，不关闭整个RENDER。

2026-09-22 完整款增量通过：用户指定第一组 `A馨妈6015-58` 与同组模板完成 7 页/11 框和动态尺码 JPG+PSD，3,809 次心跳、Completed/output_count=7；25 单元格、ICC/非目标内容/20 原件 hash、新进程 0 心跳恢复与 28 正式文件不变通过。名称、纯旋转、固定 shape/禁用效果保存证据及关闭/提交竞态修正均有真实复验；worker 已更新到本地桌面运行目录。用户确认图片通过，V1 保留现有文案，后续再优化。见 [完整款联验](../验证/RENDER-01-S7完整款素材联验-20260922.md)。本次限定门完成，继续剩余执行/输出卷/配对故障；不以此关闭历史 PSD400 根因、SIZE 旧金标/O-03 或完整 S6/S7，也不等待第二款模板。

2026-09-21 最新见 [S6/S7收口记录](../验证/RENDER-01-S6-S7稳定性与产品收口-20260921.md)：两款真实颜色/正反面与尺码8页JPG+PSD、独立校验与0心跳恢复通过；提交后/上传中退出、限定遗留回收、配对不一致/恢复、输出路径占用保护、PS空闲重启通过。第二款方图/纵版模板外观不合格；已修复次色主体几何丢失，真实几何负例4页正确无计划待处理。需适配模板后做新修订验证；历史PSD400根因、SIZE旧金标/O-03与其余故障/产品门仍开放，S6/S7未关闭。

2026-09-21 原生动态增量：按 [当前交付路线](当前交付路线与用户配合清单-20260919.md) 完成第一步。合法校准修订/新批次经真实分析和正式工厂准备后，首次 JPG、正常重启不重渲染、原生单页 attempt2 均通过；source/旧输出/journal 保留，output_count=1。增量修订资源继承、虚拟化文件路径及异常状态/句柄清理已修复；build0/0、Infrastructure聚焦65/65。详见 [原生动态流程](../验证/RENDER-01-S6原生动态尺码流程-20260921.md)。下一步定位无取消意图 PSD400，补中断/输出卷/配对轮换与崩溃遗留回收，最后产品/SIZE 联验。本次只关闭原生动态执行子门，下方同日旧“原生动态待补”不覆盖本条。

2026-09-21 最新覆盖：[S6 单页重生成](../验证/RENDER-01-S6单页重生成与终态兼容-20260921.md)已实现SQLite v17/Version2追加attempt与同事务任务重开；已提交页和取消批次指定页分别387心跳真实成功，旧输出/源hash/历史不变、不重复计数且原Planned页不投递，均新进程恢复0心跳。正式 probe 动态尺码页也完成 attempt 1→2：58 次认证心跳、两次 Committed、恢复 0 心跳且不重渲染，两个 artifact 经当前 worker 只读复核为 114 层/69 个文字层。[生命周期/取消](../验证/RENDER-01-S6款式暂停恢复与传输诊断-20260921.md)完整批次780心跳、资源副本与part清零、恢复通过；同一报告新增 modal 内取消实机，11 次认证心跳命中 `host_state=busy`/`modal_state=running` 后安全取消，资源副本、输出 `.part`、正式输出为 0，SQLite integrity 为 ok。最新 solution/probe build 0/0；本轮 Infrastructure 620通过/5跳过/0失败，Bridge Contracts 58/58，Render Application 160/160，插件 Node 211/211。继续原生 WPF 重生成、应用/Photoshop/bridge 中断与输出卷故障矩阵、崩溃遗留回收；历史无取消意图 PSD400 及 S5/S7 产品/SIZE 联验不关闭，RENDER 未完成。

2026-09-20 最新批次覆盖：生产 coordinator/owner/availability/目标卷资源准备与 WPF 逐页正式输出已接通；三页技术身份批次 776 次认证心跳、两页 JPG/PSD 提交、第三页故障隔离，新进程 output_count=2 不重渲染，正式文件与源 hash 不变。桌面恢复保留构图检查和变体身份，旧 journal 首版只读兼容已验证。见 [S6 批次与桌面恢复](../验证/RENDER-01-S6批次编排与桌面恢复验证-20260920.md)。继续 WPF 真实发起/款式暂停/取消、新 attempt/终态兼容、目标卷资源收尾、完整故障/产品/SIZE 门；一次 PSD 回传中断仍 Unknown，未确认根因。共用一张照片的技术变体不算真实颜色验收，S1-S7 整体仍未完成。

2026-09-20 当前覆盖：尺码校准生产入口、持久化/冻结新修订、模板包v2及v1索引兼容已实现，原生保存/重开/包往返与持久化规则准备后的真实输出/恢复通过。普通页正式工厂已接通绝对placement，真实普通填充JPG/PSD提交和恢复通过（387次认证心跳）；无解或未定义布局按页标黄不提交，真实worker裸摘要兼容已修复。见 [S4生产放置](../验证/RENDER-01-S4生产放置与计划工厂实机验证-20260920.md) 与 [S5校准增量](../验证/RENDER-01-S5尺码插入合同与实机调试-20260920.md)。后续范围为既有owner/broker的桌面批次生产调用、受控资源与目标卷、款式安全暂停/恢复、逐页结果及多变体checkpoint，随后补实机故障和产品/SIZE联合门。外部依赖仍为已安装PS/UDT及受控样本；不重写适配器，不扩大权限，不将单页成功作为阶段退出。

2026-09-20 S5 增量：新 run `ee952d73f1e641c394079c7ff7ef29c4` 已完成真实动态 JPG/PSD、114 层/69 文字层和 ICC 独立验证、原子提交及新进程恢复；57 次认证心跳、completed、exit0，恢复 output_count=1、不重渲染。输出副本实际编辑嵌入尺码 SO 和原文字层通过，正式输出与源 hash 不变；原失败 run 保留。最新插件已 Reload，明确失败终态仍待对应实机故障用例。详见 [S5 持续调试证据](../验证/RENDER-01-S5尺码插入合同与实机调试-20260920.md)。生产校准入口、普通页 placement、完整编排、旧金标/产品门和 S6/S7 仍未完成。

2026-09-20 配对增补：factory/设置页入口已注册，隔离 Windows/UXP 受保护配对与重载后空输入重连通过，3 次认证 ready 心跳、exit0；保存/备份读回与失败回滚已加固。完整生产编排及双端轮换实机故障门未完成，详见 [设备配对与重连](../验证/RENDER-01-S2设备配对与重连验证-20260920.md)。

2026-09-20 最新覆盖：替换栈 JPG+PSD 与 JPG-only 两次真实运行分别 388/390 次认证心跳、completed、published=true、exit0；37 层/14 文字层独立检查、指定旧层隐藏、3144 字节标准 sRGB ICC、源 hash 不变。JPG-only 显式能力要求临时 verification_psd，正式目录不发布该文件。两次新进程恢复不重渲染、output_count=1，插件已确认释放临时资源。最新 build 0/0、Infrastructure 聚焦 88/88。见 [S4/S6 持续证据](../验证/RENDER-01-S4-S6替换栈与JPG-only验证-20260920.md)。生产 owner/受保护分发、生产 placement、动态尺码和完整故障/编辑/产品门仍开放；下文 superseded/JPG-only 尚未实现的表述是历史。

2026-09-20 原子提交进展覆盖：run `986f5af920db4e2cb4537e9453129e6b` 已真实完成正式独立媒体验证、页目录原子提交、SQLite Committed 和 manifest/marker/hash 一致性；260 次认证心跳、published=true、completed、exit0。新进程恢复 output_count=1、不重渲染；源 hash 未变。现有 owner/broker 已接入 committer，UXP 查询完成事件后释放临时资源；仍非生产 owner 注册、动态页或整个 S6/S7 通过。见 [S6 持续证据](../验证/RENDER-01-S6普通页原子提交与恢复验证-20260920.md)。

2026-09-20 最新覆盖：现有 owner/session/broker/UXP main 已接通领取、ACK、真实资源临时文件、普通页 Photoshop 执行和认证 JPG/PSD 暂存回传。run `610483f50bb74f82b908c0898f76544c` 有 263 次心跳，取消收口 exit 0、published=false；独立检查与正式 .NET/worker 校验通过 42 层/16 文字层、源 hash、目标嵌入/几何及 JPG/ICC。效应摘要仅规范化已验证的保存默认值与内建渐变本地化名称，真效果改动仍拒绝。见 [S4 持续证据](../验证/RENDER-01-S4普通页执行与输出回传验证-20260920.md)。下文未配对/main 未接是历史，不覆盖当前状态。

本轮工作范围：沿用现有 owner、AdapterClient、broker 和 stageResource 接通领取/ACK/资源生命周期，再实现普通页执行；依据旧成功插件的 Photoshop API 证据，不复制其宽权限或无认证握手。保持生产可用性门、源文件保护及未知结果不重提；外部依赖为现有 PS/UDT 和已登记的受控真实样本。

2026-09-19 已接管并验证既有 `BridgeWireV1`、`BridgeWireErrors`、`BridgeWireValidator`、`BridgePlanSubmissionValidator` 和 `BridgeWireValidatorTests`，没有重写为另一套实现。首次合同测试为 15 通过/3 失败，定位并修复合法拒绝响应在根 `body.oneOf` 的重叠问题；随后补齐错误字段边界、摘要大小写一致性、Application 提交服务和有界 UTF-8 读取器。

随后完成独立 `ps-adapter/v1` 机器 Schema/DTO、认证转录与 .NET/JS 向量、Kestrel 回环 listener、UXP client、单通道领取/ACK/事件账本、15 秒心跳断线处理和实际文件读取租约。继续接通提交前租约匹配、128 KiB 认证资源分块、结束/断线/取消/异常释放，新增 UXP 分块及完整资源 hash 校验模块；main/实际临时副本与执行仍未接入。本轮补齐 journal 16 MiB 有界读取、严格字段和事件/状态恢复校验，保留旧 v1 文件格式。最新 solution/探针 build 均 0 警告/0 错误；本轮分批 Contracts 112、Application 提交相关 42、Infrastructure Bridge 124、Architecture 26，共 304 项 .NET，最终插件 Node 22/22 通过。**S1 整体仍在进行中**，这些不是页面 checkpoint、真实握手或正式输出证据。

本轮原生 Computer Use 已成功，正式插件重新加载/重载，未打开 Photoshop 文档；完整 build 已确认 `26.8.1 (20250624.r.8 5e0d05d)`、UXP `uxp-8.3.0-uxp-b1d8c85`，随机源/AbortController 及 modal 函数兼容已确认。真实首次 fetch 仍报 `Permission denied to the url [loopback] Manifest entry not found.`，未收到 HTTP，也没有配对/认证心跳。显式端口、尾斜杠、host 形式、fetch wrapper、正常重启和原单主机声明对照均失败；已撤回无效端口枚举，恢复 `http://127.0.0.1` 最小声明，不使用 all 或放松认证。旧 Esc/undefined 记录保留为历史，不再列为当前原因。用户新增两张尺码图不是原缺失旧金标，不替换预期；SIZE/RENDER 真实输出门继续开放。

最新详见 [实机配对阻断与恢复账本验证](../验证/RENDER-01-实机配对阻断与恢复账本验证-20260919.md)。[S1-S3 候选验证](../验证/RENDER-01-S1-S3传输与资源候选验证-20260919.md)、[新增真实样本登记](../验证/RENDER-01-S0新增真实尺码样本登记-20260919.md)、[传输增补](../技术方案/RENDER-S1-UXP主动连接传输增补草案.md) 及先前 [S1 验证记录](../验证/RENDER-01-S1合同校验与提交边界验证-20260919.md) 保留此前依据。

继续执行新增：localhost-only 无秘密对照已到 HTTP 400，但 numeric URL 仍被 manifest 拒绝；UXP 拒绝自定义 Host，而且实测 `redirect:error` 对部分 GET/POST 跳转仍会跨端口跟随。单换地址的候选按安全门停止，见 [ADR-032](../架构决策/ADR-032-UXP回环主机名兼容候选.md)。配置已恢复 numeric、诊断实例已 Unload，未放宽认证/Host/Origin。新增 `BridgeSessionOwner` 生命周期核心并接入原探针，新增 `stageResource` 新临时文件的验证/释放模块；build/探针 0/0、Bridge 128/128、资源 Node 聚焦 23/23。生产分发/提交编排、main/实际 UXP 文件写入和真实配对仍未完成，不能把这些模块等同 S2/S3 退出。

下一步按当前顺序执行（替代历史网络定位待办）：

1. 普通页首次 JPG+PSD 的正式媒体验证、目标卷 staging、manifest/marker、SQLite checkpoint 与新进程恢复已有真实通过；继续覆盖重新生成/多页/故障边界，历史已取消/unknown 且没有提交 journal 的 run 不追认发布。
2. 实现受保护配对首次分发/轮换与桌面生产 owner 注册，完成生产 placement/旧冻结校准兼容、旧层替换栈执行与 JPG-only 独立结构证据；保留生产可用性守卫直到实际合同闭合。
3. 在 S5 前补齐动态尺码计划的校准目标/放置语义：当前 builder 的 `targets=[]`，通用栅格样式与实际模板尚未闭合；需 ADR-014 的显式兼容演进/迁移，不能由 UXP 猜位置或贴市场图；
4. 完成 S4/S5 剩余普通/动态页执行与 S6 暂停恢复，再运行 S7 真实编辑、视觉、故障矩阵、产品走查与 SIZE 联合验收；当前余项包含功能开发，不只是验收操作。
5. 另行进入 INSTALL-01 制作签名桌面制品和正式 `.ccx`，不得把 Developer Tool 加载包当作交付包。
