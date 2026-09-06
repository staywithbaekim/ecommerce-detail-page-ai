# TPL-LEARN-01 LEARN-S7 真实样本逐框、恢复与隔离记录

## 结论

本记录完成了 LEARN-S7 可由自动化和只读样本核对完成的部分，但不把任务标记为整体通过。已登记的 6 页人工完成 PSD 在隔离副本上逐框核对为 11 个结构候选，全部与参考源照片逐字节精确匹配；但“有图”不等于“都是当前商品照片位”：其中右下成分图虽尺寸较小，确实填入了“详细水洗唛/成分材质”图片框，另有一格使用了深色款袖口素材，和同页红色款主体存在跨款风险。是否作为未来商品图位、是否保留固定内容以及是否更换该细节图，均由人工复核决定。退出/重启恢复、旧层证据持久化和参考款/目标款隔离均有自动化证据。

产品负责人仍需在实际 WPF 窗口中逐框确认语义、裁切和位置，并完成退出/重启走查。当前 worktree 的 Debug 输出本身尚未包含安装版 `psd-inspection-worker.exe`，但已生成带 worker 的隔离测试包 `tmp/TPL-LEARN-01-S7-folder-management-test-package.zip`，因此不需要 Photoshop 或办公室电脑即可在本机完成 TPL-LEARN-01 的真实 PSD 走查；办公室 Photoshop 仅属于后续 RENDER-01。

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

## WPF 产品负责人最短走查

走查使用应用内部副本，不要直接编辑原始 ZIP 或原始 PSD：

1. 解压并启动 `tmp/TPL-LEARN-01-S7-folder-management-test-package.zip` 中的 `Desktop.App.exe`。该包已包含 PSD 检查 worker；建议使用应用提供的隔离数据根，避免污染正式本地数据。
2. 进入“模板”→“新建模板组”，点击“导入文件夹”并选择上述目录中的 `奶奶新模板`；确认 6 个 PSD 一次出现在列表中，再用“添加 PSD”追加一个 PSD 并用行内“移除”取消追加项。确认二次确认提示明确说明不会删除源 PSD。
3. 选择“从人工结果学习”，选择参考照片文件夹 `tmp/tpl-learn-review-20260905/source/新建文件夹`，点击“开始学习并复核”。
4. 逐页检查 11 个候选图位：候选图、源照片、窗口范围、整件/细节是否完整；填写简短语义后点击“确认商品框、素材与布局”，或明确点击“保留模板内容”。
5. 对 `G-详情-05-细节.psd` 路径 `4` 重点确认成分/水洗唛图是否应随目标款替换；不要因为尺寸小就自动跳过。
6. 退出校准后重新启动应用，确认学习草稿仍可恢复；确认参考照片只出现在学习复核中，后续目标批次必须重新选择目标款素材。
7. 只有 11 个候选均已明确接受或保留、退出/重启走查通过且 reviewed diff 完成后，才可将证据交给 RENDER-01；本任务仍不生成 Photoshop JPG/PSD 输出。

### 当前走查阻塞

当前 Debug 输出目录仍没有安装版 `psd-inspection-worker/psd-inspection-worker.exe`，直接启动 Debug 外壳导入真实 PSD 会停在 `TPL_PSD_WORKER_UNAVAILABLE`；因此必须使用带 worker 的 `TPL-LEARN-01-S7-folder-management-test-package.zip`，而不是把 Debug 目录当作完整安装包。该阻塞不要求 Photoshop 或办公室电脑；在该测试包上完成产品负责人逐框确认、退出/重启走查和 reviewed diff 后，S7 才能关闭。

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
