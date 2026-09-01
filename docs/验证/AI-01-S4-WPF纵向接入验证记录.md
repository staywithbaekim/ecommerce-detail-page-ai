# AI-01 S4→S5 WPF 纵向接入与真实样本验证记录

## 范围

本记录覆盖 AI-01 S4 的 B-02/B-03 应用层/WPF 接入，以及同一执行链中已经形成的 S5 非服装正例和模板不匹配负例；不代表完整 S5、正式 Photoshop 渲染或 AI-01 整体验收。实现直接消费 INPUT-01 的 `ScanReady BatchDraft`、committed 模板快照以及 AI-S3 的 request ledger、result/cache、scheduler checkpoint；Presentation 未复制 AI 状态机、供应商 DTO 或产品规则。

重复的从属任务书 `docs/任务/AI-01-S4-WPF纵向接入任务书.md` 已删除，`docs/任务/AI-01纵向实现任务书.md` 是唯一正式任务来源。

## 基线与安全门

- 当前 `HEAD=073753efc9602603b9a798efa35813bd5127e09f`；工作树开始时干净，且要求的上游提交为其祖先。
- 当前工作树只包含本次 S4→S5 源码、测试和文档差异；未发现 PSD/JPG/PNG、数据库、SQLite、缓存、日志、压缩包、凭据或本机路径进入提交候选。
- 三图历史请求继续按 provider 已计量、客户端 `AiResultUnknown` 处理，未重发。除本文登记的圆环女包首层/主体分析和黑包一次首层分析外，没有其他新 HTTP；黑包未发逐图主体分析或第二次请求。
- 未实现 RenderPlan 正式渲染、Photoshop adapter 业务规则、账号/支付/云同步/平台上传/动态尺码，也未把 S-01 最小授权卡扩展为新的设置系统。

## 代码与自动化验证

实现要点：

- `AiBatchAnalysisWorkflow` 负责 B-02 渐进状态和 B-03 投影：待分析、分析中、人工配置、可生成、待检查、不可生成、预算停止、系统恢复、取消；暂停/恢复/取消/等待网络从 durable scheduler 控制状态恢复。
- 页面级业务问题隔离在页面投影；同款已取消页面不会阻断兄弟页面；全取消与全业务零输出分开显示；系统未知/恢复状态不启动生成。
- 人工选图、Top-N 切换、原子锁定 checkpoint 和逐图主体分析入口已接通；锁定仍由领域层 selection revision/CAS 守卫。
- WPF B-03 使用现有 `DesignSystem.xaml`，冻结宽版 1440×900、窄版 1100×760 的标题区、摘要区、单行进度/筛选、款式/页面/候选列表和 64px 底栏；长列表使用内部 `ScrollViewer`，支持 UIA 与中文状态文案。预算硬停止且仍有未结算页面时显示批次级“本轮 AI 预算已达到停止线”，禁止进入生成但保留取消动作；所有页面已结算时不覆盖原有可生成/待复核/零输出状态。必需图片框闭合按唯一 frame requirement 判断，允许同一框存在多个 Top-N 候选。
- “新建批次”和“恢复上次批次”不再混用：新建始终打开空白页，旧 AI 批次只由显式恢复入口进入。模板编辑区存在未提交增量草稿时，新批次下拉只列出有 committed revision 的模板，并通过 `GetCommittedDetailsAsync` 只读取 committed 页面；生产保存不会消费未提交页面。用户误导入的 `gallery-21` 仍保留在模板草稿中，没有被删除、改写或偷偷并入正式批次。
- B-03 自动刷新只读取本地 durable state；分析结算后定时器停止。人工替换只写本地 selection checkpoint，不直接调用 provider；逐图主体分析保持独立显式动作。
- 候选证据文案区分正向与负向码：颜色不符、缺肩带等真实负向证据显示为“不符/缺少”，未识别证据统一显示“需要人工核对”，不会降级成“其他检查通过”。

命令与结果：

```text
dotnet build src/Desktop.App/Desktop.App.csproj --no-restore
成功，0 warnings / 0 errors

dotnet test EcommerceDetailPage.Desktop.slnx --no-build --verbosity minimal
Domain 40、Contracts 32、Application 104、Infrastructure 163（1 个 opt-in RealProbeThroughFormalAdapter 跳过）、Architecture 5、WPF UI 11；合计 355 passed / 1 skipped / 0 failed

git diff --check
通过
```

## 实际应用观察

使用当前实际 `Desktop.App.exe` 恢复了真实 `black-bag-01` 批次。模板图片框要求是灰色圆环女包，输入唯一照片是黑色方包；B-03 显示“待人工复核”、可生成页面 `0/1`，没有因唯一候选被默认选用而误标绿。生成前款式行是普通摘要容器，不是 `Expander` 或行内下拉；状态灯位于状态文字前，右侧 `>` 使用独立按钮和内边距，不再裁切。点击 `>` 进入独立“款式检查”页，旧标题由互斥可见性隐藏；返回区、标题和款式名不重叠。独立页左侧显示本款全部用图、图片框要求和“AI 推荐 · 已选用”，右侧显示本款全部照片与“当前使用”，不向用户暴露文件名、asset ID、结构签名或本机路径。

同轮 UIA 实机读取到 B-03 的四个筛选、款式行、`>` 入口、独立页返回按钮、左右列表与替换动作；“待检查”筛选获得焦点后，方向键可把焦点移动到相邻筛选。返回 B-03、切换筛选与查看独立页都只读本地状态，没有点“刷新状态”、没有锁定/替换，也没有形成 provider 调用。

产品负责人于 2026-08-31 在实际应用检查上述真实单款界面并确认无问题；该结论不是 fixture、反射投影或截图替代。自动化覆盖 1440×900、1100×760、长列表、UIA 和中文状态；最终 AI-01 退出门仍需在实际应用补齐 100%/125% DPI、中文输入，以及待分析、等待网络、预算停止、全业务红、未知结果和恢复等尚未在本次真实单款中出现的代表状态。

## 上传授权交互批准与实现证据（2026-08-31）

产品负责人已批准“一次性上传授权交互”：不新增登录/账号/云授权系统；F-01 首次使用配置或首次点击“扫描并分析”时显示一次默认未勾选的“AI 分析与图片上传授权”说明和同意操作，明确仅上传必要的压缩分析副本到火山方舟，不上传 PSD、原始高清图片、本地路径或正式输出；日常批次不重复弹窗。S-01 显示已授权/已撤回并可撤回/重新授权，撤回后禁止新 AI 请求，已有本地结果保留，重新授权后才可继续。`consent-{batchId}` 仅为请求关联标识。

实现已将授权生产者建模为供应商无关 `IConsentPort`（架构决策见 `docs/架构决策/ADR-019-AI上传授权状态边界.md`）：应用内允许/撤回操作由 `SqliteDesktopStore` 在 SQLite v12 `ai_upload_consents` 当前状态表与 `ai_upload_consent_events` 追加事件表中以同一事务写入 `Granted/Revoked`、作用域 `ai-analysis-copy`、版本 `ai-upload-consent/v1`、consent ID 和时间。`AiBatchAnalysisWorkflow.StartAsync` 入队前强制授权；scheduler 在预算预留前通过 `IAiUploadAdmissionPort.TryAcquireAsync` 执行与撤回共用 writer/事务的最终准入读取，再转为 `Uploading`/调用 provider。缺失/撤回均返回 `AI_UPLOAD_CONSENT_REQUIRED`/`AI_UPLOAD_CONSENT_REVOKED`，保持 ledger `Queued`，不消耗预算、不调用 provider、不改写未知结果；准入先提交后撤回不回滚已准入请求，事务不跨 HTTP。跨重启当前状态和事件历史可恢复；重新授权后 `ResumeAsync` 可继续原待处理请求。

自动化证据：`AiConsentStoreTests` 覆盖授权/撤回跨重启、consent ID 保留、事件历史数量和缺失默认空，以及已撤回准入读取拒绝且不追加事件；`AiAnalysisSchedulerTests` 覆盖缺失授权、撤回授权、重新授权恢复、“首次读取 Granted 后撤回”的受控竞态和“准入先提交后撤回仍完成”的边界，验证 provider 调用数/预算请求数在 fail-closed 前为 0。完整 solution 回归及迁移验证见上文命令结果与 `docs/任务/AI-01纵向实现任务书.md` 实施记录。

## S5 衔接状态

产品负责人已批准本次提供的 PSD、商品图片和人工参考仅用于本机只读 S5，并允许应用上传必要压缩分析副本；3 个非服装款、6 张素材已登记，源 PSD 与素材 hash 复核均未变化。

- `ring-bag-01`：正式批次资产 digest 与批准清单一致；首层请求选择唯一商品图为 rank 1（confidence `0.98`），人工锁定后仅执行一次主体分析（confidence `0.99`）。SQLite 用量为 `3049/425` 与 `3138/375` tokens，均非缓存、无重试。
- `black-bag-01`：只执行一次获批首层请求 `ai-batch-7900e6631ac1401188043960930b0a7f-style_a7fb1940362383c8589edcba172f30d7`，模型配置版本为 `doubao-seed-2.1-pro-260628`。唯一照片因候选集合只有一张仍为 rank 1，但 requirement confidence 仅 `0.40`，证据码包括黑色/灰色不符、缺少肩带，结果 warning 为 `no_fully_matching_asset`；主体几何 confidence `0.98` 只说明黑包主体可定位，不表示满足模板语义。B-03 正确保持待人工复核、可生成 `0/1`。SQLite 用量为 `3140/569` tokens，cache hit `0`、retry `0`；没有逐图主体分析或第二次请求。
- 最终账本与 usage 均为 `3`，没有因返回、筛选、自动读取本地状态或应用关闭而增加。黑包源图 SHA-256 为 `6F4D002295EAF13580A1DDBA85E8BEA1D1BE592F27A9EC1F73872046924DE8CA`，请求前后及 asset manifest 三者一致；正式输出目录为 0 文件，未执行 Photoshop。
- 黑包首层 `1600×1067` PNG 分析副本为 355,408 bytes。成功首层仍可能产生显式 refinement 时，scheduler 按 durable lease 保留该副本；当前不得为“清理”手工破坏租约，也不得通过第二次请求清理。该副本、sidecar、数据库、结果 JSON 和样本媒体全部位于忽略的本机数据/临时目录，不进入 Git。

上述正例和负例不等于完整 S5：电脑机箱、服装/尺码/异常和故障矩阵仍待执行，真实 HTTP 继续逐次审批；三图历史请求仍保持 provider 已计量、客户端 `AiResultUnknown` 且不重发。
