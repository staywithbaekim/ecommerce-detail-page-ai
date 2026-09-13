# ADR-022：参考学习 AI 语义合同与日常整组匹配唯一事实源

## 状态

已批准并实现。2026-09-08 reviewed diff 后修订：`reference-learning-ai/v2` 只负责学习阶段的动态语义生成；日常目标整组匹配只由既有 `ai-analysis/v1` 生产链路产生并计费。该决策关闭版本化参考语义和生产投影代码子门，但不关闭 `TPL-LEARN-01`；不同目标款真实素材以及受影响的 `LEARN-S4` 至 `LEARN-S7` 仍需重验。

## 日期

2026-09-08

## 背景

`reference-learning/v1` 已能保存本地 PSD 结构、参考照片身份和几何，但 fix16 的用途文字来自本地提示，不能证明未来目标款会按姿态、景别、构图或遮挡等实际差异选对图片。现有 `AI-01` 已提供 Credential Manager、用户授权、WIC 有界分析副本、供应商适配器、费用记录、缓存和未知结果边界；参考学习应复用这些能力，同时避免静默改变 v1 会话语义或突破每款最多三次逻辑请求的成本边界。

## 决策

1. `reference-learning-ai/v2` 与 `reference-learning-ai-result/v2` 只保留 `generate_selection_requirements` 操作。旧版本、未知操作（包括已撤销的 `match_target_group`）或错误结果一律在 HTTP 前或结果摄入边界拒绝，不回写为成功结果。
2. 本地结构、像素贡献、SHA-256 参考照片身份和 `LayoutIntent` 先闭合；学习阶段 AI 只负责开放式选图语义。日常目标候选排序和主体几何由 `ai-analysis/v1` 的 `product_joint_understanding` 负责；两条链路均不得根据图层名、文件名、尺寸或位置决定语义，也不生成商品图或 Photoshop 命令。
3. 一个参考模板组的全部页面先完成本地分析，再以一次逻辑 AI 请求生成全部图片框的动态语义。显式单页“重新分析”允许一次单页请求。这样六页模板不会产生六次供应商请求。
4. 本地路径、原始高清照片和 PSD 内容不发送给供应商，只发送整组照片的 WIC 有界分析副本。提交学习规则时，`FrameBinding.SelectionRequirement.Criteria` 持久化 `reference-audit/v1`、参考组 digest、完整参考内容 hash 集合以及语义来源/请求/结果/模型/提示词版本；SQLite 与 `template-package/v1` 均沿用 criteria 往返，旧无审计字段修订保持可读。
5. `AiAnalysisInputProjector` 在调度、授权准入、预算和 provider 之前校验学习绑定的审计元数据，并拒绝任何与参考内容 hash 相同的目标素材。审计字段参与 `requirements_digest` 和既有请求 fingerprint，但不会进入供应商的 `selection_requirement` 文本，也不会进入 `RenderPlan`。
6. 学习语义缓存键规范化包含参考素材逻辑 ID/内容 hash、参考组 digest、模型配置版本、提示词合同版本、结果 Schema 版本、压缩配置版本和逐框上下文。日常目标匹配继续使用 AI-01 的 request fingerprint、结果存储、cache、usage ledger、checkpoint 和未知结果边界；不建立第二套目标匹配缓存或账本。
7. 用户修改语义时保存 `user-override/v1` 覆盖并单向失效原 AI 缓存。重启或重新分析会重新应用覆盖；文字改变时既有复核结论回到待确认，文字不变时保留决定。
8. `ai-analysis/v1` 不静默改变 JSON 字段：`AiRequirementMatchV1.evidence_codes` 对学习绑定必须且只能包含一个受控匹配类别 `exact`、`safe_similar`、`material_difference` 或 `unsafe`。提示词版本升为 `ai-prompt/v2`，使旧缓存不会冒充新语义。
9. 目标预检门槛为：`exact` 置信度至少 `0.85` 直接为绿；不再用前两名分差机械降级
   `exact`。`safe_similar` 至少 `0.90` 为绿；候选分差或同分不单独降级。`exact`、`safe_similar` 或
   `material_difference` 至少 `0.60` 为黄；`unsafe`、低于 `0.60` 或无候选为红。只有显式
   变体/错款等语义冲突才额外阻断；缺少主体几何为黄；AI 不可用、未知结果、结果无分类或
   合同损坏是系统恢复状态，不伪装成业务红。正式修订见 ADR-030。
10. 同一目标照片允许被多个合理图片框选择。`subject_geometry` 是所选素材的主体事实，每个图片框再结合自身独立 `LayoutIntent` 生成不同的布局参数；人工锁定覆盖 AI 默认选择并由 selection revision/CAS 保护，不被异步刷新覆盖。
11. 授权、凭据、网络、超时、合同或分析副本失败均保留为可解释失败；未知结果不自动重试。没有用户完整手写语义时，失败候选标记 `AiUnavailable`，不能批量确认或提交为已学习 AI 语义。

## 影响

- `IAiServicePort` 的 v2 方法只服务参考语义生成；无人调用的 v2 目标匹配命令、服务实现和测试已删除，避免重复事实与重复计费。
- renderer-neutral `RenderPlan` 边界不变；日常路径为 `BatchDraft/asset-manifest → AiAnalysisInputProjector → AiAnalysisScheduler → AiBatchAnalysisWorkflow → 人工锁定 → AiRenderPlanFactory`，RenderPlan 只含目标批次资源与确认事实。
- WPF 同时使用本地学习服务和 AI 语义服务；AI 持久化后的文档由本地会话服务按持久化时间与 digest 安全接管，避免首次复核被误判为 stale。
- 本次真实调用只证明参考组动态语义链路；仓库没有不同目标款真实夹具，因此不得宣称目标挑图准确率或 `TPL-LEARN-01` 整体通过。

## 验证

参考语义真实调用证据见 `docs/验证/TPL-LEARN-01-LEARN-S3-AI动态语义验证记录.md`；生产投影与唯一事实源证据见 `docs/验证/TPL-LEARN-01-生产目标匹配链路验证记录.md`。

## 2026-09-11 兼容性补充：逐框上下文提示词 v3

本轮参考学习提示词由固定通用上下文扩展为逐框图层名、布局角色、画幅和位置上下文。为避免已经持久化的 v2 语义结果被误当作新合同结果，`ReferenceLearningAiProtocol.SemanticPromptVersion` 固定为 `reference-learning-semantic-prompt/v3`。该版本进入请求/结果元数据、cache fingerprint 和可复用性检查；旧 v2 cache entry 保留用于审计但不可复用。模型配置、分析副本、费用上限和 `AiResultUnknown` 边界不变。

供应商提示词把该上下文定义为结构/版式线索而非语义捷径：模型不得仅凭文件名、图层名、尺寸或位置臆测，必须以参考照片可见内容和图片框实际呈现为证据。

验证为离线代码/合同回归，不关闭不同目标款真实夹具和 LEARN-S4 至 S7 验收。

## 2026-09-12 兼容性补充：图片绑定闭合与类别提示词 v5

真实界面复核发现，源目录中未被 PSD 使用的照片仍可能进入旧参考语义请求，并且模型可能把该照片
内容写到另一个有效框。当前请求只从最终有像素贡献的 PSD 框反向收集照片；调用供应商前必须闭合
`ReferenceAssetId`、相对路径、源照片 SHA-256，且 `Exact` 框的嵌入 payload hash 必须与源照片一致。
任何不一致均在 HTTP 前失败，不创建或上传多余照片的分析副本。

同时移除逐框上下文中的图层名、画幅和位置描述，`selection_requirement` 只保留未来目标款可复用的
图片类别。`SemanticPromptVersion` 升为 `reference-learning-semantic-prompt/v5`，旧 v3/v4 缓存保留用于
审计但不可复用。该修正不改变原始 PSD/照片只读、一次整组请求、费用、180 秒未知结果和不自动重试边界。

## 2026-09-12 兼容性补充：日常整组分析完整短别名 v3

真实新建批次证明，日常 `ai-analysis/v1` 虽已兼容 `img_n` 返回值，却仍要求多模态模型逐字复制长
request/model 标识、随机 `learn_frame_*` requirement ID 和真实 `asset_*` ID。供应商已经返回并计费，
但结果因 `AI_PROVIDER_RESPONSE_REFERENCE_MISMATCH` 被安全门拒绝。旧响应未保存具体错误字段，因此不追认
某一个 ID；实现必须同时消除四类长引用的模型抄写风险。

供应商侧从本版本起只看到 `request_0`、`model_0`、`req_n` 和 `img_n`。每张图片的 `img_n` 标签必须
紧邻位于该图片之前，动态 JSON Schema 只允许本次请求范围内的短别名。结果返回本机后，适配器严格映射
回真实 ID，再执行既有语义、重复、越界和引用闭合验证；任何未知、冲突或归一化后重复仍失败关闭。日常
提示词/cache 合同升为 `ai-prompt/v3`，旧 v2 cache 不复用。该修复不改变一次整组请求、24 图、16 MiB、
180 秒、预算、未知结果不自动重试和 renderer-neutral `RenderPlan` 边界。

验证证据见 `docs/验证/TPL-LEARN-01-20260912-日常批次引用别名修复验证记录.md`。旧终态批次不可重发；
必须使用新包建立全新 UI 批次完成逐框业务验收后，才能更新 LEARN-S4 至 S7 状态。

同日真实供应商探针以 24 张目标款照片和 11 类规则完成 1 次请求：112,808 ms、11 条匹配、
error=`none`，没有引用不闭合或 180 秒 unknown。该结果关闭真实 API 传输闭合子门；WPF 逐框候选与
绿/黄/红状态、人工锁定和生成前业务复核仍需在全新 UI 批次中完成。

## 2026-09-13 兼容性补充：exact 直接绿色与连续人工替换

产品负责人批准后，`exact >= 0.85` 直接视为可用 Top-1；同分或候选接近不再单独构成黄色。
`safe_similar` 的替代门槛、`material_difference`/`unsafe` 风险门和显式 `variant_conflict`
阻断保持不变。自动批次的 AI Top-1 默认显示“AI 推荐 · 已确认”，只有主动“替换用图”才
进入人工锁定流程。该行为、语义类别显示和重复 `selection_revision` 的持久化规则以 ADR-030
为准；旧文字仅保留作历史审计，不得据此恢复逐框确认或同状态账本重写。
