# TPL-LEARN-01 LEARN-S7 真实样本逐框、恢复与隔离记录

## 结论

本记录完成了 fix16 可由自动化、只读样本和产品负责人本机走查证明的本地子门，但不把 `TPL-LEARN-01` 标记为整体通过。已登记的 6 页人工完成 PSD 在隔离副本上逐框核对为 11 个有效候选，全部与参考源照片逐字节精确匹配，并具有自身像素和最终合成贡献；产品负责人重新分析并完成本地复核，未发现有效层、逐框隔离、规则编辑和复核流程问题。退出/重启恢复、旧层证据持久化和参考款/目标款隔离已有自动化证据。

随后对需求和实现的复核发现，fix16 的用途文字仍由图层名、文件名、尺寸和位置启发式生成，该路径没有调用视觉模型，也没有证明确认后的理解能让不同目标款文件夹挑对图。因此本记录现在只作为 LEARN-S0-S2、LEARN-S3 本地身份/结构/几何及部分 S4-S6 UI/持久化证据；LEARN-S3 AI 动态语义未完成，受影响的 S4-S7 必须在新实现后重验。当前不需要用户继续运行 fix16，办公室 Photoshop 仍只属于后续 RENDER-01。

2026-09-06 已按产品负责人反馈修正导入页交互：无 PSD 时整个空状态区域可点击选择/拖放，复用统一空状态结构显示圆形图标、完整说明和黑色主操作按钮；两种“校准方式”改为整张卡片可点击的单选控件，圆点仅作状态指示。随后补齐 PSD 组文件夹导入、文件夹拖拽、继续添加和单项移除；移除只改变当前草稿，不删除用户源 PSD，并在无其他页面引用时回收应用内部副本。`Desktop.App` XAML 编译通过，WPF UI 回归 15/15 通过；该自动化证据不替代真实 PSD 导入和产品负责人逐框走查。

## 样本与安全范围

- 参考 PSD 隔离副本：`tmp/tpl-learn-review-20260905/templates/奶奶新模板/`
- 参考源照片隔离副本：`tmp/tpl-learn-review-20260905/source/新建文件夹/`
- 以上目录只读使用；没有覆盖、重命名或写回用户原始 ZIP、PSD、JPG 或照片。
- `tmp/`、`output/`、`bin/`、`obj/` 和本地数据库均在 `.gitignore` 规则内；Git 追踪文件中没有 PSD/JPG/ZIP/数据库/缓存/日志。

## 自动逐框结果

“复核状态”是应用等待产品负责人确认的状态，不是自动化擅自替用户接受的结论。

| 页面 | 结构路径 | 类型 | 精确匹配源照片 | 复核状态 | 折叠旧层 |
| --- | ---: | --- | --- | --- | ---: |
| G-详情-01.psd | 4 | 商品图位 | `036A7092__036A7320_9Y2A3598.png` | 待复核 | 2 |
| G-详情-03.psd | 3 | 商品图位 | `036A7091__9Y2A3650.png` | 待复核 | 1 |
| G-详情-04-面料.psd | 5 | 商品图位 | `I36A8881.JPG` | 待复核 | 2 |
| G-详情-05-细节.psd | 4 | 成分/水洗唛信息图（是否随目标款替换待确认） | `a732592fe1beb1c75e426cb227bc9ddd.png` | 待复核 | 2 |
| G-详情-05-细节.psd | 9 | 商品图位（领口语义待确认） | `I36A8876.JPG` | 待复核 | 2 |
| G-详情-05-细节.psd | 14 | 袖口细节图（深色款素材，跨款风险待确认） | `I36A8887.JPG` | 待复核 | 2 |
| G-详情-05-细节.psd | 19 | 商品图位（构图语义待确认） | `036A7092__036A7320_9Y2A3598.png` | 待复核 | 2 |
| H-模特图-01.psd | 5 | 商品图位（正/背语义待确认） | `036A7091__9Y2A3650.png` | 待复核 | 3 |
| H-模特图-01.psd | 11 | 商品图位（正/背语义待确认） | `036A7092__036A7320_9Y2A1616.png` | 待复核 | 3 |
| H-模特图-02.psd | 5 | 商品图位（正/背语义待确认） | `036A7166__036A7320_9Y2A3558.png` | 待复核 | 3 |
| H-模特图-02.psd | 11 | 商品图位（正/背语义待确认） | `036A7166__036A7320_9Y2A3941.png` | 待复核 | 3 |

结构合计：11 个候选，全部为可人工确认的内容图位；其中 9 个是与页面款式一致的商品视觉，1 个是成分/水洗唛文字信息图，1 个是与同页红色主体不同色系的袖口细节图，不能在没有人工确认时自动当作同款商品素材。25 个被栈顶覆盖的旧替换层和 4 个隐藏智能对象不进入候选。`a732592fe1beb1c75e426cb227bc9ddd.png` 的源图尺寸为 239×252，但它与实际图片框及页面文案对应，不能单凭尺寸自动拒绝；用户若认为该内容应固定，可在复核时点击“保留模板内容”。

## 二次视觉复核补充（2026-09-06）

用户指出“每个框都填了图”后，重新查看了 6 张模板总览、11 张候选缓存和对应源照片，而不是只依赖图层名称或尺寸。结论如下：

- 原始 PSD 共 40 个嵌入式智能对象，其中 36 个可见。结构折叠后只有 11 个栈顶对象是真正影响当前画布的候选；另外 25 个可见对象位于同一剪贴窗口的下方，已经被栈顶对象覆盖，不会贡献当前输出像素。这些是“无用/重复”的旧替换层，但仍只作为审计证据保留，不能从用户源 PSD 自动删除。
- 4 个智能对象处于隐藏状态（`G-详情-01.psd:0`、`G-详情-03.psd:0`、`G-详情-04-面料.psd:6`、`H-模特图-02.psd:12`），当前画布不显示它们，默认不启用。
- `G-详情-05-细节.psd:4` 的 `a732592fe1beb1c75e426cb227bc9ddd.png` 不是空框，而是一张包含“聚酯纤维91.7% / 氨纶8.3%”的成分文字图。它是有效的、已填充的智能对象，但不是通用商品照片；后续目标款若成分不同，必须让用户明确选择替换或保留。
- `G-详情-05-细节.psd:14` 的 `I36A8887.JPG` 是深色款袖口图；同页主图、领口图和面料图为红色款。它不是无用层，而是“素材存在但同款语义不一致”的风险项，不能自动复制到红色目标款。
- 其余 9 个栈顶候选的画面与页面款式一致，且嵌入内容和源文件 hash 一一对应。所有 11 个候选仍保持“待复核”，不由自动化替用户接受。

因此，应用的安全处理是：候选层全部展示给用户；被覆盖旧层只记入 `superseded_layers`；隐藏层不进入候选；成分图和跨款袖口图必须在逐框复核时明确“替换”或“保留模板内容”。

## 自动化证据

1. 真实样本逐框测试：
   `dotnet test tests/Desktop.Infrastructure.Tests/Desktop.Infrastructure.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~ReferenceLearningRealSampleTests'`
   在显式提供隔离样本根、worker 脚本、临时 Python 依赖和 Python 3.10 后通过 1/1。测试逐页断言结构路径、源照片文件名、Exact hash、候选状态和替换栈证据。
2. 退出/重启恢复与源变化 fail-closed：`ReferenceLearningServiceTests` 相关 16 项通过，其中包括持久化会话恢复、stale review、PSD/照片变化拒绝和取消后禁止投影。
3. 多页原子提交和重启读回：`ReferenceLearningCommitIntegrationTests` 与 `JsonReferenceLearningSessionStoreTests` 聚焦 7 项通过；覆盖 SQLite/manifest marker、幂等重试、会话状态恢复和 `reference_only` 元数据。
4. 参考款/目标款隔离：`TemplateRenderPlanBuilderTests.LearnedBindingRequiresExplicitTargetBatchSelection` 通过；没有显式目标款选择时拒绝生成，提供目标款后计划只包含目标资源，输出 JSON 不包含参考资源 ID。
5. WPF 复核入口自动化：`ReferenceLearningUsesCalibrationEditorAndBlocksSaveUntilExplicitReview` 通过 1/1，确认复核页、未完成复核时保存按钮禁用；内容是否固定由用户拒绝候选决定。
6. 完整离线回归（排除工作树缺失的两张 SIZE-01 本地金标）：489 通过、2 个默认关闭的 opt-in 测试跳过（本真实 PSD 样本、外部 AI 探测）、0 失败；build 0 警告/0 错误；`dotnet format --verify-no-changes`、`git diff --check` 和禁止产物扫描通过。

7. 隔离启动补充核对：将 Debug `Desktop.App` 与 onedir `psd-inspection-worker` 复制到 `tmp/tpl-learn-s7-worker-build/app-bin/`，使用 `--data-root` 启动后进程保持运行并仅在隔离目录创建 SQLite 状态文件；应用基目录下的 `psd-inspection-worker/psd-inspection-worker.exe` 存在。直接用该安装目录中的 worker 对 `G-详情-05-细节.psd` 调用返回 `exit=0`、`schema_version=psd-inspection/v1`、`layer_count=50`、`smart_object_count=12`、`clipping_count=13`，PSD hash 为 `B75992229B87EEA33A7522E6A22A466994B50DA4A376F3A05D871B6A0DEFE076`。这证明隔离 worker 包可运行，但没有把直接 worker 自检冒充 WPF 内真实导入和产品负责人逐框确认。
8. UI 修正版测试包：`tmp/TPL-LEARN-01-S7-folder-management-test-package.zip`，SHA-256 为 `E9F76AC202F6BAE32B2010104C07A4A5563EE2F9618A64F8BDA8A8B0B97855C3`；包内含最新 `Desktop.App.exe`、PSD 文件夹导入/添加/移除 UI、应用层资源回收和 `psd-inspection-worker`，可在不安装 Photoshop 的本机执行本任务走查。

本轮还用隔离 `--data-root` 启动 `Desktop.App.exe`，进程保持响应并创建隔离 SQLite 状态文件，随后结束测试进程；未触碰正式用户数据根。原始 Debug 输出仍缺少 worker，因此没有把该外壳启动冒充真实 PSD 学习走查；带 worker 的隔离 `app-bin` 只完成了进程/worker 自检，WPF 内逐框导入、退出/重启产品走查和 Photoshop 输出仍待办公室环境完成。

## 2026-09-06 旧运行目录与退出异常修复记录

用户实际截图仍显示英文提示 `The PSD is damaged or uses an unsupported structure...`，并反馈点击“退出/放弃模板组”会关闭整个软件。对照文件时间确认截图使用的是旧目录 `tmp/tpl-learn-s7-local-app-20260906`：该目录的 `Desktop.App.dll` 早于本轮源码修复，因此不能用它验证中文错误映射和退出保护。原始 PSD 仍可由 worker 直接解析，未据此判定用户 PSD 损坏。

本轮源码修复包括：

- `ReferenceLearningService` 区分内部 PSD 缺失与 PSD/参考照片发生变化；WPF 将 worker 错误映射为中文，并显示具体 PSD 文件名和下一步动作。
- 模板页面移除时同步回收内部 PSD 与 `preview.png`，避免“预览仍在但 PSD 已不存在”的残留状态。
- “放弃模板组”将确认弹窗、内部草稿清理和模板库刷新纳入同一异常安全边界；`AsyncRelayCommand` 为该命令增加异常回调，未预期异常只更新页面状态，不再让 `async void` 异常关闭 WPF 主窗口。

最新本机运行目录为 `tmp/tpl-learn-s7-local-app-20260907-fix08`，由最新源码构建输出和已验证的 `psd-inspection-worker` 组成，`Desktop.App.dll` 与源码编译输出 SHA-256 一致，包含完整 `Desktop.App.exe`、依赖 DLL 和 `psd-inspection-worker/psd-inspection-worker.exe`；新数据根为 `tmp/tpl-learn-s7-local-data-20260907-fix08`，不与旧残留状态混用。隔离启动保持进程响应，并只在新数据根创建 SQLite 文件。

本轮验证：`Desktop.App` build 0 警告/0 错误；Application 学习聚焦 25/25；Infrastructure 学习聚焦 7/7；模板编辑应用层聚焦 9/9；WPF UI 全量 16/16，学习 UI 聚焦 2/2。使用最新隔离目录中的打包 worker 对真实样本 6 个 PSD 做 `psd-inspection/v1` 只读核验，6/6 成功；该核验未写入预览，也不替代应用内逐框映射和产品负责人确认。以上仍不替代产品负责人在新运行目录中的真实逐框确认、点击“放弃模板组”后主窗口保持打开、重启恢复和 reviewed diff，因此 LEARN-S7 仍为进行中。

## 2026-09-07 模板详情页“编辑”入口修复

用户反馈在模板详情页点击“编辑”没有进入下一页。源码检查确认入口原本已是 WPF `Button` 与 `EditTemplatePageCommand` 的绑定，但缺少针对该入口的真实回归，且编辑草稿创建/资源恢复异常只更新了不可见的页面状态，用户会看到“点击无反应”。本轮补齐详情页状态显示、编辑命令异常兜底和 WPF 聚焦回归；回归覆盖按钮命令绑定、正确的 `PageId` 参数、创建所选页面编辑草稿并进入页面设置。该修复不修改源 PSD、源照片或已提交模板修订。

- `dotnet test tests/Desktop.App.UiTests/Desktop.App.UiTests.csproj --no-restore --filter 'FullyQualifiedName~WpfShellTests'`：17/17 通过。
- 最新运行目录：`tmp/tpl-learn-s7-local-app-20260907-fix09`，包含 `Desktop.App.exe`、最新 `Desktop.App.dll` 和 `psd-inspection-worker/psd-inspection-worker.exe`；源码与包内 `Desktop.App.dll` SHA-256 一致：`B55B2CC658D4543A06EBDEF9D31E5BEF674F35151F2A41E1DA459405EE68D426`。
- 该回归仍不等于产品负责人真实视觉走查；用户下一步应关闭旧实例，只启动 fix09，重新打开“模板”→“新模板组”，再点击任一“编辑”。若仍不能进入，详情页应显示具体中文错误，而不是静默无变化。

## WPF 产品负责人最短走查

走查使用应用内部副本，不要直接编辑原始 ZIP 或原始 PSD：

1. 直接启动 `tmp/tpl-learn-s7-local-app-20260907-fix09/Desktop.App.exe`，并使用一个全新的独立数据根；该目录已包含 PSD 检查 worker，不需要 Photoshop 或 ZIP。若机器权限阻止直接运行，再使用 `tmp/TPL-LEARN-01-S7-confirmation-test-package.zip` 作为备用包，并重新指定独立数据根。
2. 进入“模板”→“新建模板组”，点击“导入文件夹”并选择上述目录中的 `奶奶新模板`；确认 6 个 PSD 一次出现在列表中，再用“添加 PSD”追加一个 PSD 并用行内“移除”取消追加项。确认二次确认提示明确说明不会删除源 PSD。
3. 选择“从人工结果学习”，选择参考照片文件夹 `tmp/tpl-learn-review-20260905/source/新建文件夹`，点击“开始学习并复核”。
4. 逐页检查 11 个候选图位：候选图、源照片、窗口范围、整件/细节是否完整；填写简短语义后点击“确认商品框、素材与布局”，或明确点击“保留模板内容”。
5. 对 `G-详情-05-细节.psd` 路径 `4` 重点确认成分/水洗唛图是否应随目标款替换；不要因为尺寸小就自动跳过。
6. 退出校准后重新启动应用，确认学习草稿仍可恢复；确认参考照片只出现在学习复核中，后续目标批次必须重新选择目标款素材。
7. 只有 11 个候选均已明确接受或保留、退出/重启走查通过且 reviewed diff 完成后，才可将证据交给 RENDER-01；本任务仍不生成 Photoshop JPG/PSD 输出。

## 2026-09-07 学习结果语义补齐

此前窗口中“系统暂未确定”只说明 PSD 结构、源照片匹配和几何证据已经提取，但语义用途候选仍为空，用户反馈成立。本轮补齐了本地确定性候选推断：优先使用页面名、图层/智能对象名和源照片名；无命名线索时使用源图尺寸与详情页四宫格位置生成低置信提示，并保留人工确认门。结果不会把低置信推断自动提交为模板规则。

- 真实 6 页、11 个结构候选全部生成非空“未来选图规则”；成分/水洗唛图仍保持逐项确认，不因尺寸小而自动跳过。
- 旧的本机学习会话如果语义为空，恢复时会基于当前页面名和已持久化的参考照片重新补齐，不需要用户重新拖图或重新计算布局。
- Application 学习聚焦测试 22/22；真实学习样本回归 1/1；`Desktop.App` build 0 警告/0 错误；WPF 学习 UI 聚焦 2/2。
- 最新本机运行目录：`tmp/tpl-learn-s7-local-app-20260907-fix09`；隔离数据根需每次重新指定独立目录。该运行目录包含 `psd-inspection-worker`，本轮不需要 Photoshop 或 ZIP。

以上只关闭“候选规则不显示”的实现问题，不关闭 LEARN-S7 产品验收；仍需用户在新运行目录逐框确认文案是否正确、验证放弃/退出不关闭主窗口、重启恢复并完成 reviewed diff。

### 当前走查阻塞

当前源码 Debug 输出目录仍没有安装版 `psd-inspection-worker/psd-inspection-worker.exe`，直接启动 Debug 外壳导入真实 PSD 会停在 `TPL_PSD_WORKER_UNAVAILABLE`；因此必须使用带 worker 的 `tmp/tpl-learn-s7-local-app-20260907-fix09`，而不是 `src/Desktop.App/bin/Debug`。当前会话的 Windows 窗口接管工具初始化失败，尚未形成产品负责人视觉走查证据。该阻塞不要求 Photoshop 或办公室电脑；在最新运行目录上完成产品负责人逐框确认、退出/重启走查和 reviewed diff 后，S7 才能关闭。

## 2026-09-06 交互批准后的快捷确认实现

本轮继续复用 S1–S6 合同和服务，不改变学习状态机。`ReferenceFrameCandidate.IsEvidenceClosed` 汇总结构、可见性、嵌入内容/照片身份、结构签名和 `LayoutIntent` 的闭合证据；`IsAutoConfirmable` 在此基础上要求精确匹配、置信度至少 0.99、用途提示明确且仍为 `Pending`。可能属于模板固定信息的成分/水洗唛说明图即使精确匹配也排除在快捷确认之外。WPF 复核页新增“确认本页合格候选”和“确认全部合格候选”：批量请求只包含上述候选，并原样沿用 PSD 已提取的裁切、缩放、偏移，不要求重新拖图或手工计算。异常候选不被批量操作改写，继续逐项换图、改用途、保留模板内容或转人工校准。

新增/更新聚焦测试：

- `ReferenceLearningServiceTests.AutoConfirmableCandidateRetainsExtractedLayoutWhenBatchReviewed`：高置信确认后 `LayoutIntent` 保持不变并进入 `ReadyToCommit`（仍经过显式复核）。
- `ReferenceLearningServiceTests.BatchConfirmationLeavesLowConfidenceCandidatePendingAndBlocksCommit`：批量只确认精确候选，冲突候选保持 `Pending`，文档保持 `AwaitingReview` 且 `CanCommit=false`。
- `ReferenceLearningServiceTests.RecognizedLayerCueProvidesUsageWithoutManualLayoutReentry`：可明确识别的本地元数据预填用途，不需要重新录入布局。
- `WpfShellTests.ReferenceLearningOffersPageAndBatchConfirmationForQualifiedCandidates`：复核页暴露本页/全部合格候选按钮，分别通过页级和跨页快捷入口提交同一高置信候选。

验证命令与结果：

```text
dotnet test tests/Desktop.Application.Tests/Desktop.Application.Tests.csproj --no-restore --filter 'FullyQualifiedName~ReferenceLearningServiceTests'
通过 18/18

dotnet test tests/Desktop.App.UiTests/Desktop.App.UiTests.csproj --no-restore --filter 'FullyQualifiedName~ReferenceLearning'
通过 2/2

dotnet build src/Desktop.App/Desktop.App.csproj --no-restore
0 警告 / 0 错误
```

以上是代码和自动化证据，不替代真实 6 页 PSD 的本机逐框确认、退出/重启产品走查或 reviewed diff；S7 仍保持进行中。

## 2026-09-07 fix10 编辑草稿失败诊断包

产品负责人使用 fix09 重启后仍无法从模板详情页进入页面编辑。代码路径核对显示：详情页只需读取已提交修订和页面索引；编辑还会新建页面编辑草稿，再通过资源仓库恢复每个页面的内部 PSD 检查记录和预览引用。fix09 对恢复链路中的 `JsonException`、`InvalidDataException`、`FormatException`、资源参数空值或其他未知异常仍显示通用提示，无法区分“模板修订存在”与“编辑草稿恢复失败”。

fix10 将上述异常映射为不泄露路径、凭据和媒体内容的中文行动提示；未知异常仅显示异常类型，便于本机诊断，并明确不会删除模板组。新增回归后：

- `dotnet test tests/Desktop.App.UiTests/Desktop.App.UiTests.csproj --no-restore --filter 'FullyQualifiedName~WpfShellTests'`：18/18 通过；
- `dotnet test tests/Desktop.Application.Tests/Desktop.Application.Tests.csproj --no-restore --filter 'FullyQualifiedName~TemplateWorkspaceServiceTests|FullyQualifiedName~ReferenceLearningServiceTests'`：31/31 通过；
- `dotnet build src/Desktop.App/Desktop.App.csproj --no-restore`：0 警告/0 错误；
- `tmp/tpl-learn-s7-local-app-20260907-fix10/Desktop.App.dll` 与最新源码输出一致，SHA-256：`7B0889F827968CF93121AA7CD377352E81FF655F5B7D13A61FF0066D3E71F18E`；包内含 `psd-inspection-worker/psd-inspection-worker.exe`。

该包只用于取得真实编辑失败分类和继续 S7 走查，不把自动化通过当作产品验收通过；如果 fix10 仍无法进入，下一步依据屏幕上新的具体异常类型修复编辑草稿/资源恢复链路，不要求重新上传用户源 PSD 或转到办公室 Photoshop。

## 2026-09-07 fix11 编辑草稿 SQLite 冲突修复与隔离验证

### 根因证据

对默认本地数据根做只读副本检查，未修改正式数据库：模板组存在 1 个，`template_revisions` 中存在已提交修订 1 和未完成草稿修订 2，`calibration_drafts` 中存在对应页面校准记录。`template_revisions` schema 对 `(group_id, revision_no)` 有唯一约束；旧逻辑再次点击页面“编辑”时使用 `current.RevisionNo + 1`，重新写入修订号 2，故界面显示 `SqliteException`。PSD、源照片和 Photoshop 均不在失败路径中。

### 修复与验证

- 已有同页面编辑草稿：恢复原 `DraftRevisionId`，不重复插入修订；
- 兼容旧版 `calibration_<committedRevision>_<page>` 校准记录，重启后可恢复页面编辑范围；
- 草稿修订已落库但编辑标记未落库时，仅在页面/绑定图与当前已提交修订一致时恢复；另有不同未完成草稿时返回可行动提示；
- `TemplateWorkspaceServiceTests`：9/9；`TemplateRepositoryTests`：9/9，其中包含真实 SQLite 旧记录恢复与重复修订约束回归；
- `WpfShellTests`：18/18；`dotnet build src/Desktop.App/Desktop.App.csproj --no-restore --configuration Debug`：0 警告/0 错误；
- 隔离启动 `tmp/tpl-learn-s7-local-app-20260907-fix11/Desktop.App.exe --data-root=<全新隔离目录>` 保持进程响应并创建隔离 SQLite；包内 `psd-inspection-worker/psd-inspection-worker.exe` 存在；测试完成后未留下 `Desktop.App` 或 worker 进程。

fix11 包内 `Desktop.App.dll` SHA-256 为 `665C936704FEAB34D45312ECFBAE4B86CAE738E36A45C75F06605B89E71E987C`。该修复仅关闭编辑入口的 SQLite 冲突子问题，不关闭 LEARN-S7；仍需产品负责人在 fix11 上完成真实逐框候选确认、取消/退出不关闭主窗口、重启恢复和 reviewed diff。

## 2026-09-07 fix12 人工学习规则兼容回填修复

用户在旧运行目录中观察到：进入统一校准编辑器后，原本人工学习的素材、用途和布局没有显示。截图时间为 19:40；该截图对应 fix12 生成前的旧包，不能作为 fix12 失败证据。

根因是人工学习提交的绑定使用 `learn_frame_*`，统一校准编辑器根据当前 PSD 图层生成 `frame_*`；旧逻辑只做图片框 ID 精确匹配。另外，旧页面编辑草稿可能把校准状态保存在已提交修订号下，当前编辑草稿没有读取该兼容位置。

fix12 已完成：

- 按页面、图层 ID、图层路径和结构签名兼容匹配人工学习绑定；
- 当前草稿没有状态时兼容读取旧已提交修订的校准状态，并迁移到当前编辑草稿；
- 回填参考素材、图片框用途、缩放、水平/垂直偏移和布局；
- 修改时保留 `reference_only` 等人工学习元数据，不重复创建绑定；
- 参考图片文件不在本机时仅显示历史名称，不把它误报为当前错误预览。

自动化结果：规则恢复聚焦 15/15；Infrastructure 215 通过、2 个环境跳过；真实 SQLite 旧校准状态迁移回归通过；WPF 18/18；`Desktop.App` build 0 警告/0 错误；Application 169 通过。fix12 运行目录为 `tmp/tpl-learn-s7-local-app-20260907-fix12`，包内 `Desktop.App.dll` SHA-256 为 `6466A445D0D9A97CF0E1729BE2A189EFF75980A7F6F68C39805B3D2BB27DF5AF`，并包含已验证的 `psd-inspection-worker`。

该记录仍不关闭 LEARN-S7。产品负责人下一步使用 fix12 打开已有模板，确认人工学习规则自动回填、切换图片框不丢失、修改后保存、关闭重开仍在、参考款素材不会进入目标款，并完成逐框 reviewed diff。

## 2026-09-07 fix13 全量编辑模板组与独立另存为

本轮落实产品负责人批准的第二种编辑语义：从已有模板组详情页点击“编辑模板组”时，不再把已提交页面视为只读；服务创建全量编辑草稿，全部既有页面、规则和内部 PSD 资源都属于同一可编辑范围。原来的“编辑页面”和“添加模板页面”边界保持不变：前者只打开所选页面，后者只允许新增页面。

验证内容：

1. `TemplateDraftSession.IsFullTemplateEdit`、`BeginFullEditAsync` 和全量草稿页面范围回归通过；Application `TemplateWorkspaceServiceTests` 为 `10/10`。
2. 真实 SQLite 与文件存储回归通过：`CommitDraftAsNewGroupAsync` 为页面、资源和图片框生成新 ID，复制应用内部 PSD，保留 `SelectionRequirement`/`LayoutIntent`，原模板组当前修订、原资源路径和原 PSD 字节均未改变；Infrastructure `TemplateRepositoryTests` 为 `11/11`。
3. WPF 详情页“编辑模板组”按钮绑定和命令进入页面设置回归通过；校准页新增“另存为”按钮仅在全量编辑草稿可用；WPF 聚焦为 `2/2`。
4. `Desktop.App` 构建为 `0` 警告、`0` 错误。隔离包 `tmp/tpl-learn-s7-local-app-20260907-fix13` 包含 `psd-inspection-worker/psd-inspection-worker.exe`；`Desktop.App.dll` SHA-256 为 `07F542965B6482B2F2C98B8982BB627388BB7D6DACD0EDF8E90568A215CCF2A9`。使用新的隔离数据根启动 4 秒保持响应，测试结束后没有残留 `Desktop.App` 进程。

保存语义：点击“保存模板组”提交原模板组的新修订；点击“另存为”生成新的独立模板组，原模板组不被替换。两者都不改用户源 PSD、源照片，也不需要重新上传 PSD。

本轮仍不能关闭 LEARN-S7。待产品负责人使用 fix13 在本机真实窗口完成全量编辑、逐框规则确认、切换/保存不丢失、取消/退出恢复、重启恢复和 reviewed diff；办公室 Photoshop 与 ZIP 不属于本轮前置条件，正式 JPG/PSD 输出仍由 RENDER-01 验收。

## 2026-09-07 fix14 规则输入框可编辑

### 用户反馈与根因

产品负责人反馈：已有模板进入统一校准编辑器后，右侧“图片框用途（未来选图规则，可修改）”显示为灰色，无法把“商品展示图，与参考款保持同一视角”改成“侧面图”。代码检查确认，`CalibrationDraft` 使用私有 setter 在异步加载后赋值，但 `CanEditReferenceLearningRule` 的 WPF 绑定没有同步刷新；因此输入框保留了初始空草稿的禁用值。人工学习复核与拖拽校准的保存逻辑本身均使用同一 `SelectionRequirement`/`ReferenceFrameReview` 规则合同，问题是 UI 权限通知缺失。

### 修复与验证

- 校准草稿加载、素材绑定、图片框切换和校准界面切换后，显式刷新 `CanEditReferenceLearningRule`；
- 新增 `ManualCalibrationRuleFieldBecomesEditableAfterDraftLoads`：异步草稿加载后规则框可编辑，并可输入“侧面图”；
- 既有人工学习复核 WPF 回归增加规则输入框可编辑断言；
- `dotnet test tests/Desktop.Application.Tests/Desktop.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~TemplateCalibrationServiceTests|FullyQualifiedName~ReferenceLearningServiceTests|FullyQualifiedName~ReferenceLearningCommitServiceTests|FullyQualifiedName~TemplateWorkspaceServiceTests"`：47/47；
- `dotnet test tests/Desktop.App.UiTests/Desktop.App.UiTests.csproj --no-restore`：20/20；
- `dotnet publish src/Desktop.App/Desktop.App.csproj --no-restore -c Debug -r win-x64 --self-contained false -o tmp/tpl-learn-s7-local-app-20260907-fix14`：0 警告/0 错误；包内含 `psd-inspection-worker/psd-inspection-worker.exe`，`Desktop.App.dll` SHA-256 为 `552046A6D737210FAE9CCF7A69B636D6741BD660F5FC821267EDD171AD75F3C6`；
- 使用全新隔离数据根启动 fix14，4 秒后进程保持响应，结束后 `Desktop.App` 进程数为 0。

### 仍待产品负责人操作

fix14 只关闭“规则框无法编辑”的代码问题，不关闭 LEARN-S7。产品负责人应在本机直接启动 `tmp/tpl-learn-s7-local-app-20260907-fix14/Desktop.App.exe`，打开已有模板组进入编辑，在当前图片框规则框输入“侧面图”，点击“应用并确认当前规则”，切换其他图片框再切回确认文本仍在，点击“保存模板组”后检查新修订；随后继续逐框确认、取消/退出恢复、重启恢复和 reviewed diff。该走查不要求办公室 Photoshop 或 ZIP。

## 2026-09-08 fix14 产品负责人六项本机走查结果

产品负责人已确认 fix14 规定的六项本机走查均完成且没有发现问题：

1. 已从已有模板组进入统一编辑器；
2. 规则输入框可编辑，并成功将当前用途改为“侧面图”；
3. 点击“应用并确认当前规则”成功；
4. 切换到其他图片框再切回后，已修改规则仍然保留；
5. 点击“保存模板组”成功生成新修订；
6. 关闭并重新启动后，规则仍可恢复显示。

这关闭了 fix14 的规则编辑、确认、切换保持、保存和重启持久化子门。它不等于 `LEARN-S7` 整体通过：11 个真实候选仍需逐框完成用途/替换或保留模板内容的最终确认，随后还需完成取消/退出草稿恢复、参考款与目标款隔离核对及 reviewed diff。以上确认在本机完成，不需要办公室 Photoshop 或 ZIP。

## 2026-09-08 fix15 页面切换图片框列表刷新与异步竞态保护

### 用户可见问题与根因

在统一模板校准编辑器中切换页面时，中间预览和右侧检查器已经切换，但左侧“图片框”列表仍可能显示上一页的图层名称。根因是 `CalibrationFrameItems` 是由 `CalibrationDraft` 计算得到的绑定属性，草稿更新后没有发出该属性的变更通知；同时，快速连续切页时，较早发起的异步 PSD 校准加载可能晚于当前页返回并覆盖当前状态。

### 修复内容

- `UpdateCalibrationInputs` 更新草稿时显式刷新 `CalibrationFrameItems`，并重新应用当前页有效的选中图片框；
- 页面校准加载使用单调递增的请求版本，同时校验当前页面、当前草稿和校准界面，旧页面结果不能覆盖新页面；离开校准界面时使未完成加载失效；
- 新增 WPF 回归：页面草稿切换后列表数量、图层名称和选中项同步更新；延迟加载乱序返回时只接受当前页面结果。

### 验证证据

- `dotnet test tests/Desktop.App.UiTests/Desktop.App.UiTests.csproj --no-restore --configuration Release`：22/22 通过；
- `dotnet test tests/Desktop.Application.Tests/Desktop.Application.Tests.csproj --no-restore --configuration Release --filter "FullyQualifiedName!~SizeTableGoldSampleTests"`：170/170 通过；
- `dotnet build src/Desktop.App/Desktop.App.csproj --no-restore --configuration Release`：0 警告、0 错误；
- `dotnet publish src/Desktop.App/Desktop.App.csproj --no-restore -c Debug -r win-x64 --self-contained false -o tmp/tpl-learn-s7-local-app-20260908-fix15`：0 警告、0 错误；包内含 `Desktop.App.exe` 和 `psd-inspection-worker/psd-inspection-worker.exe`；
- 包内 `Desktop.App.dll` SHA-256：`644F8D24AE82AD7547574AE73B74D71790B1987D99D3AFB7449EEFA2F92ECE7B`；worker SHA-256：`44E67931082283D891B95E52100F821513CACC9ED845FAFDA78BA2CEEA09EEBF`；
- 使用独立数据根启动 fix15 5 秒，进程保持响应，结束后剩余 `Desktop.App` 进程数为 0，未在正式数据根写入状态。

完整解决方案回归中另有两个与本次无关的 SIZE-01 真实金标测试因仓库当前缺少 `需求样本/SIZE-01金标/真实来源/size-real-a-577fa4d19f919a7a.jpg` 和 `size-real-b-9231ad299120bba6.jpg` 而失败；未补造或下载样本。该环境问题不影响本次 TPL-LEARN-01/UI 修复证据。

### 下一步

产品负责人使用 `tmp/tpl-learn-s7-local-app-20260908-fix15/Desktop.App.exe` 在本机已有数据上继续：进入已有模板组的“编辑模板组”，直接点击“开始校准”，切换两个页面确认左侧图片框名称与当前页面一致，再继续 11 个候选的最终确认、取消/退出恢复、参考款/目标款隔离和 reviewed diff。此步不需要重新导入 PSD、ZIP 或办公室 Photoshop；LEARN-S7 仍未整体关闭。

## 2026-09-08 fix15 产品负责人本机走查结果

产品负责人确认 fix15 的页面切换走查已通过：从已有模板组进入“编辑模板组”后直接开始校准，切换不同页面时左侧图片框列表跟随当前页面，没有继续显示上一页内容；统一规则框可以修改并保存，未发现问题。

本次确认只关闭页面列表刷新、规则编辑和保存的产品子门。`LEARN-S7` 仍保持进行中，尚未替代以下证据：11 个候选逐框最终选择、取消/退出草稿恢复、参考款与目标款隔离核对以及 reviewed diff。完成这些项目之前，不进入办公室 Photoshop 的正式输出验收，也不把规则交接给 `RENDER-01`。

## 2026-09-08 fix16 有效商品层、逐框规则隔离与打包 worker

### 修复口径

- 每个图片框独立保存参考素材、用途、缩放和水平/垂直偏移；A→B→A 切换不会把 B 的规则带回 A。
- worker 为智能对象计算自身非透明像素和“从完整页面移除该层后”的最终合成像素差；只有嵌入内容与源照片 SHA-256 精确匹配且两类像素证据均非零的有效可见层进入候选。
- 隐藏层、完全被遮挡的旧层、外链、普通像素层、装饰和证据不完整内容不登记；同一明确剪贴窗口中的连续重叠层只保留最终有效商品层。图层名只用于生成用户可修改的用途提示，不参与候选资格判断。
- 成分/水洗唛属于商品随款变化的信息图；只要同样满足内容与最终显示闭合，继续登记为可替换框。
- `reference-learning/v1` digest 和 JSON Schema 纳入像素证据。旧会话可只读恢复，但缺少像素证据时不能直接新提交，必须重新分析；已提交模板修订不受影响。

### 打包依赖纠正

首次仅使用旧最小运行依赖构建的候选在 G-详情-01 的渐变叠加重合成处触发 `ImportError: Gradient fills require: scipy`。该候选立即废弃；这不是 PSD 损坏，也没有要求用户另存 PSD。随后按 `psd-tools[composite] 1.18.0` 补齐并锁定 `aggdraw 1.3.19`、`scipy 1.15.3`、`scikit-image 0.25.2`、`ImageIO 2.37.4`、`lazy-loader 0.5`、`networkx 3.4.2`、`packaging 26.3` 和 `tifffile 2025.5.10`，更新 `requirements.lock` 后重建。最终 onedir worker 为 `1,193` 个文件、`166,051,888` bytes；EXE SHA-256 为 `7C0E7E6B8AF86EBE00474452616EB475E5B45070FBE0E9ABFD55945FB35686A3`。

### 真实 6 页结果

| PSD | 有效框路径 | 数量 | worker 时间 | 峰值工作集 |
| --- | --- | ---: | ---: | ---: |
| G-详情-01 | `4` | 1 | `30,537.988 ms` | `434,061,312` bytes |
| G-详情-03 | `3` | 1 | `18,894.050 ms` | `381,521,920` bytes |
| G-详情-04-面料 | `5` | 1 | `54,864.369 ms` | `437,858,304` bytes |
| G-详情-05-细节 | `4/9/14/19` | 4 | `34,067.506 ms` | `354,779,136` bytes |
| H-模特图-01 | `5/11` | 2 | `51,583.334 ms` | `537,825,280` bytes |
| H-模特图-02 | `5/11` | 2 | `42,179.494 ms` | `581,738,496` bytes |

合计 11 个有效框，总墙钟时间 `240,674.753 ms`。详情05四框分别匹配成分/水洗唛、领口、袖口和主图源照片；每个候选的 `RenderedPixelCount` 与 `CompositeContributionPixelCount` 都大于 0。包内 worker 再单独运行详情05，50 层、12 个智能对象中仍只有路径 `4/9/14/19` 四个最终贡献层，耗时 `44,143.071 ms`、峰值 `354,402,304` bytes。

### 构建、回归与运行目录

- `dotnet build EcommerceDetailPage.Desktop.slnx -c Release`：0 警告、0 错误。
- 完整 solution：Domain 49/49、Contracts 48/48、Architecture 5/5、WPF 23/23、Infrastructure 217 通过/2 个显式环境探测跳过；Application 175 项通过，另两项只因仓库缺少 SIZE-01 外部金标 `size-real-a-577fa4d19f919a7a.jpg`、`size-real-b-9231ad299120bba6.jpg` 失败。未补造样本，TPL-LEARN 聚焦回归无失败。
- `dotnet format EcommerceDetailPage.Desktop.slnx --verify-no-changes --no-restore`、`git diff --check`、worker `compileall`、锁定环境 `pip check`、变更行凭据扫描和禁止媒体/数据库/二进制进入 Git 检查均通过。
- 运行目录：`tmp/tpl-learn-s7-local-app-20260908-fix16`；`Desktop.App.dll` SHA-256 为 `4BD19BAF8971C218D1E37D67B77F2BC8E7CF7CB48A72DC845B787984B10DA918`，worker EXE SHA-256 同上。完整目录 `1,217` 个文件、`172,968,085` bytes。
- 使用全新 `tmp/tpl-learn-s7-local-data-20260908-fix16-smoke` 启动 5 秒，主程序保持响应并仅在隔离根创建 SQLite；强制结束 smoke 后剩余 `Desktop.App`/worker 进程为 0。

### fix16 当时的产品走查要求（已完成本地复核）

产品负责人已在本机启动 fix16，复用已有模板组的应用内部 PSD，重新选择“从人工结果学习”和源照片文件夹，并完成重新分析与复核，未发现有效框、逐框切换、修改或复核流程问题。该结果仅确认 fix16 本地能力；由于后文识别出 AI 动态语义与不同目标款整组匹配尚未实现，原计划中的 S7 收口和 RENDER-01 交接不再执行。本轮仍不需要 Photoshop、办公室电脑或 ZIP。

技术顾问已审查 fix16 完整 diff，重点覆盖候选资格、像素证据、逐框输入隔离、旧会话兼容、模板修订/源文件保护、异常边界和 composite 依赖闭包；未发现阻止其本地产品走查的问题。该 reviewed diff 只覆盖 fix16 范围，不覆盖后续新增的 AI 动态语义与目标款匹配实现。

## 2026-09-08 产品复核与阶段回退结论

产品负责人在 fix16 中重新执行分析并完成复核，确认当前本地结果可用。随后正式冻结以下补充要求：

1. 每个图片框的规则必须说明未来目标款应选什么图，并足以区分参考组实际存在的相似照片；不能使用固定的“正面/背面/领口/袖口”词表。若实际差异是坐姿、站姿、背身、景别、构图或其他特征，规则必须动态表达这些差异。
2. 本地继续负责有效层资格、SHA-256 参考照片身份和 PSD 几何；AI 在用户未充分手写时结合整组参考照片生成开放式 `selection_requirement`，不得用本地启发式冒充 AI 学习结果。
3. 后续目标款联合分析整组照片；同一张照片允许用于多个合理图片框，各框裁切、缩放和偏移独立。没有精确候选时允许相近替代：安全高置信且差异不影响用途可绿，存在实质差异或模型明确语义冲突为黄；候选分差或同分本身不降级；错款/错变体/无关/参考款引用/无安全候选为红（2026-09-13 按 ADR-030 修订）。
4. 系统应借助整组上下文、动态提示词、候选分差和主体证据尽量标绿，但不得降低严重错误或参考款隔离门。

实现核对结论：当前 `reference-learning/v1` 只承载现有语义字段和本地候选，fix16 没有该要求对应的真实 AI 调用、版本化动态语义证据或不同目标款整组匹配金标。因此原“只剩 S7 产品签字”的路由无效；保留本记录已证明的本地子门，先回补 LEARN-S3，再以新测试包重验受影响的 S4-S7。完成前不得交接 RENDER-01，也不得将本记录解释为 TPL-LEARN-01 已完成。

## 2026-09-08 文档基线同步验证

本次只同步 PRD、ADR-021、TPL-LEARN-01 任务书、总体技术方案、验收矩阵、当前项目状态、项目总览和本验证记录；未在本次文档更新中修改代码、Schema 或用户媒体，工作树中原有 fix16 代码/Schema 改动保持不变。

- `git diff --check -- <上述 8 份文档>`：退出码 0；仅输出仓库现有 LF/CRLF 转换提示，无空白错误。
- PowerShell 关键合同扫描：`开放式动态`、`同图多框`、`相近替代`、`LEARN-S3`、`目标款整组`、`参考款` 均在更新后的基线中存在。
- 活跃旧路由扫描：未发现旧的“S0-S6 全部通过后只做 S7”或“继续使用 fix16 收口”等文案仍被当作当前状态。
- `TPL-LEARN-01` 验收矩阵检查：恰好 1 个任务行、6 个字段，状态为 S3 AI 动态语义未完成并要求重验 S4-S7。

## 2026-09-09 模板索引恢复与隔离导入验证

发现默认数据根中服装模板的 6 个内部 PSD、`revision-manifest.json`、marker 和已提交学习会话仍存在，但 `template_groups`/`template_revisions` 索引缺失；默认库因此只显示 `S5-圆环女包验证`。未修改默认数据库、原始 PSD、源照片或正式输出。

使用只读内部 PSD 与既有 `Committed` 学习会话生成恢复包：`tmp/recovered-clothing-template-20260909/新模板组-恢复.tplpkg`。首轮包校验发现恢复脚本漏写 `sha256:` 结构签名前缀，已修复脚本并重新生成；最终 `template-package/v1` 校验通过，包含 6 个 PSD、6 个页面和 10 个已确认图片框（页面绑定数 `1/1/1/4/1/2`），包 SHA-256 为 `C83E66CE820D43374B2E82F33AA8A75EF6A04426CAB6BFB348EBC558CBA385C1`。

在全新隔离数据根 `tmp/recovered-clothing-template-20260909/isolated-data` 中实际执行导入，结果为 `Committed`，生成新本地 group/revision 身份，6 页和 10 个绑定均可重读；PSD 长度与 SHA-256 与内部副本一致。该隔离数据根仅用于本机后续走查，不代表 TPL-LEARN-01 的不同目标款整组准确率已通过。

## 2026-09-13 真实目标款 WPF 分析、人工锁图与进程重启复核

### 范围与输入

本次使用最新隔离包 `tmp/tpl-learn-20260913-transport-fix-debug-app/Desktop.App.exe`，模板组为 `测试`（6 个已校准页面），没有使用草稿模板组 `111`。新建批次和隔离输出如下：

- `batch_id=` `batch-9f7b115defbb4563a26471089f479098`；`style_id=` `style_78caace23ecc18c3bf5ab1856b101c1e`；UI 显示时间约为 2026-09-13 02:31（北京时间）；
- 输入目录：`C:\Users\79122\xwechat_files\wxid_ooplq7l3oskg22_55b3\msg\file\2026-09\简唏7707-75\简唏7707-75`；只读核对得到 24 张 PNG/JPG，SQLite 中 24 个 asset、24 个不同源 hash；
- 输出目录：`tmp/tpl-learn-20260913-transport-fix-ui-output-01`；本次未点击“开始生成”，目录递归文件数为 0；没有向源 PSD、源照片或旧批次写入文件。

### 真实请求与结果

本批次只发起 1 次 `ai-analysis/v1`，没有重试：

- request：`ai-batch-9f7b115defbb4563a26471089f479098-style_78caace23ecc18c3bf5ab1856b101c1e`；最终 UTF-8 payload `8,401,443` bytes，assets `24`，requirements `11`；
- `upload_completed=true`，`upload_bytes_written=8,401,443`；HTTP `200`；provider request reference 为 `02178923789931088a480d877f824556f2559e19636edcd2af52f`（结果 JSON 的 `provider_request_ref` 带 `resp_` 前缀）；响应体 `7,847` bytes；
- model config：`doubao-seed-2.1-pro-260628-output4096-v2`；SQLite usage 为输入 `33,568`、输出 `827`，`cache_hit=0`，`retry_count=0`，provider cost 字段为空（应用侧估算值不作为账单事实）；
- 结果 JSON 通过 `ai-analysis-result/v1`：11 个匹配中 `exact=9`、`safe_similar=2`，两个相近候选置信度为 `0.80` 和 `0.78`；唯一警告为 `no_full_back_person`（没有人物背面全身图，使用服装背面平铺图作为安全相近替代）。

### WPF 复核与人工锁图

款式检查页显示 6 个页面、11 个图片框、24 张照片；总览文案为“24 张素材 · 6 / 6 页面已分析”“已完成 1/1 款 · 可生成页面 0/6”“待人工复核”，汇总为“可生成 0 / 待检查 1 / 不可生成 0”。初次打开时 11 个框均显示“AI 推荐 · 已选用”（绿色）；这只证明候选已投影到复核页，不等于整组可生成。

对 `page_1 · 图片框 1` 执行一次“替换 → 照片 03 → 改用这张”后，界面明确提示已改用并锁定。返回总览再重新打开款式检查页，观察到：该框变为“人工选用 · 已锁定”，当前照片为照片 03；其余 10 个框仍为“AI 推荐 · 已选用”，没有重新发起 AI 请求。SQLite 对应锁定记录为：

`learn_frame_417b5d982ce7450824f39617` → `asset_3f2625eece0e07758c1ebbfeb1f50e4b`，`selection_revision=1`，session/ledger 终态均为 `ManualReview`。

### 进程重启持久化

在上述选择已提交后，先正常关闭旧包 PID `37192`，再从同一路径启动新进程 PID `51072`；启动约 4 秒后 `Responding=True`，窗口标题为“电商详情页智能生产系统”。重启前后只读核对 `state/desktop-v1.db`：`integrity_check=ok`，同一 `ManualReview` 会话仍保留上述锁定 JSON、`selection_revision=1` 和 `has_verified_result=1`；ledger 仍为 `ManualReview`，usage 仍为单次、无重试，传输日志总行数保持 143，未产生第二次 AI 请求。

本次 WPF 控制面按规程重试后仍返回 `Trusted RPC service is not configured: sky`，因此重启后的“再次打开款式检查页”没有新的可截图视觉证据；上段的页面重新打开证据来自同一进程内刷新，进程级重启证据来自窗口响应、SQLite 和日志只读核对。不得将后者表述为完整的重启后视觉走查。

### 阶段结论与未关闭门

本次关闭了新包真实传输、结果闭合、目标款一次人工锁图和进程重启后锁定记录持久化这些子门；没有生成 PSD/JPG，也没有交接 `RENDER-01`。`LEARN-S4` 至 `LEARN-S7` 仍保持进行中，尚缺：全部 11 框的逐框业务语义确认、完整绿/黄/红准确性（含同图多框和主体适配）金标、取消/中途退出恢复、重启后的视觉确认、renderer-neutral `RenderPlan` 参考零引用和 reviewed diff。下一步只能在可用的原生窗口控制面或由产品负责人完成上述剩余走查；不得点击“开始生成”或重发本请求。
