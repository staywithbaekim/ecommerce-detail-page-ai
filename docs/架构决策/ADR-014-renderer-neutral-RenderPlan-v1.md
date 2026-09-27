# ADR-014：renderer-neutral RenderPlan v1

## 状态

已批准并冻结语义合同。机器可读合同位于 `docs/技术方案/schemas/render-plan-v1.schema.json`；跨语言实现和真实 UXP 回归仍需契约测试与 `RENDER-01` 验收。

## 日期

2026-08-27

## 背景

桌面端需要把模板规则、资源选择、裁切/变换、尺码表和输出意图交给 Photoshop 执行，同时保留未来 JSX 或独立渲染器的替换可能。历史验证计划包含 UXP 路径和本地文件，不能直接作为产品合同。

## 决策

1. `RenderPlan v1` 只表达渲染语义：计划身份、模板修订、逻辑资源、文档画布、校准目标、替换栈、布局、尺码表和输出意图。
2. 正式合同是独立 JSON Schema，不以 C# DTO、UXP 私有 JSON 或 Photoshop 命令列表作为唯一来源。Schema 版本固定为 `render-plan/v1`，未知顶层/嵌套字段拒绝。
3. 资源只登记逻辑 `resource_id`、用途、MIME 和内容 hash。允许的用途包括 `template_copy`、`high_resolution_render_source` 和 `deterministic_size_table`；`analysis_copy`、缩略图和生成式图片不得作为正式渲染资源。
4. 所有目标使用 `layer_path_order=psd_bottom_to_top`，路径段为从底到顶的 1-based 同级序号。UXP 的数组倒序转换只属于适配器；路径、类型、可见性、剪贴、蒙版、透明度、混合模式、ID 和结构签名在替换前校验。
5. 替换栈使用逐层 `superseded_layers`，禁止恢复模糊的 `superseded_paths`。目标名称替换后的变化属于执行 evidence，不改变目标身份合同。
6. 普通套图页的 `size_table` 必须为 `null`；动态尺码表必须引用本地确定性栅格器生成的无损资源，不能直接贴市场尺码图或 AI 生成图片。尺码单元格保留来源原文，包括 `-`、`\\` 和 `/`。
7. 输出只允许文件名和逻辑 artifact ID，不允许绝对路径、临时路径、session token 或任意 Photoshop 命令。路径、句柄和覆盖确认在 bridge 执行信封中绑定。
8. `plan_digest` 对稳定语义计算，不能包含机器路径、端口、token、运行时版本或 adapter 私有字段。页面重生成必须创建新的 `plan_id`/`idempotency_key`，历史计划不可变。
9. 兼容策略只允许显式迁移：同一主版本可增加可选字段并提供迁移器；破坏语义的变化建立 `render-plan/v2` 和新 ADR。拒绝未知版本，不静默猜测字段。

## Schema 与校验门

### 2026-09-25 新计划的可识别输出名称

按PRD16.1及V1执行计划D7，新AI计划工厂使用冻结修订页面的页码/名称、必要的变体显示名称生成JPG/PSD共用基名，不再把款序号与内部variant_id当作用户文件名。普通页与动态尺码共用规则；每款一次的页不附加颜色名。名称不构成款式/页面/变体身份，目录仍由桌面发布层负责，本增补不声称按款发布已完成。

Windows非法字符、控制/方向格式字符替换，NFC规范化后按完整文本元素限制页名80及变体名48个UTF-16单元；页码用InvariantCulture。大小写不敏感、规范化/清洗/截断后的重名，基于同款选中页面及全部变体（含被阻断项）预留原名后按稳定ID排序分配数字后缀，避免抢占已有字面后缀名。最终叶子名称遵守既有160字符Schema限制，不新增命名表达式或UXP规则。

四种新计划身份preimage均纳入最终output_base_name，防止变体名/输出名称变更却复用原幂等身份。该字段只存在于本地哈希输入，不加入wire/Schema。已持久化journal/manifest/计划字节不变，恢复从原journal读取；Prepare发现既有journal直接返回，不能用新命名投影替换Unknown或旧输出。同事实单页重生成仍保留原文件名，不通过重生成隐式升级命名。

### 2026-09-21 相同冻结事实的单页重生成

`RenderPlanRegeneration` 只为选定逻辑页创建新的 plan_id、idempotency_key、artifact_set_id、created_at 和重算 digest。模板修订、资源角色/hash/长度、文档/变体、targets/绝对位置、动态尺码结构/插入及导出语义均保持原计划事实；修改选图或校准不通过该入口隐式完成。追加前比较剔除这些执行身份字段后的 JSON 语义，并对新计划重新做完整校验。普通页与动态尺码页沿用同一规则，不新增 UXP 命令或能力。历史计划字节不变，新 artifact 目录保留同一文件名但不覆盖旧目录。attempt 和精确执行范围属于桌面 Version=2 journal，持久化兼容见 ADR-012。

### 2026-09-20 输出变体身份兼容增补

新增可选 `document.output_variant_id`，只用于有独立颜色/规格输出身份的新计划，并与 `output/variant-identity-v1` 能力成对出现；.NET 与 UXP 在提交/ACK 前拒绝缺任一侧的计划。不含变体的历史计划保持原字节和 digest，不从文件名、顺序或图层猜变体。新工厂将变体纳入计划身份，每款一次的页面只生成一次，每变体页面分别生成；既有 plan/idempotency/artifact 身份不能复用。旧 adapter 不声明该能力时拒绝新计划。

输出 checkpoint 的逻辑页面键是款式/页面/可选变体，attempt 是该逻辑页的新执行次数；新 attempt 保留旧正式输出和历史记录，不能当作新成功页重复计数。数据库兼容见 ADR-012。该增补不代表批次编排或真实多变体验收已通过。

进入 Application/UXP 之前必须依次通过：JSON Schema、ID/摘要格式、资源引用闭合、目标结构证据、页类型与尺码互斥、输出文件名无路径逃逸、`plan_digest` 重算一致。任一失败返回结构化错误，不能开始 Photoshop 修改。

## 候选与取舍

| 方案 | 优点 | 风险 | 结论 |
| --- | --- | --- | --- |
| 独立 JSON Schema + 版本迁移 | 跨 WPF/UXP/未来渲染器，正反例可测试 | 需要维护版本和迁移器 | **采用** |
| C# DTO 作为合同 | 编写快 | UXP/未来渲染器被 .NET 类型绑死 | 不采用 |
| UXP 私有 JSON | 可直接执行 | AI/业务/Photoshop 细节泄漏，无法复用 | 不采用 |

## 影响

- `docs/技术方案/schemas/render-plan-v1.schema.json` 是提交前的规范合同；历史 `uxp-validation-plugin/render-plans/*.json` 仍是技术验证夹具，不自动宣称符合生产 v1。
- adapter 可使用内部 DTO，但必须在边界处验证/序列化为 v1，并通过正例、负例、迁移和跨语言测试。
- 执行 evidence 另存 `target_name_changes`、结构审计和 ICC 字段，不污染产品计划语义。

## 退出条件

若正式模板需要无法表达的语义，先新增兼容字段/版本和迁移测试；不得把 adapter 私有字段塞进计划或绕过 Schema。

## 2026-09-20 RENDER-S4 显式放置兼容增补

当前 `layout.intent/scale/offset/safe_bounds` 未定义源像素尺寸、最终绝对几何及裁切算法；手工校准预览与参考学习的 offset 来源也不同。不能让执行端据 intent 推测两者等价。本次按第 9 条增加可选 `layout.placement`，旧计划保持可读及原 digest；要求执行该字段的新计划显式声明 `layout/absolute-placement-v1`，每个 target 必须完整携带 placement。

placement 表达整张资源的、无旋转/透视/镜像的等比放置：`source_width/source_height` 为 Photoshop 解码后的像素尺寸，`left/top/width/height` 为最终文档像素中的整资源矩形。坐标可超出画布，但须与画布相交。`clipping=preserve_template_masks_and_canvas` 表示只保留已有模板蒙版、剪贴关系和画布裁切；不把 safe_bounds 当成新增蒙版，不自动 crop、fit 或猜主体位置。source/目标宽高比差允许百万分之一的十进制舍入误差；非目标层保持不变。执行端须校验实际源尺寸、无旋转/透视变换及最终矩形，不能覆盖为推测值。

`RenderPlanPlacementMigration` 只接收 Application 调用方提供的每个 frame 的显式几何；拒绝缺项、已迁移项以及复用 plan/idempotency/artifact 身份，重新计算 digest，保留资源和结构证据。它不修改已冻结模板修订或历史计划。旧计划能通过存储/读取合同不代表可执行；自动从手工/参考校准产生明确几何以及动态尺码空 targets 的迁移仍需分别接通，不能靠本增补关闭这些门。

### 普通页生产者的逐图放置（subject-placement/v1）

生产准备产生的布局检查事实必须与页执行结果分别持久化：原因为 `AI_RENDER_PLAN_VALID_LAYOUT_FALLBACK` 的页面，即使 JPG/PSD 已提交、应用重启恢复，也保留“待检查构图”。执行成功不清除主体定位/业务检查缺口；该事实属于桌面批次账本，不向 RenderPlan 或 UXP 注入业务判断。旧账本恢复见 ADR-012 的 v16 兼容增补。

`AiRenderPlanFactory` 的新 ordinary 计划要求 `layout/absolute-placement-v1`，由 Application 使用扫描登记的源像素尺寸、冻结校准区域和当前选图的主体归一化边界计算整图矩形。完整主体类意图在“不露白边”和“主体完整落在 safe_bounds”两组约束的完整交集内调整缩放与位置；允许细节裁切的明确意图只要求填充。基准缩放/偏移作为偏好，不施加任意的小范围移动限制。数学上无解、未知意图或参考变换不明确时输出 `TPL_RENDER_LAYOUT_REVIEW_REQUIRED`，当前页无可执行计划、结果标黄，其他页可继续。十进制运算仅容忍十亿分之一像素的端点舍入残差。

手工校准采用居中 UniformToFill 的基准；参考学习的旧 offset 来自原智能对象左上角，必须以冻结检查中的同 ID/路径、正向轴对齐 transform box 转换为中心偏好，并还原旧 scale 的 0.01–20 截断语义。不对旋转、透视或镜像作隐式解释。缺主体定位时以实际尺寸普通填充、保留待检查构图状态，不伪造主体定位；参考旧照片的偏移不用于这一回退。safe_bounds 仍不是新增蒙版，现有模板蒙版/剪贴及画布保持。

生产计划身份 preimage 纳入算法版本、源 hash/长度/MIME/像素尺寸和是否输出 PSD；旧计划不原地升级，底层 builder 的旧调用模式保持可读。旧计划的执行迁移仍走显式迁移器；新生产者直接产生完整 placement。该实现本身不关闭实机、人工作图对照及产品构图门。

2026-09-21 多颜色准备修正：Application准备输入可选携带当前款各实际asset的已验证主体几何；主色与其他颜色分别按被选资源取值，不借用主色坐标，也不丢弃已存在的次色证据。映射必须属于当前款且key与AssetId一致，未提供映射的既有调用保留兼容。计划仍只保存经本地计算的placement，wire/schema、冻结计划、SQLite及UXP无变化；缺证据维持构图待检查，有证据但无解不生成可执行计划。实机混合批次及真实方图无解准备证据见 [S6/S7收口记录](../验证/RENDER-01-S6-S7稳定性与产品收口-20260921.md)。

## 2026-09-22 完整款模板兼容修正

普通页 `layout/absolute-placement-v1` 已明确最终整图为正向轴对齐矩形。正式 adapter 现在允许模板目标原先带纯旋转：先核对嵌入对象、正向矩形四角、无透视/剪切/镜像/非恒等 warp，再在目标副本上撤销该旧旋转，核验轴对齐，最后执行既有绝对放置。它不从旧角度推断商品布局；参考学习的旋转几何仍不隐式转换为布局偏好。动态尺码目标保留原轴对齐门。合同字段、资源角色、固定层和源模板均不变。命令无效或回读不符合矩形即失败，不导出成功结果。

`ExpectedLayerEvidence.NameBefore` 属于精确结构证据，保留 PSD 图层名的前后空白和制表符，仍拒绝空白名称和超长名称。PS 26.8.1 的 DOM 与 Action Manager 经真实只读核对均将 TAB 读为一个 SPACE，因此 adapter 仅将 expected 名称中的每个 TAB 映射为一个 SPACE 后逐字匹配；不 Trim、不折叠其他空白，路径、ID 及全部结构检查不变。独立 PSD 检查仍使用原名称。旧修订/计划不原地重写；修正后的整款测试使用新根、新修订和新计划。真实证据见 [完整款联验](../验证/RENDER-01-S7完整款素材联验-20260922.md)。

固定纯色矢量 shape 的非目标内容证据通过既有 `adjustment_sha256` 字段承载：只接收同时具有矢量蒙版与纯色填充块的 shape，对全部 tagged block 的 tag、长度和原字节排序摘要，包含描边、几何、未知块及元数据；不规范化名称或默认值。像素、蒙版、效果、bounds/结构和前后摘要比较仍要求完整通过。未知/不支持的 shape 保持 unavailable、禁止提交。该增补不改变 RenderPlan、媒体角色或序列化合同。

无编辑另存的 PS 26.8.1 副本证明两项效果保存差异，worker 仅对对应证据做兼容：禁用且 present=false 的 OrGl 中，ShpC 两点 (0,0)/(255,255) 线性等高线的“默认/线性”内建名称等价；旧版 BevelInfo v2 禁用、高光/阴影字节均在 127/128 且所有现代 ebbl 均证明禁用、present=false、两项严格为 Unit.Percent 50.0 时，缓存统一为 128。曲线、字段、单位、其他标签/百分比以及启用效果仍精确比较；仅旧版数据不得借此放宽。另补入 psd-tools BevelInfo.write 未正确序列化的 real_highlight_color/real_shadow_color，防止遗漏真实颜色变化。该修正只改变独立内容摘要计算，不改冻结计划、来源或输出字节；旧 Unknown 不追认，新任务使用新根和计划。

## 2026-09-20 JPG-only 独立结构验证能力

新生成的 JPG-only 计划声明 `output/verification-psd-v1`。它要求执行端生成只用于独立结构验证的临时 PSD，经认证 adapter 的 `verification_psd` 类型回传。该能力不改变 `output.editable_psd.enabled=false` 的交付含义，不新增正式 PSD 文件名或机器路径。旧计划保持原 digest，不静默添加能力；不支持该能力的 adapter 拒绝执行。

独立媒体验证仍核对完整 JPG/ICC、模板和输出 PSD 结构/内容/hash。只有全部通过才能提交 JPG。正式 manifest 的 files 不含 verification_psd；media receipts 保留其验证摘要。Verified checkpoint 绑定完整 manifest 摘要，恢复不重新渲染或将临时验证产物冒充用户请求的可编辑输出。真实限定证据见 [替换栈与 JPG-only 验证](../验证/RENDER-01-S4-S6替换栈与JPG-only验证-20260920.md)。

### 2026-09-24 历史 JPG-only manifest 读取兼容

P0 后新 JPG-only 生产不再输出或接收 `verification_psd`，但不得用新收据形状拒绝上述已持久化的旧 `render-page/v1`。历史读取/恢复仅额外接受“正式 files 恰为一个 JPG，media receipts 恰为该 JPG 加一个合法 verification_psd 摘要”的旧形状；不把验证 PSD 升格为正式文件，不要求已释放的临时 PSD 再出现。原 manifest 字节、摘要/长度、marker、正式文件集合/hash、精确页面身份及原 SQLite Verified/Committed 证据仍必须一致。未知/重复/不匹配收据、未登记及损坏产物继续拒绝；新 prepare/提交仍使用严格的新形状。无需改变 wire、schema、SQLite 或历史 digest，不重写旧 manifest，不自动重投。触发证据为原生恢复旧6015批次时七张预览均被新计数规则拒绝；聚焦兼容正例在修复前3项失败，修复后连同新生产/损坏负例38/38通过，原生复核随本轮桌面整合报告记录。

## 2026-09-20 动态尺码显式替换目标兼容增补

新增能力 `size-table/calibrated-target-v1`，用于模板本来就有明确尺码智能对象的情形；同时要求 `layout/absolute-placement-v1`。旧空 targets 计划保持原字节/digest 可读，但不得标记可执行。声明新能力的动态页只能有一个 target，其 asset 必须正好引用 size_table 的确定性 PNG，grid_digest 必须与该资源实际声明 hash 一致；不接受普通商品资源、市场 JPG 或另一张表。目标完整沿用 layer path/ID/结构签名/superseded_layers 证据，placement 必须等比且整表在 safe_bounds 内。

此初始替换能力只接受无蒙版/剪贴、全透明度、正常混合的尺码目标，祖先也不得造成裁切或未知效果；不通过复制商品校准、按名称查层或挑唯一智能对象填充合同。Application 的显式绑定额外核对模板修订、模板 hash、页面和栅格尺寸；独立输出检查仍核对嵌入 PNG hash、目标几何及全部非目标内容，manifest 增加可选尺码身份/结构摘要/栅格摘要/模板摘要证据，普通页旧 manifest 不补字段。

旧计划通过现有 `RenderPlanPlacementMigration.MigrateSizeTableTarget` 显式迁移，必须提供完整绑定和匹配 PSD 检查，创建新的 plan/idempotency/artifact 身份。UXP 仅执行替换，不解释单元格、不计算布局。

当前真实 `F-尺码表-【L-5XL】.psd` 的旧表由文字/背景组组成，唯一智能对象属于隐藏品牌组，**不适用此替换能力**。其显式旧组隐藏/新表插入、样式/区域校准及生产持久化入口仍需实现；不得用这一能力或合成测试关闭真实 S5 门。

### 无既有尺码智能对象的显式插入

新增可选 `size_table.insertion` 与能力 `size-table/insert-layer-v1`，同时要求绝对 placement。与 calibrated-target 能力互斥，targets 必须为空；缺少插入信息的旧计划保持原合同可读。只接受桌面端明确校准的根层锚点，在该层正上方插入一个命名的嵌入智能对象；锚点使用原 PSD 从底到顶 1-based 路径、ID、类型、名称和结构签名，不能按名称搜索。`hidden_layers` 显式列出需隐藏的根文字/像素/组层，子层自身可见性及内容不改；不删除旧层。范围不含其他模板、页或款式。

插入位置采用完整 PNG 的等比绝对矩形，必须完整位于校准 safe_bounds。新层保持 normal、opacity/fill 255，无剪贴、蒙版和效果。插入后的根层序号变化可由锚点精确推导；独立输出检查按原 ID 验证全部已有层，仅允许登记根层的 visible 变 false、其后代 effective_visible 变 false，以及锚点上方根路径整体加一。新增层只能有一个，其名称、嵌入资源 hash 和矩形必须与计划一致。此增补不提供自动校准、不选择隐藏品牌智能对象，也不将市场图作为资源。

### 桌面尺码区域校准与持久化

模板页增加可选 `SizeTableCalibration`（size-table-template/v1）：明确的根层锚点与待隐藏旧表根层、原模板 SHA-256/结构签名、表格安全矩形和栅格字体/颜色/行高/留白。入口要求用户选择图层；不根据名称、唯一智能对象或旧图内容自动推断。该规则固定为“完整等比缩放并在所选区域居中”；Application 以每次实际确定性 PNG 的尺寸计算 placement，UXP 仍只执行绝对几何。数据行数变化只改变本次几何，不覆盖冻结模板或原计划。

SQLite v14 给现有 template_pages 增加 nullable 校准 JSON；旧修订/旧批次不补默认校准，不修改历史 digest。新草稿校准随修订继承、重排、复制和模板包 v2 可选字段保留；模板包版本与索引迁移见 ADR-034，不改变冻结 v1。commit manifest 纳入校准文件长度及 SHA-256，使新 rule digest 覆盖该规则。模板包仍严格拒绝未知字段，并检查校准与模板 hash/结构/区域相符。切换为普通页时移除草稿尺码规则。没有校准的旧动态计划仍为 BusinessBlocked，不因模板成功保存而宣称可执行。
