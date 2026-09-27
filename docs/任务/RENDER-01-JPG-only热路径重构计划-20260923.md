# RENDER-01 JPG-only 热路径重构计划（2026-09-23）

> **历史归档说明（2026-09-27）**：本计划记录旧性能样本和专项优化过程，仅用于证据追溯；不作为当前 V1 性能结论或交付前置。当前口径见“RENDER-01 / PERF-01 时间基线收口（2026-09-27）”。


## 结论

> **2026-09-24 执行边界（性能生命周期专项）**：P0、P1 页面级 modal 与生命周期热路径七页限定门已通过。生命周期r2有效耗时79.769秒，相对P1的136.565秒单次样本少41.59%；不扩大modal、模板副本复用、页间重叠或P2。下一步先加固本轮发现的偏好恢复记录损坏/诊断边界，再推进主线原生整合，不猜测修改Photoshop业务逻辑。

专项测量分段记录：冷启动/listener 启动、认证握手与 ready、热运行页面执行、输出 ACK/独立验证、原子提交与释放。不得把连接等待和页面执行混为单一指标。

2026-09-24 接续：UXP工作查询与5秒普通心跳解耦，资源清理ACK保持；r2已完成7/7提交、独立核验、与P1七页RGB像素一致及新进程0心跳恢复，21正式文件和64保护文件不变。原始478.751秒/首次ready与排障398.982秒单列，ACK合计20.350→1.195秒。r1 Unknown不重投、已完成r2不重跑；有证据的一次性偏好记录隔离不等于产品自动恢复已实现。以 [专项交接](RENDER-01-性能生命周期审计交接-20260924.md) 为唯一当前入口；完整10款PERF-01仍开放。

模板导入/模板修订提交时已经完成 PSD 解析、图层绑定、旧层关系、结构签名和布局校准，并将证据持久化到 `template_resources.inspection_json`、模板 revision 和页面 frame bindings。选择一个已经提交的模板组时，只需要读取当前 revision；生成批次不应再次做完整 PSD 解析或重新发现替换图层。

当前生成阶段的重复解析来自安全实现过度保守，而不是业务上仍然需要解析：

- `RenderBatchResourcePreparer` 每页再次调用 `resources.inspect_template`；真实 7 页批次累计约 62.86 秒。
- JPG-only 的 `RenderMediaValidator` 每页再次做 `media.inspect_baseline` 和 `media.inspect_output`；真实批次累计约 66.80 秒和 31.77 秒。
- UXP 在 JPG-only 仍保存 `verification_psd`，并执行完整结构审计，增加 PSD 写盘、读取、worker 解析和内存压力。

这些步骤解释了当前 `A馨妈6015-58` 7 页 JPG-only 有效批次 `367.918s` 中的大部分非 Photoshop 业务成本。它们应从 JPG-only 默认热路径移出，但不能删除源文件保护、哈希、原子提交、失败隔离和恢复。

## 目标流程

### 模板组导入/提交（一次）

1. 复制受控 PSD，计算长度和 SHA-256。
2. 运行 PSD inspection，保存 canvas、layer path/id、smart object 类型、结构签名、旧层和内容证据。
3. 完成 frame binding、图片框校准、尺码校准和模板 revision commit。
4. 形成不可变模板证据：`revision_id`、`resource_id`、`sha256`、`byte_length`、`structure_signature`、`frame_bindings`、`calibration_evidence`。

模板 PSD 发生变化、哈希变化或证据缺失时，阻断生成并要求重新导入/提交模板；不在生成页偷偷回退到 PSD worker。

### 批次准备（每批次一次）

1. 使用已提交 revision 创建 batch snapshot。
2. 完成 AI 选图、用户确认、页面选择和资源哈希核对。
3. 生成并冻结 renderer-neutral `RenderPlan`，其中包含已知 layer path/id、旧层和 placement 参数。

### JPG-only 页面生成（默认）

1. 用 `revision_id + resource_id + sha256 + byte_length` 做廉价身份核对；复制受控模板副本并校验字节哈希。
2. 打开受控副本，按 RenderPlan 的已知绑定直接替换 smart object 并定位。
3. 导出 JPG。
4. 只做 JPG 字节哈希、解码、尺寸和 sRGB ICC 检查。
5. 原子提交页面 manifest、marker 和 checkpoint，关闭文档且不保存。

失败页从同一个冻结 RenderPlan 单页重做，不重新 AI、不重新解析模板、不影响已提交页。

### 用户主动需要 PSD 时

沿用现有 `editable_psd.enabled=true` 作为严格路径：保存正式 PSD，并执行完整 PSD 结构/可编辑性检查。第一阶段不新增另一套模式字段，避免无必要的协议迁移；将来若需要独立诊断模式，再单独设计 `validation_profile` 或 `render-plan/v2`。

## 改动分级

### P0：必须先做

**2026-09-23 实施状态：代码与自动化子门通过，真实 Photoshop 复测待执行。** 资源准备读取已持久化 inspection，并校验计划/批次快照绑定、resource id、长度、SHA-256、结构签名和 target；JPG-only 验证只接受 JPG receipt 并执行字节 hash、完整解码、画布尺寸及 sRGB ICC 检查，不调用 PSD inspector；JPG-only 不生成 `verification_psd`，UXP 仍保留文档身份、canvas/RGB/8-bit、已知 target、几何和旧层检查。`editable_psd.enabled=true` 仍保留正式 PSD 保存、完整结构审计及输出 inspection。原子 manifest/marker/checkpoint、源文件只读保护、失败清理/隔离和恢复合同未放宽。

聚焦自动化验证：Infrastructure 提交/恢复 24/24、临时文件账本 15/15、UXP Photoshop/输出 66/66；`git diff --check` 通过。没有重跑 148 项测试，也没有执行真实 Photoshop 批次。2026-09-23 已新建隔离根 `tmp/r01-render-perf-20260923-p0-post` 并完成 7 页计划准备（批次 `batch_9901f92a1e7a4d8cb86edc9a2fc41aed`），但当前会话无法访问 Windows Computer Use，未能连接 UXP；probe 已停止，批次保持 Planned、输出文件为 0。未接管或关闭现有桌面/Photoshop 进程。需在可用的原生 UI 会话连接 Photoshop 2025 后从该隔离根继续；既有 367.918 秒实测是重构前数据，不能作为 P0 后性能结果。

2026-09-24 首次真实 Photoshop 复测已启动但未通过：新隔离根 `tmp/r01-render-perf-20260924-p0-r3` 的 `page_front` 在 `execution_accepted/page_started` 后发生 `heartbeat_timeout`，批次安全收口为 `RecoveryRequired`，第一页 `Unknown`、其余页 `Planned`、正式输出0、资源副本2、输出part0。未自动重投未知 execution；该根保持原状，不盲目恢复或复用。

2026-09-24 第二次真实 Photoshop 复测已证明连接链路稳定但仍未通过页面门：新隔离根 `tmp/r01-render-perf-20260924-p0-r5` 的批次 `batch_2c7e625180f04c6bbf2bead743c60cb2` 认证心跳94次，3/7页（`page_front`、`page_back`、`page_size`）Committed，4页以 `BRIDGE_PLAN_INVALID` 失败，形成3个JPG、0个PSD且资源副本/输出part为0。失败根因是 JPG-only known-target preflight 错误拒绝 `descriptor.group=true` 的裁剪旧智能对象；已在 `uxp-render-adapter/src/photoshop.js` 修复，并由新增回归测试覆盖。UXP Node 测试236/236、build成功。需重新加载修复后的 `dist` 插件并建立全新P0根复测；P0真实门通过前不进入P1/P2，不复用r5 execution。

2026-09-24 第三次真实 Photoshop 复测通过 P0：全新隔离根 `tmp/r01-render-perf-20260924-p0-r6` 的批次 `batch_88209d1eef93436d9f7b5f9300e19632` 为 `Completed`，7/7 `Committed`，162 次认证心跳；7个JPG、0个正式PSD、0个verification PSD，资源副本和输出part均为0。独立JPG hash/尺寸/sRGB ICC/manifest/marker及输入资产hash核验通过。批次193.732秒，其中首次连接等待56.517秒，排除连接等待后的有效批次约137.215秒。P0真实门现已关闭，按本计划进入P1：先以该报告作为基线，评估减少UXP往返和同批次模板副本复用；P2仍只在P1测量后实施。

2026-09-24 P1 第一小步已落地：`uxp-render-adapter/src/photoshop.js` 将同一页面的多个 target 合并到一个页面级安全 `executeAsModal`，target 之间保留认证 pulse 和取消检查，所有 per-target 的 layer identity、replace、transform、placement verify、superseded hide 仍逐项执行；严格 PSD 路径的结构审计、保存和关闭边界未移除。新增多 target 严格 PSD 回归，覆盖两层、两次替换、单一 replacement modal、target 间 pulse 和完整输出/关闭。UXP Node 全套 `237/237` 通过，UXP build 成功。当前未改直接按 layer id 的 Action Manager 语义，待真实 Photoshop 测量后决定。

P1 第一项实机验证已完成：全新隔离根 `tmp/r01-render-perf-20260924-p1-r1` 的批次 `batch_6aaa27c74fab45a2bba1f6695772cce6` 为 `Completed`，7/7 `Committed`，157 次认证心跳，7个JPG、0个PSD、资源副本/输出part均为0；独立 manifest/marker、JPG hash、尺寸和3144-byte sRGB ICC核验通过。排除本次272.533秒连接/ready等待后，有效批次136.565秒，相比P0有效137.215秒仅少0.650秒，不能宣称显著性能收益。该项关闭 P1 第一项真实行为门；本批7个模板resource hash均不重复，模板副本复用暂无可测收益，保持现有生命周期；页间重叠和P2暂不实施。详见 [P1 页面级 modal 实机测量](../验证/RENDER-01-JPG-only-P1页面级modal实机测量-20260924.md)。

1. **资源准备不再逐页 PSD inspection**
   - 文件：`src/Desktop.Infrastructure/Bridge/RenderBatchResourcePreparer.cs`
   - 从 `ITemplateResourceRepository` 读取已经持久化的 `ImportedTemplateResource.Inspection`。
   - 只校验 revision、resource id、长度、SHA-256、structure signature 和计划中的 target 事实；缺证据直接阻断。
   - 删除生成阶段的 `resources.inspect_template` 调用。

2. **输出验证拆成 JPG-only 和严格 PSD 两条路径**
   - 文件：`src/Desktop.Infrastructure/Bridge/RenderMediaValidator.cs` 及其调用方。
   - JPG-only 只接收一个 JPG receipt，不打开模板/PSD，不调用 PSD worker；保留 hash、解码、尺寸、ICC 和 manifest 一致性检查。
   - `editable_psd.enabled=true` 保留现有 baseline/output PSD inspection 和结构对比。

3. **JPG-only 不生成 verification PSD**
   - 文件：`uxp-render-adapter/src/photoshop.js`、`RenderTemporaryFileLedger.cs`、`RenderPageCommitter.cs`、输出 reservation/manifest 合同及相关测试。
   - 只有 `editable_psd.enabled=true` 才执行 `saveAs.psd` 和登记 PSD receipt。
   - 新 JPG-only 正式输出应为 1 个 JPG、0 个正式 PSD、0 个 verification PSD 临时文件。
   - 保留关闭不保存、临时资源归属和失败清理。

4. **UXP 快路径取消完整结构审计**
   - JPG-only 仅保留文档身份、canvas/RGB/8-bit 和已知目标 layer 的轻量 preflight。
   - 严格 PSD 路径继续保留结构审计。
   - 不删除替换后的必要错误检查；先保留 placement 结果检查，再用真实 PS2025 测时决定是否进一步合并。

### P1：P0 复测后再做

1. **减少 UXP 往返**
   - 在一个页面级安全 modal 中批量执行多个 target 的 replace、transform 和旧层隐藏。
   - 评估直接按 layer id 操作，减少 `select + targetEnum` 和重复树遍历。
   - 保留取消安全边界、页级失败隔离和必要的 placement 检查；不能为了吞吐量引入未知执行结果。

2. **模板副本按批次复用**
   - 同一 revision/hash 的模板在一个批次只复制/校验一次，多个页面共享只读受控副本或受控 lease。
   - 先确认当前 bridge/资源归属能覆盖整个批次，再改生命周期；不直接把页面资源释放语义改成全局缓存。

3. **页间准备与验证重叠**
   - Photoshop 仍保持单文档串行。
   - 在当前页进入 Photoshop 后，受控地预备下一页资源；输出 JPG 的轻量验证可与下一页资源准备重叠。
   - 用明确的 page-completed/ACK 事件减少不必要轮询，保留断线查询和 RecoveryRequired 语义。

### P1-Lifecycle：当前唯一执行项

1. 绘制 listener、owner、session、broker、UXP ready、heartbeat、page ACK、output validation、commit、dispose 的实际调用链。
2. 统计每批次/每页的启动次数、ready等待次数、状态查询次数、heartbeat次数、ACK等待次数及耗时。
3. 区分可删重复调用与必须保留的安全检查：模板已提交证据不重新建模；正常批次不全量扫描旧层/非目标层；未知执行、模板修订/哈希变化、执行器变化、恢复/重试时才升级验证。
4. 只修复实测证实的生命周期瓶颈，保持 Bridge、RenderPlan、checkpoint、manifest、原子提交和 Unknown 不自动重投合同。

已发现的代码信号：Coordinator 在每页进入 Photoshop 前轮询 IsReadyAsync；批次 Session 的 IsReadyAsync 每次读取 owner 状态；每次 OpenAsync 创建并启动一个 owner/listener。需确认这些是否重复触发，以及 Observe/output validation 是否与 page_completed ACK 重复等待。

### P2：最后再考虑

- 同批次相同 source hash 的图片复制去重。
- 下一页资源预取和输出目录 I/O 小幅优化。
- 只有阶段数据证明仍有必要时，才优化 ICC、日志或非关键 UI 刷新。

## 明确保留

- `RenderPlan` 的 renderer-neutral 边界、plan digest、资源用途和 SQLite checkpoint。
- 源 PSD、源照片、市场尺码图和既有正式输出保护。
- 受控副本、资源/输出 hash、sRGB ICC、尺寸、原子提交和 marker。
- 页级失败隔离、取消安全边界、断线查询、未知状态恢复和已完成页不重复渲染。
- 严格 PSD 模式下的完整结构、可编辑性和尺码页独立检查。
- AI 和产品规则留在桌面端，不下沉到 Photoshop/UXP。

不采用旧参考包 JSX 直接替换当前链路；只借鉴其短热路径，保留当前项目的恢复和文件安全合同。

## 实施顺序与验收门

1. **已完成（代码/自动化限定子门）**：P0 代码和合同变更；只运行受影响的 Infrastructure/UXP 聚焦测试，不重复 148 项测试和既有埋点。
2. **自动化断言通过**：
   - JPG-only 不调用 PSD inspector；
   - JPG-only receipt 只有 JPG；
   - JPG-only 不创建 `verification_psd.part`；
   - 严格 PSD 路径行为保持不变；
   - 缺失或哈希不匹配的模板证据在进入 Photoshop 前失败。
3. **待实机**：隔离批次与数据根已创建（`tmp/r01-render-perf-20260923-p0-post` / `batch_9901f92a1e7a4d8cb86edc9a2fc41aed`），7 页计划已准备但仍为 Planned；本会话缺少 Windows Computer Use 控制面，未能连接 UXP，故没有开始渲染。恢复后只继续此隔离根，不复用旧正式输出；先确认 Photoshop 2025 窗口无用户文档，再连接已安装插件。
4. 真实门要求：7/7 Committed、7 JPG、0 PSD、ICC/hash/marker/checkpoint 通过；生成期 `resources.inspect_template`、JPG-only `media.inspect_baseline`、`media.inspect_output` 和 verification PSD 均不再出现。旧性能报告所记录的 verification PSD 属于重构前批次，保留为历史证据。
5. 生命周期专项完成并取得前后对比后，再决定是否实施模板复用、页间重叠或 P2；无证据不得实施。
6. 更新 `docs/当前项目状态.md`、V1 计划、验收矩阵和性能实测记录；在真实 10 款/约 70 JPG 证据前，`PERF-01` 不宣称关闭。

## 非目标

- 本计划不重新进行 AI 选图、不重做模板校准、不修改 148 项测试证据。
- 本计划不删除所有验证，不把失败页自动标记为成功，不放宽源文件或输出安全边界。
- 本计划不直接替换 bridge 协议，不并发打开多个 Photoshop 文档，也不引入云同步、上传或 PSD 长期缓存。
