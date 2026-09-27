# SIZE-01 SIZE-S7 真实金标与 O-03 走查记录

## 1. 当前结论

2026-09-02 已完成两张真实市场尺码图的正式逐格回归，以及动态尺码页 O-03 编辑弹层的自动化 WPF 窗口走查候选。

- 真实金标：2/2 通过；逐格结构与人工核对一致，原文 `/`、小数和整数均保留；来源文件 SHA-256 与 SIZE-S0 收口值一致。
- O-03 窗口候选：1/1 通过；弹层在 1440×900 与 1100×760 下边界闭合，来源参考/结构化表格/模板样式预览均可见；合法 `\\` 可编辑，真正空值禁用保存。
- 本记录不把自动化窗口测试写成产品负责人视觉签字；125% DPI 实机确认、中文 IME/键盘/UIA 的动态弹层专项走查，以及真实模板 JPG/PSD 联合验收仍开放。

因此 `SIZE-01` **仍未整体通过**。

## 2. 真实金标逐格回归

执行测试：

```text
dotnet test tests/Desktop.Application.Tests/Desktop.Application.Tests.csproj --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~SizeTableGoldSampleTests" --verbosity minimal
结果：2 通过、0 失败、0 跳过。
```

测试读取 Git 忽略目录中的真实文件，先以 SHA-256 校验字节未变，再把人工逐格核对结果送入正式 `SizeTableService`。测试不上传、复制、改写或提交图片。

### 样本 A

文件：`需求样本/SIZE-01金标/真实来源/size-real-a-577fa4d19f919a7a.jpg`

- 长度：98,392 bytes
- SHA-256：`577fa4d19f919a7a7bbd64f07daa1102a34642c470e0967364f44543271a9e2b`
- 列：`尺码 | 衣长 | 胸围 | 肩宽 | 袖长`
- 行：
  - `L | 61 | 102 | 40 | /`
  - `XL | 62 | 107 | 41 | /`
  - `2XL | 63 | 112 | 42 | /`
  - `3XL | 64 | 117 | 43 | /`
  - `4XL | 65 | 122 | 44 | /`
  - `5XL | 66 | 127 | 45 | /`

### 样本 B

文件：`需求样本/SIZE-01金标/真实来源/size-real-b-9231ad299120bba6.jpg`

- 长度：239,126 bytes
- SHA-256：`9231ad299120bba65af9391ba8a59097b7ff169197810a38971621df886d9fb0`
- 列：`尺码 | 胸围 | 肩宽 | 袖长 | 衣长 | 袖口`
- 行：
  - `L | 99 | 39.5 | 51.5 | 64 | 28`
  - `XL | 103 | 40.5 | 52.5 | 65 | 29`
  - `2XL | 107 | 41.5 | 53.5 | 66 | 30`
  - `3XL | 111 | 42.5 | 54.5 | 67 | 31`
  - `4XL | 115 | 43.5 | 55.5 | 68 | 32`
  - `5XL | 119 | 44.5 | 56 | 68.5 | 33`

实现证据见 `tests/Desktop.Application.Tests/SizeTableGoldSampleTests.cs`。这项测试验证的是“真实来源 hash + 已核对结构进入正式领域/应用合同”；它不是 OCR 准确率或真实 provider 请求证据。

## 3. O-03 WPF 窗口走查候选

执行测试：

```text
dotnet test tests/Desktop.App.UiTests/Desktop.App.UiTests.csproj --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~DynamicSizeTableEditorRendersSourceGridAndTemplatePreviewAtWideAndNarrowSizes" --verbosity minimal
结果：1 通过、0 失败、0 跳过；实际耗时约 11 秒（测试主体约 7 秒）。
```

覆盖内容：

- 从动态尺码页结果详情打开“编辑尺码数据”；
- 来源只读预览、结构化表格、模板样式预览和保存按钮均存在且可见；
- 第一张真实金标的 `/` 原样呈现；改为 `\\` 后保存按钮可用；改回真正空字符串后保存按钮禁用并显示不能保存；
- 弹层在 1440×900 和 1100×760 下均未越出窗口边界；
- 当前测试环境实际 DPI 由 WPF `VisualTreeHelper.GetDpi` 读回，未通过修改系统缩放设置制造 125% 环境。

既有 Shell 自动化还覆盖当前环境的键盘焦点、中文输入和 UIA；本轮以 `DESKTOP_EXPECTED_DPI_PERCENT=100` 复跑 `ShellSupportsWideNarrowUiaFocusAndChineseText`，结果 1/1 通过。动态尺码弹层在 125% DPI 下的实机专项和产品负责人视觉确认尚未完成，不能把本候选写成完整 S7 通过。

## 4. 未关闭的 S7 门

1. 产品负责人对 O-03 实际弹层的视觉确认，包含 1440×900、1100×760、100%/125% DPI、中文 IME、键盘和 UIA；
2. 以真实已校准模板和 Photoshop/UXP v5 执行真实动态尺码页，确认 JPG/PSD 原子输出和失败不覆盖旧输出；
3. 完成 reviewed diff、上游文件安全审查和最终验收矩阵同步。

在上述证据齐全前，保持验收矩阵中的 `SIZE-01` 为“进行中”。

## 5. 本轮完整回归

```text
dotnet build EcommerceDetailPage.Desktop.slnx --no-restore -p:NuGetAudit=false --verbosity minimal
结果：0 警告、0 错误。

dotnet test EcommerceDetailPage.Desktop.slnx --no-restore -p:NuGetAudit=false --verbosity minimal
结果：456 通过、1 跳过、0 失败；跳过项为默认关闭的 opt-in RealProbeThroughFormalAdapter，本轮未联网。

dotnet format EcommerceDetailPage.Desktop.slnx --no-restore --verify-no-changes --verbosity minimal
git diff --check
结果：均通过。

## 6. 2026-09-03 外部联合验收条件复核

只读检查发现一份真实尺码表模板 PSD 候选：

```text
C:\Users\79122\Documents\ChatGPT\全新实现\需求样本\第一组\模板PSD\F-尺码表-【L-5XL】.psd
bytes: 4,151,145
SHA-256: 6CFF7EB29E1929E2294338AA6E05CF4B0520A45DF63A8718350F632ADEB81E05
```

本机当前 `Photoshop` 进程不存在，常见 `Program Files`/Adobe 安装目录和 `Photoshop*.exe` 命令均未发现可执行文件。因此本轮没有打开或改写 PSD，没有生成 JPG/PSD 输出，也没有执行 Photoshop/UXP 联合验收。该外部条件是 SIZE-01 完成门的剩余阻塞，不以自动化测试或模板文件存在替代。
```

## 7. 2026-09-14 办公室环境只读启动复核

本轮在办公室电脑完成了构建、进程和原生窗口控制面复核，未打开或修改任何 PSD/JPG/PNG，也未启动 `RENDER-01`。

- `.NET SDK`：`10.0.400`；`src/Desktop.App/Desktop.App.csproj` 存在；此前本轮 restore/build 结果为 `0` 警告、`0` 错误。
- `Desktop.App`：PID `14596`，窗口标题“电商详情页智能生产系统”；原生 UIA 可读回 `DesktopMainWindow`、`PrimaryNavigation`、`PageTitle`、`PrimaryPageAction`、`ActiveTaskList`、`RecentTaskList` 以及最小化/最大化/关闭按钮。
- 当前 WPF 窗口读回外框 `1440×844`、`96 DPI`（100%）；通过“恢复上次批次”仅进入“新建批次/扫描前设置”，未选择模板、输入目录、输出目录或页面，随后已取消并返回工作台。该路径没有读取素材文件。
- Photoshop 2025：路径 `G:\夸克网盘\Adobe Photoshop 2025\Photoshop.exe`，PID `15660`，窗口标题“Adobe Photoshop 2025”；原生 UIA 可读回菜单和标题栏，截图显示欢迎/最近使用页，没有打开文档或文档标签。
- 已在仓库外建立 `F-尺码表-【L-5XL】-readonly-copy.psd` 工作副本；源文件与副本 SHA-256 均为 `6CFF7EB29E1929E2294338AA6E05CF4B0520A45DF63A8718350F632ADEB81E05`。Photoshop 曾打开系统文件选择对话框但未选中文件，随后已关闭；没有打开 PSD、保存修改或生成输出。
- 125% 实机复核：Windows 显示缩放切换后，`Desktop.App` 与 Photoshop 主窗口 `GetDpiForWindow` 均读回 `120`（125%）；WPF UIA 树仍可读取。当前桌面存在其他应用通知窗口遮挡，且未配置可直接进入 O-03 的真实批次，因此本轮未进行弹层点击，不把该结果写成 O-03 视觉通过。
- 125% 子 agent 复核补充：WPF 截图逻辑约 `1427×838`（外框约 `1440×844`），未见明显裁切；`RecentTaskList` 暴露的任务行是无 Button/Command 的 `Border`，UIA 与坐标点击“查看结果”均未导航，任务页显示“尚无任务”。当前 fixture 没有可进入 O-03 的控件入口，因此无法打开动态尺码结果详情/弹层；该阻塞不作为 O-03 通过证据。
- 办公室素材包位于仓库外：`C:\Users\马恒杰\Documents\ChatGPT\office-assets-ecommerce-detail-page-ai-20260914`。ZIP SHA-256 为 `E3990CEAD94A920D925A10A1BE8682FA0E67821AD3C71ACC80F12437D64BD363`；已解压到同目录 `extracted`，素材说明保持只读使用和不得回拷仓库的约束。
- Git 工作树仍只有既有 `docs/当前项目状态.md` 修改；`bin/`、`obj/`、日志和办公室素材均未进入 Git。

本轮只关闭办公室 SDK、构建、进程、窗口和素材包可用性子门；没有取得 125% DPI 的 O-03 实机弹层确认，也没有执行真实 Photoshop JPG/PSD 输出，因此 `SIZE-01` 仍保持“进行中”。

## 8. 2026-09-15 PSD worker 运行目录修复与导入合同复核

本轮针对用户手动导入 `F-尺码表-【L-5XL】.psd` 时的“PSD 检查组件不可用”阻断，只补齐运行目录，不改变 SIZE-01 验收状态。

- 旧 Debug 输出缺少 `psd-inspection-worker`；按仓库锁定依赖重建 PyInstaller onedir，版本为 Python `3.12.8`、PyInstaller `6.16.0`、`psd-tools 1.18.0`、`scipy 1.15.3`、`scikit-image 0.25.2`。完整 worker 位于 `tmp/worker-runtime-repair-20260915/dist/psd-inspection-worker`，共 `1,200` 文件、`174,310,430` bytes，EXE SHA-256 为 `AA07AB4855C662A4EE5BBDA52D025632A1485E7D74075E8518C949937496943C`。
- 直接使用正式 `PsdInspectionWorkerClient` 复核：中文路径下对同一隔离内部副本完成指纹、复制和 `psd-inspection/v1` 检查，`exit=0`、`ok=true`、源/内部副本 hash 均为 `6CFF7EB29E1929E2294338AA6E05CF4B0520A45DF63A8718350F632ADEB81E05`，结果为 `113` 层、`1` 个智能对象、`3` 个 clipping；在独立 evidence data-root 上完整 `TemplatePageImportService` 导入成功并生成 preview。
- 可启动完整目录为 `tmp/runtime-repair-20260915/app-bin3`，含新 Debug `Desktop.App.exe` 与 worker onedir。当前运行中的旧 `Desktop.App` PID `37624` 锁定原 Debug 输出，故本轮不覆盖旧目录、不重启；父任务切换到新目录后可继续沿用现有隔离 `--data-root` 直接重试。
- 限制：未打开或修改源 PSD/JPG/PNG，未调用 AI/Photoshop，未进入 `RENDER-01`；本记录只证明 worker/导入技术合同和运行目录已具备，不替代 O-03 实际窗口/DPI/IME/UIA 或真实 JPG/PSD 联验收，`SIZE-01` 仍为进行中。

## 2026-09-15 按锁定环境重新构建并启动

- 已确认并使用 `.NET SDK 10.0.400`、Python `3.10.11`；依赖来自 `src/PsdInspection.Worker/requirements.lock` 与 `build-requirements.lock`，独立 venv 位于 `tmp/runtime-repair-20260915/py310-venv2`。
- 从当前源码重新生成运行目录 `tmp/runtime-repair-20260915/app-bin4`，包含 `Desktop.App.exe`、`Desktop.Infrastructure.dll`、`psd-inspection-worker/psd-inspection-worker.exe` 及 `_internal`。
- 新 worker 对真实尺码模板 PSD 返回 `ok=true`，`psd-inspection/v1`、113 层、1 个智能对象，并成功生成预览；PSD SHA-256 为 `6CFF7EB29E1929E2294338AA6E05CF4B0520A45DF63A8718350F632ADEB81E05`。
- 聚焦回归：`PsdInspectionWorkerClientTests` 9/9 通过，`TemplatePageImportServiceTests` 10/10 通过。当前 Desktop.App 已从 `app-bin4` 启动并保持响应。

## 2026-09-15 动态尺码表流程修正

按产品负责人确认的流程，动态尺码表页不再进入商品图校准：页面设置中选择“动态尺码表页”后，系统将该页排除出人工结果学习和图片框校准；如果模板组只有动态尺码表页，下一步按钮直接显示“保存模板组”并提交模板。普通套图页仍按原流程进入人工结果学习或拖拽校准。校准编辑器的页面列表也只显示普通套图页，避免用户误以为尺码表需要选择商品图。

本轮从当前源码发布 `tmp/runtime-repair-20260915/app-bin5`，包含 `Desktop.App.exe`、`Desktop.Infrastructure.dll` 和完整 `psd-inspection-worker`（含 `_internal`）。构建使用 .NET SDK `10.0.400`、Python `3.10.11` 及两个锁定依赖文件；Debug build 0 警告/0 错误，动态尺码表编辑器 UI 回归 1/1，通过模板工作区与相关应用回归 14/14。新包已启动，窗口保持响应。

## 2026-09-15 页面设置切换动态尺码后“暂无预览”回归

根因：隐藏的 `CalibrationPageList` 过滤掉动态尺码页后，通过共用的双向 `SelectedTemplatePage` 绑定回写 null，页面设置失去选中项。新增校准屏幕专用选择绑定，仅在校准/参考学习屏幕活动时同步，避免隐藏列表清空当前页；正常页切换与删除清空仍有效。

验证命令（仓库根目录，SDK 10.0.400）：

```powershell
dotnet test tests/Desktop.App.UiTests/Desktop.App.UiTests.csproj -c Debug --no-restore -p:NuGetAudit=false --filter ChangingPageToDynamicSizeTablePreservesSelectionPreviewAndSettings --verbosity minimal
dotnet test tests/Desktop.App.UiTests/Desktop.App.UiTests.csproj -c Debug --no-build --no-restore -p:NuGetAudit=false --filter 'FullyQualifiedName~ChangingPageToDynamicSizeTable|FullyQualifiedName~ReferenceLearning|FullyQualifiedName~Calibration' --verbosity minimal
dotnet publish src/Desktop.App/Desktop.App.csproj -c Debug --no-restore -r win-x64 --self-contained false -o tmp/runtime-repair-20260915/app-bin6 -p:NuGetAudit=false --verbosity minimal
git diff --check
```

- 新增测试在修复前 2/2 复现 `SelectedTemplatePage == null`，修复后 2/2 通过；相关回归合计 13/13 通过。测试通过真实 WPF 下拉框绑定反复切换类型，检查预览 Image.Source、表单值与列表选中项；混合模板还检查普通页校准/参考学习切换与返回。测试预览为生成的小 PNG，不能作为真实模板套版视觉验收。
- 发布无编译警告/错误；复制先前 `worker-dist4` 的完整 Python 3.10.11 onedir worker，1,194 文件 SHA-256 逐个匹配；`pip check` 返回无依赖问题。运行目录包含 Desktop.App.exe、Desktop.Infrastructure.dll、worker EXE 与 `_internal`。
- 已正常关闭 app-bin5 并从 app-bin6 启动，沿用默认 DesktopV1 数据目录。只读 SQLite/文件检查确认用户草稿的动态类型、每款一次、启用状态已持久化，内部 PSD SHA-256 与记录一致，真实 1500×1523 预览可解码。不需要重新导入模板。
- reviewed diff 范围为选择绑定、对应 WPF 回归及交接记录，保留既有工作树变更，源素材未修改。未取得重启后用户实际点击的视觉确认，也未执行 AI/Photoshop 或正式 JPG/PSD 输出；SIZE-S7/RENDER-01 状态不变。

## 9. 2026-09-15 动态尺码表来源卡片与最终启动

- AI 输入投影为没有普通 PSD frame 的动态尺码页增加独立尺码图来源要求；款式检查 UI 用独立卡片展示本款全部图片候选。唯一候选自动识别，多候选进入“需要选择”，用户可点击候选替换来源；该要求不会被转成普通图片框 RenderPlan。
- DynamicSizeTablePageAddsSourceRequirementWithoutAProductFrame 1/1、DynamicSizeTablePageShowsIndependentSourcePickerWithoutFrameRows 1/1 通过；AI/动态尺码/参考学习/校准聚焦回归 18/18 通过；solution build 0 警告/0 错误。
- 已从当前源码发布 tmp/runtime-repair-20260915/app-bin7 并复制完整锁定依赖 worker；确认四项入口文件/目录存在。旧 app-bin6 已关闭，app-bin7 PID 34788，窗口标题“电商详情页智能生产系统”，Responding=True。
- 本记录只证明应用逻辑、构建、运行目录和启动状态；Photoshop 正式 JPG/PSD 联验收、O-03 实际窗口/DPI/IME/UIA 仍未关闭。

## 10. 2026-09-15 动态尺码图按钮组件统一

- 动态尺码表来源候选项已统一为普通图片框使用的紧凑按钮风格，显示缩略图、照片名、选择状态和匹配度；普通套图图片框无行为变化。
- solution build 0 警告/0 错误，相关 UI 回归 15/15 通过。
- 已发布并启动 `tmp/runtime-repair-20260915/app-bin8`，PID `35924`，窗口标题“电商详情页智能生产系统”，响应正常。

## 11. 2026-09-15 尺码图行组件对齐普通套图

- 动态尺码图来源行已按普通套图选图组件统一：缩略图、状态灯、用途/页面上下文和右侧“替换”按钮一致；用途显示为“尺码图”，不显示图片框编号。
- 相关 UI 回归 18/18 通过，solution build 0 警告/0 错误。
- 已发布并启动 `tmp/runtime-repair-20260915/app-bin9`，确认 Desktop.App.exe、Desktop.Infrastructure.dll、worker EXE 和 `_internal` 均存在；PID `27660`，响应正常。
