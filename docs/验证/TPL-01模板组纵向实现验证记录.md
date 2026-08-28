# TPL-01 模板组纵向实现验证记录

## 当前结论

截至 2026-08-28，`TPL-S0` 至 `TPL-S7` 已完成，`TPL-01` 通过。产品负责人已在实际应用中确认流程没有问题，并接受当前版本作为阶段性视觉基线；五加一增量修订、旧批次快照隔离、新批次六页、失败回滚、重启恢复、模板包和源文件安全均有真实纵向或自动化证据。本文不把 PSD 技术门、Figma 上下文、历史 Photoshop 探针或测试数量单独写成模板功能完成证据，也不顺带宣称完整 `UI-01`、真实 AI 或 Photoshop 准确预览通过。

## 起点与范围

- 起点：`217ec0b docs: approve TPL-01 implementation brief`；阶段 0 工程骨架未重做。
- 开始前工作区干净；已按任务书顺序读取 PRD 1.5、当前状态、任务书、验收矩阵、ADR-010/011/012/014、交互规格和 T-01 至 T-05 高保真交付记录。
- 已按 `figma-design-to-code` 技能读取冻结 TPL 代表节点；实现以 PRD 1.5/交互规格为业务输入，以 Figma 为视觉和空间输入，实际 WPF 页面仍须由用户在真实窗口完成最终人工走查。
- 用户源 PSD、源照片、人工结果和尺码图只读；实验只使用工作区 `tmp` 中的明确副本。

## TPL-S0：已通过

- 候选 A：`PhotoshopFile 1.0.0` 不存在；`Aspose.PSD 26.8.0` 虽能读目标 ID，但层树计数与批准证据不一致，评估预览有水印，且商业许可/资源代价不满足门槛，因此拒绝。
- 候选 B：采用 `psd-tools 1.18.0` 隔离 worker。8 个批准 PSD 副本全部成功；G03/G05 层树、路径、ID、剪贴关系、蒙版状态和结构签名与批准证据一致。
- 候选 C：保留 Photoshop 2025 / UXP v5 作为显式兼容性探针，不作为默认校准实现。
- 打包 worker 与 .NET client 真实端到端通过；路径、缺失文件、畸形响应、超时和响应超限均稳定返回错误码。
- 四框默认 cover 与 Photoshop 人工参考差异低且几何一致；缩放/偏移探针无白边，换算误差不超过 `0.5 px`。详细证据见 `docs/验证/TPL-S0-PSD只读检查技术门记录.md` 和 `ADR-017`。

## TPL-S1：已通过

- 新增品类中立的 `TemplateGroup`、不可变 `TemplateRevision`、稳定 `TemplatePage`、显式 `FrameBinding`、`CalibrationDraft`、`CalibrationSample`、`TemplatePreviewState` 和 `BatchTemplateSnapshot`。Domain 未引用 WPF、SQLite、PSD 库、HTTP、Photoshop 或外层项目。
- 页面守卫拒绝非组合模式的 `variant_capacity`，组合模式要求正整数；PSD 层路径固定 `psd_bottom_to_top`，注册框必须携带稳定 layer ID、可见性、剪贴、蒙版、透明度、混合模式和结构签名。
- 修订状态机按任务书主流程和失败终点推进；提交需要相对 manifest 引用、规则 digest 和一致提交证据，`Committed` 后拒绝任何转换或草稿覆盖。增量修订继承原 `page_id`、资源 ID 和绑定，只给新增页分配新 ID。
- 批次快照创建时冻结修订、已选页面顺序、生成规则、模板资源和 frame ID；分析开始后切换返回 `TPL_BATCH_SNAPSHOT_FROZEN`。SQLite repository 对相同 batch 的冲突快照也稳定拒绝。
- 冻结 `template-revision/v1` JSON Schema 和正例；未知主版本、绝对/逃逸路径、缺少结构签名、非组合容量和 Photoshop 私有字段负例均拒绝。
- SQLite 从既有 checksum `desktop-v1-schema-001` 单调迁移到 v2；启动验证所有已应用 checksum，迁移在同一事务中执行。预置不兼容 v2 结构的故障注入证明失败后版本仍为 1、旧数据可读、v2 表未半创建。
- `ITemplateRepository`、`IBatchSnapshotRepository` 都映射到已有 `SqliteDesktopStore` singleton，继续使用唯一 `SqliteWriteQueue`；模板修订、绑定、草稿和快照重启恢复通过。

## TPL-S2：已通过

- 新增只读源 PSD fingerprint、应用自有 staging 副本和复制前/中/后长度与 SHA-256 核对。目标路径只能由 `IPathPolicy` 解析为应用自有相对路径；源绝对路径只存在于当次 Application 请求，不进入 SQLite、manifest、日志或 RenderPlan。
- 流程固定为“只读 fingerprint → 复制到 staging → 内容 hash 判重 → 明确选择”。重复时不删除用户源 PSD，也不自动写成第二个资源；保留现有副本或取消时只删除本次应用自有 staging，继续则经过 S0 inspection 后保存独立资源。
- `command_id` 提供写入幂等；同命令不同资源返回 `TPL_IMPORT_COMMAND_CONFLICT`。SQLite v3 新增 `template_resources` 与 `template_import_commands`，继续复用唯一 `SqliteDesktopStore`/`SqliteWriteQueue`，重启后可按命令和 hash 恢复。
- inspection 失败、inspection hash/长度不一致、重复查询失败、SQLite 外键/提交失败和无效重复选择均清理内部副本且不留下数据库半对象。临时 `.part` 清理失败不会覆盖原错误，而返回 `TPL_IMPORT_CLEANUP_REQUIRED`。
- 真实文件端口测试比较源 SHA-256、内部副本 SHA-256 和清理结果；应用/文件/SQLite 串联测试证明成功重启恢复、inspection 失败零资源、重复源文件均保留、数据库事务失败回滚。测试使用临时 4-byte `.psd` 字节文件和 fake inspection，不冒充真实 PSD 结构验证；真实结构读取仍由已通过 S0 的隔离 worker 证据承担。

## TPL-S3：已通过

- 模板库、空状态、详情、删除确认、另存/导入/导出入口、新建和增量模板组均使用真实 repository 投影；新增 PSD 后可从页面设置或校准页继续添加，新增页可移除，增量原页面保持只读。
- T-03 页面设置支持页面命名、排序、内容类型、生成方式和组合容量校验；页面设置现为“页面列表 / 应用副本 PSD 预览 / 页面属性”三栏，列表在窄内容区禁用横向滚动。
- 重复 PSD 继续通过明确用户选择处理，不自动删除用户源文件；界面不把 Figma 旧版“新建批次 / 模板组 / 历史批次”文案当作业务要求，导航按最新 PRD/交互规格保留“工作台 / 模板 / 任务 / 设置”。

## TPL-S4：已通过

- T-04 三栏校准编辑器支持候选框显式注册、单框单图绑定、拖拽/方向键移动、缩放和偏移、规则文字、撤销/重做、草稿持久化、退出保存/不保存/取消及返回页面设置。
- 右侧按 Figma 信息层级呈现“素材 / 规则”，当前图片框显示 PSD 原始图层名或用户可读兜底，不显示 `frame_*` 内部 ID；历史英文选图要求在投影层转换为中文默认文案。
- 画布选中态补充低强调焦点边框和四角控制点，底部状态条显示可行动状态。高级调整仍为可选，不把本地预览冒充 Photoshop 准确预览。
- 左右栏外观已按 Figma T-04 `191:120` 的精确尺寸与 token 复核：左 224px、右 260px、间距 16px、面板 1px `#E5E5E7`/8px 圆角、整行浅灰选中态、72×32 标签态和 6px 控件圆角；校准页关闭外层纵向滚动，左右栏使用内部滚动，真实窗口页头和三栏在 1440×900 同屏可见。
- 真实应用已执行“从已登记素材拖入当前框 → `Delete` → 清除当前绑定”往返检查：绑定数由 0 变 1 再回 0，登记素材仍在列表，源文件未删除；服务测试同时覆盖清除后撤销恢复素材与选择状态。右侧另有可见“移除素材”入口，按钮状态随绑定变化重新查询。

## TPL-S5：已通过

- 五页模板添加第六页的增量修订、原页面稳定 ID/资源/绑定/规则保持、失败时旧修订和 marker 保留、旧批次快照隔离、新批次读取六页均由 service/repository 集成测试覆盖。
- SQLite 迁移、manifest、ready/committed marker、当前指针切换和批次快照均沿用单 writer 与原子提交边界；分析开始后拒绝切换快照。

## TPL-S6：已通过

- `template-package/v1` Schema、正例、版本拒绝、路径/敏感字段拒绝、ZIP 条目校验、跨空白数据根导入、PSD hash/规则一致性和删除引用守卫均已有合同/集成证据。
- 模板包只包含当前有效修订及应用自有 PSD 副本和规则，不包含 Key、任务历史、代表款源照片、人工结果、绝对路径、数据库、缓存或日志。
- reviewed diff 补严 `inspection` 合同：Schema 现在完整约束 canvas、计数、结构签名、timing、layers、bounds、clipping base 和 smart object；运行时同时核对条目长度/hash、层路径/ID/属性、绑定签名、页面归属和 `asset_id` 规则。空 inspection、adapter 私有字段和结构不一致稳定拒绝。

## TPL-S7：已通过

- 产品负责人在实际 WPF 应用中完成模板流程和校准界面检查，确认流程没有问题，并明确接受当前版本作为阶段性视觉基线继续完成 `TPL-01`。
- 当前 T-04 已满足本纵向任务继续开发所需的三栏布局、画布可用性、双向框选择、素材移除、撤销/重做、退出/返回和中文可行动状态；纯外观的跨页面统一精修转入 `docs/任务/UI-01全局视觉收口清单.md`，不作为 TPL 功能验收的无限打磨项。
- reviewed diff 覆盖 Presentation、Application、Domain、Contracts、Infrastructure、Schema/ADR 和测试；修正一处旧阶段占位提示，并补严模板包结构证据合同。未发现 AI/产品规则进入 Photoshop adapter、renderer-neutral `RenderPlan` 泄漏 adapter 命令或把全部智能对象自动登记为图片框。
- 最终完整构建、123 项 solution 回归、格式检查、diff 检查、临时输出清理、源文件安全和未跟踪文件审查均通过。

实际命令：

```powershell
dotnet test tests/Desktop.Domain.Tests/Desktop.Domain.Tests.csproj -c Debug --no-restore
dotnet test tests/Desktop.Contracts.Tests/Desktop.Contracts.Tests.csproj -c Debug --no-restore
dotnet test tests/Desktop.Infrastructure.Tests/Desktop.Infrastructure.Tests.csproj -c Debug --no-restore
dotnet test tests/Desktop.Architecture.Tests/Desktop.Architecture.Tests.csproj -c Debug --no-restore
dotnet format EcommerceDetailPage.Desktop.slnx --no-restore --verify-no-changes --verbosity minimal
dotnet build EcommerceDetailPage.Desktop.slnx -c Debug --no-restore --no-incremental
dotnet test EcommerceDetailPage.Desktop.slnx -c Debug --no-build --no-restore
```

## 源文件与仓库安全

- 8 个源 PSD、4 张源照片和 1 张人工参考在复制前后 SHA-256 均不变；解析、预览、打包和损坏注入仅发生在 `tmp/tpl-s0-validation`。
- Git 不提交 PSD/JPG、人工参考、预览、数据库、缓存、日志、凭据、虚拟环境、PyInstaller 制品或其他生成物。
- worker 只接受应用自有路径；PSD 读取前后再次核对 hash。所有图片框仍需用户显式注册或完整结构证据，未把全部智能对象自动注册。

## 当前验证结果

| 检查 | 结果 |
| --- | --- |
| NuGet restore + 漏洞索引 | 人工批准联网后 11 个项目恢复；无 `NU1301`、`NU1900` 或漏洞告警 |
| Python 语法 | worker 与本地预览验证工具通过 `compileall` |
| 打包 worker | 102 文件；58,631,966 bytes；EXE SHA-256 `112D4119...C99A` |
| 真实 PSD | 8/8 成功，源/副本 hash 不变；损坏/协议/JSON 负例稳定阻断 |
| .NET client 聚焦测试 | 6/6 通过 |
| TPL-S0 solution build | 0 警告、0 错误 |
| TPL-S1 聚焦测试 | Domain 26/26；Contracts 15/15；Infrastructure 21/21；Architecture 5/5 |
| TPL-S2 聚焦测试 | Application 12/12；Infrastructure 31/31；源 hash、重复确认、重启恢复、inspection/SQLite 失败清理均通过 |
| WPF 应用编译 | `dotnet build src/Desktop.App/Desktop.App.csproj --no-restore -p:OutDir=<临时应用目录>`：0 警告、0 错误；旧人工走查进程锁定工作区输出，未强制结束用户进程 |
| 当前 solution test | 123/123 通过：Domain 29 + Contracts 16 + Application 27 + Infrastructure 41 + Architecture 5 + WPF UI 5 |
| Figma/WPF 对照修正 | T-03 三栏、T-04 素材/规则层级、用户可读图片框名、四角控制点、底部状态条、中文选图要求兜底和 8px 卡片圆角已落地；产品负责人接受当前版本为阶段性视觉基线 |
| T-04 左右栏与素材移除复核 | 精确设计参数已映射到 WPF；实际窗口三栏同屏，外层不滚动；拖入素材后 `Delete` 仅清除绑定并回到 0，登记素材/源文件保留；产品负责人阶段性视觉确认通过 |
| 模板包结构证据 | 完整 inspection Schema 与运行时交叉核对通过；空 inspection、adapter 私有字段、PSD hash/长度或层绑定不一致均拒绝 |

## 后续项（不阻塞 TPL-01）

- T-05 Photoshop 准确预览和真实 AI 学习不属于本任务实现范围，且不得用 mock 冒充通过。
- 完整 `UI-01` 仍待主要页面齐全后按 `docs/任务/UI-01全局视觉收口清单.md` 集中验收；当前产品确认只是 TPL 页的阶段性视觉基线。
- `DATA-01` 的完整模板库跨卷迁移、正式 Photoshop 批量渲染、安装和性能仍由各自任务关闭。

## 下一动作

从本记录和 `docs/当前项目状态.md` 先建立 `INPUT-01` 纵向任务书，交付并验收真实 `BatchDraft`、目录扫描状态和 asset manifest；合并形成新主基线后再恢复已批准的 `AI-01`。AI 开发期间继续保持品类中立合同、真实金标门和 renderer-neutral `RenderPlan` 边界。主要页面齐全后再启动一次全局 `UI-01` 视觉收口。

## 2026-08-28：主开发基线合并复核

- 合并前确认主开发 worktree 位于干净的 `codex/desktop-foundation-01`，HEAD 为 `217ec0b`；`217ec0b` 是 TPL 分支 `62b9dff` 的直接祖先，因此使用 `git merge --ff-only codex/tpl-01-vertical`，未产生内容冲突或额外 merge commit。
- 合并差异完整包含 TPL-S0 至 TPL-S7 的领域模型、SQLite/manifest、只读 PSD 导入、T-01 至 T-04 WPF、增量原子修订、旧批次快照隔离、模板包、renderer-neutral `RenderPlan` builder 和 `src/Desktop.App/Styles/DesignSystem.xaml`。
- 主 worktree 的旧 `project.assets.json` 曾引用不存在的沙箱 NuGet 目录；将 `NUGET_PACKAGES` 明确指向当前用户包缓存并执行 11 项目强制恢复后，`dotnet build EcommerceDetailPage.Desktop.slnx --no-restore -v:minimal` 为 0 警告/0 错误，`dotnet test EcommerceDetailPage.Desktop.slnx --no-build -v:minimal` 为 123/123 通过。
- 合并和验证后 Git 状态干净；`bin/obj` 均被忽略。提交差异未发现 PSD/JPG、数据库、缓存、日志、凭据、dump 或生成物。`AI-01` 必须从包含 `62b9dff` 及本复核记录的最新主开发基线建立，不得从 `217ec0b` 直接建立。

## 2026-08-28：TPL 页视觉追加验证

- 产品负责人要求把 TPL 页视觉收口提前到当前任务执行；本轮重新读取 Figma T-01 `375:208`、T-02 `381:3`、T-03 `191:2`/`191:50`、T-04 `191:120`/`191:155`，并按 WPF 技术栈转换，未引入 Tailwind、Web 控件或外部 UI 框架。
- 共享实现：`src/Desktop.App/Styles/DesignSystem.xaml` 提供统一字体/字号/行高、颜色、边框、圆角、按钮、文本框、自绘 ComboBox、开关、列表项、标签、滚动条、焦点/悬停/禁用状态和应用内弹窗；`App.xaml` 只负责合并资源字典和应用级 converter。
- 页面实现：T-01/T-02 使用 Figma 的 208px 导航、32px 工作区边距和页头节奏；T-03 宽版三栏为 `300 / 16 / 自适应 / 16 / 352`，1240px 以下响应为 `248 / 16 / 自适应 / 16 / 300`，T-04 为 `224 / 16 / 自适应 / 16 / 260`。内容类型/生成方式不再落入 Windows 默认灰色模板，长文本框不再被固定 38px 高度裁切。
- 弹窗实现：放弃、移除、退出、删除和引用阻断使用 560px 应用内弹窗，32% 黑色遮罩、8px 圆角、阴影、危险主按钮和 Escape 后备操作一致；重复 PSD 复用同一 DialogCard。系统文件选择器及启动失败兜底不属于 TPL 业务弹窗，保留 Windows 原生交互。
- 工程实际窗口：在 `1440×900` 真实 WPF 窗口检查模板库空状态和 T-02 导入/校准方式代表状态，中文字体、按钮、面板、禁用卡片与底部操作完整显示。2026-08-28 产品负责人随后使用默认真实数据进入 T-03/T-04，复核左右栏、下拉框、文字完整性、三栏比例、画布和图片框选中态，确认“没什么问题，基准通过”；该结论来自实际应用人工走查，不以测试 fixture 代替。
- 已执行 `dotnet format EcommerceDetailPage.Desktop.slnx --verify-no-changes --no-restore`：通过；`dotnet build EcommerceDetailPage.Desktop.slnx --no-restore -v:minimal`：0 警告、0 错误；`dotnet test EcommerceDetailPage.Desktop.slnx --no-build -v:minimal`：123/123 通过（Domain 29、Contracts 16、Application 27、Infrastructure 41、Architecture 5、WPF UI 5）。聚焦 WPF UI 测试同时覆盖宽/窄 T-03 栏宽、T-04 窄版切换、共享 ComboBox 模板、可增长文本框和应用内危险确认弹窗。
- 本轮不改变 `TPL-01` 已通过的功能结论，也不宣称完整 `UI-01`、真实 AI、Photoshop 准确预览或正式渲染通过。
