# TPL-LEARN-01 S1-S6 连续实现验证记录

## 1. 结论

截至 2026-09-05，`TPL-LEARN-01` 的 LEARN-S1 至 S6 已形成正式产品代码候选并通过本机自动化门。WPF 已接通“从人工结果学习”的逐框复核入口；多页面学习结果可经现有模板工作区一次性写入 SQLite、revision manifest、`ready.marker` 和 `committed.marker`，并在重启后恢复。

本记录不把任务标记为整体通过。真实样本的结构候选已按替换栈折叠为 11 个可人工复核图位，全部与参考照片精确匹配；其中成分图虽然较小，但实际占据图片框，不能仅按尺寸判为固定。另有 25 个可见旧替换层和 4 个隐藏智能对象不进入候选。11 个候选仍需产品负责人逐框确认；LEARN-S7 的真实产品走查、6 页完整复核和 reviewed diff 交接仍开放。正式 Photoshop JPG/PSD 输出属于 `RENDER-01`。

## 2. 输入与安全边界

- 源照片归档：`新建文件夹.zip`，51,740,515 bytes，SHA-256 `64993e1230d74dc3c06f05e7b58d4fcb237345e0d61d167b07705f2339776fed`。
- 人工完成 PSD 归档：`奶奶新模板.zip`，440,185,511 bytes，SHA-256 `4febd3beaca81088af0dc43f4ea28d9f5b1b1829abf2df5640f3ff6d234fa98e`。
- 真实样本只在 Git 忽略目录的应用自有副本上检查；原始 ZIP、PSD 和照片未修改、未覆盖、未提交。
- 6/6 PSD 可解析；40 个智能对象均为嵌入式。原始检查报告的 36 个可见智能对象经结构折叠后为 11 个候选；25 个 visible superseded 旧替换层和 4 个 hidden 对象保留在审计中但不登记为图片框。11 个候选均按内容 SHA-256 精确命中源照片，含右下成分/水洗唛图位。
- 图层名和文件名只用于显示与审计，不参与图片框用途或照片匹配判定；没有调用图片生成、生成式填充、重绘或内容补全。

### 结构折叠复核（2026-09-06）

此前“36 个可见候选”实际是 36 个 raw visible smart object，不是 36 个图片框。学习服务现按检查器的结构规则逐父级处理：可见且面积不少于画布 1% 的连续重叠智能对象组成 replacement stack，只取最上层；其余层保存为 `superseded_layers`。真实样本的 6 页合计为 11 个结构候选，全部进入人工复核；`G-详情-05-细节.psd` 路径 `4` 的成分图虽为 239×252，仍实际填入图片框，不能仅凭尺寸自动 `Rejected`。25 个 visible superseded 层和 4 个 hidden 层不再出现在图片框列表中。`FrameBinding` 投影会逐层带入替换栈证据，避免输出时旧图残留。

## 3. 实现证据

### LEARN-S1 至 S3

- 隔离 PSD worker 只对嵌入式智能对象 payload 计算 SHA-256；外链不解析、不打开。
- 参考照片端口以只读、顶层目录、有界数量/大小方式读取，记录长度、内容 hash、MIME 和尺寸；文件名不是匹配证据。
- 候选保存页面身份、稳定 frame ID、层路径、结构签名、蒙版/剪贴、transform、匹配状态和 `LayoutIntent`。跨 PSD 重复 `layer_id` 通过页面 + layer + path 稳定摘要隔离。
- 初始语义保持空白；用户明确填写用途并选择参考照片后才能接受。伪造的空语义 Accepted 候选不能投影为规则。
- `reference-learning/v1` 会话不保存参考照片绝对目录、媒体字节、token 或 Photoshop 命令；待复核的空语义状态可以通过 Schema 原子保存。

### LEARN-S4

- T-02 的“从人工结果学习”入口已启用，要求先选择制作参考 PSD 时使用的源照片文件夹。
- 复用 T-04 三栏校准编辑器，显示页面、PSD 画布、候选框、参考照片、匹配证据、置信度、用途、缩放和偏移。
- 支持确认商品框、保留固定模板内容、退回拖拽校准、重新分析、取消和重启恢复。
- 所有受影响普通页逐框进入 `ReadyToCommit` 前，保存按钮保持禁用；1100×760 窄版切换可用。
- 照片预览以 `OnLoad` 内存方式读取并立即释放源文件句柄。
- 保存前再次核对每页应用内 PSD 长度/hash 和参考照片目录 digest；来源变化时不提交旧匹配或坐标。

### LEARN-S5

- 多页面文档先统一投影为 `FrameBinding`/`LayoutIntent`，再调用一次既有 `TemplateWorkspaceService.CommitDraftAsync`。
- 学习绑定以 `reference_only=true` 标记参考身份。`TemplateRenderPlanBuilder` 对这类绑定强制要求目标批次显式选图，生成的计划不包含参考素材 ID。
- 真实 `SqliteDesktopStore + JsonManifestStore + JsonReferenceLearningSessionStore` 集成测试证明：多页绑定、manifest、ready/committed marker 和 Committed 会话可在重启后恢复；旧修订保持不可变。

### LEARN-S6 故障矩阵

| 场景 | 结果 |
| --- | --- |
| 无匹配、外链、不支持结构 | 保持待复核或转人工校准，不自动提交 |
| 会话首次写入失败 | 返回 `TPL_LEARN_SESSION_WRITE_FAILED`，未持久化候选不进入活动内存 |
| PSD 内容同长度变化、照片集合变化 | 严格恢复返回 `TPL_LEARN_SOURCE_CHANGED` |
| manifest 或 ready marker 写入失败 | 真实 SQLite 中旧修订仍为 current，带绑定草稿可恢复 |
| SQLite revision 提交失败 | 旧 current 不变，草稿保留；由既有模板工作区故障注入覆盖 |
| committed marker 写入失败 | SQLite 已提交状态可识别；重复命令补写 marker，不创建新修订 |
| 学习会话 Committed checkpoint 校验/写入失败 | 返回 `TPL_LEARN_COMMIT_CHECKPOINT_FAILED`；模板修订保持已提交并可幂等补写会话 |
| 重复提交 | 比对既有绑定后幂等成功；绑定不一致时阻断 |
| 中途退出/重启 | 原子 JSON 会话与 SQLite 模板修订均可恢复 |
| 用户取消或退回人工校准 | 会话关闭或转人工，不能投影/提交学习规则 |
| 参考素材进入目标批次 | `reference_only` 绑定必须提供目标款显式选择；缺少选择时返回 `TPL_RENDER_SELECTIONS_INCOMPLETE` |

## 4. 验证命令与结果

执行环境使用本机已有 NuGet 缓存；首次完整 build 的旧 `obj/project.assets.json` 指向历史沙箱用户目录，经离线 restore 重建后复跑。

- `dotnet restore EcommerceDetailPage.Desktop.slnx --ignore-failed-sources -p:NuGetAudit=false`：成功。
- `dotnet build EcommerceDetailPage.Desktop.slnx --no-restore --verbosity minimal`：0 警告、0 错误。
- `dotnet test tests/Desktop.Application.Tests/Desktop.Application.Tests.csproj --no-restore --filter 'FullyQualifiedName~ReferenceLearning'`：19/19 通过。
- `dotnet test tests/Desktop.Infrastructure.Tests/Desktop.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~ReferenceLearning|FullyQualifiedName~ReferencePhoto'`：10/10 通过，覆盖学习相关端口、会话与真实提交集成。
- `dotnet test tests/Desktop.App.UiTests/Desktop.App.UiTests.csproj --no-restore --filter 'FullyQualifiedName~ReferenceLearningUsesCalibrationEditorAndBlocksSaveUntilExplicitReview'`：1/1 通过。
- `dotnet test EcommerceDetailPage.Desktop.slnx --no-build --no-restore --filter 'FullyQualifiedName!~SizeTableGoldSampleTests'`：489 通过、2 个显式 opt-in 测试跳过（真实 PSD 样本与外部 AI 探测）、0 失败；其中完整 WPF UI 为 15/15、架构门为 5/5。
- 无过滤完整 solution：同样 489 项通过、2 项跳过；两项分别为默认关闭的真实 PSD 样本与外部 AI 探测。另有两项 `SizeTableGoldSampleTests` 因当前工作树未携带 `size-real-a-577fa4d19f919a7a.jpg` 和 `size-real-b-9231ad299120bba6.jpg` 返回动态跳过/失败。该缺失属于既有 SIZE-01 本地媒体条件，不是 TPL-LEARN-01 回归失败，也不能据此宣称无条件完整回归全绿。
- `dotnet format EcommerceDetailPage.Desktop.slnx --no-restore --verify-no-changes`：通过。
- 最终收口检查：`git diff --check` 通过；变更集禁止媒体/PSD、数据库、日志、压缩包和二进制扩展名为 0；差异中的本机绝对路径、常见凭据模式、base64/私钥和媒体二进制扫描均为 0。工作树保持 detached HEAD `94f79d9`，未创建提交。

## 5. 剩余动作

1. 在实际应用中打开这组 6 页参考 PSD，逐框处理 11 个候选（包括成分/水洗唛图位）；如需固定内容，明确拒绝该候选，并在需要时查看 25 个被折叠旧替换层的审计证据。
2. 由产品负责人检查页面切换、缩放/偏移、1100×760 和标准宽窗实际观感，并完成保存、退出、重启恢复走查。
3. 在真实样本完成上述走查并形成 reviewed diff 后，才可完成 LEARN-S7 交接；在此之前 `TPL-LEARN-01` 保持进行中。
