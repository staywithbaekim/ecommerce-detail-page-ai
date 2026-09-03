# RENDER-01 正式 Photoshop/UXP 渲染适配器纵向实现任务书

## 1. 任务信息与批准门

| 项目 | 内容 |
| --- | --- |
| 任务 ID | `RENDER-01` |
| 任务名称 | 正式 Photoshop/UXP 渲染适配器纵向实现 |
| 状态 | **已批准建立任务书；待 RENDER-S0 前置核对，尚未开始正式 UXP 生产实现，不能宣称 RENDER-01 已完成** |
| 创建日期 | 2026-09-03 |
| 更新日期 | 2026-09-03 |
| 执行基线 | `codex/desktop-foundation-01`，`ebd2c14fff9745e37eb3b5a1f2286e0738b9cdfa` |
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
- 同一阻断连续复现两次，停止 workaround 链，回到批准合同或记录新的 ADR，不通过放宽安全门继续。

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

当前只完成任务书和交接合同准备，不代表 RENDER-01 已开始实现。SIZE-01 当前仍为进行中，剩余真实动态尺码 JPG/PSD 联验收和 125%/产品视觉证据不能被本任务书替代。

下一步按顺序执行：

1. 在办公室 Photoshop 2025 `26.8.1` 上先按 SIZE-S7 操作卡完成真实动态尺码页验证，确认模板副本和输出目录；
2. 复核 RENDER-S0，确定正式插件命名/ID/最小权限和本轮 RENDER-01 实现窗口；
3. 执行 RENDER-S1 至 S3 的正式 adapter/bridge/资源绑定实现和离线合同测试；
4. 在办公室实机执行 RENDER-S4/S5，再完成 S6 故障恢复和 S7 治理收口；
5. 另行进入 INSTALL-01 制作签名桌面制品和正式 `.ccx`，不得把 Developer Tool 加载包当作交付包。
