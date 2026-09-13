# TPL-LEARN-01 修复进展报告

**更新时间：** 2026-09-13
**状态：** 代码与自动化子门已完成；真实目标款已完成一次整组分析和一次人工锁图，进程重启后锁定记录保持；完整 WPF 视觉走查与 TPL-LEARN-01 S4-S7 仍进行中

---

> 本页最初的 agent 进度记录保留作历史追溯；当前事实以“2026-09-11 Codex 复核更新”及文末“删除一致性与最终包复核”为准。

## 修复进展

### 初始交接记录（已由下方复核更新取代）

#### BUG-001: AI生成的选图规则质量问题
**状态：** ✅ 已修复
**修复内容：**
- 实现了动态布局意图推断，根据图片框特征生成7种不同的布局意图
- 为每个框生成个性化的AI输入上下文（包含图层名称、布局角色）
- 从硬编码改为智能推断
- 修改文件：
  - `src/Desktop.Application/Templates/ReferenceLearningService.cs`
  - `src/Desktop.Application/Templates/ReferenceLearningAiService.cs`
- 新增测试：`tests/Desktop.Application.Tests/ReferenceLearningLayoutIntentTests.cs`

**预期效果：**
- 修复前：10个框中9个都是"商品展示图（与参考款保持同一视角）"
- 修复后：每个框生成不同的具体描述，如"领口特写图"、"面料质地特写图"等

**验证文档：** `docs/修复记录/BUG-001-*.md`

#### BUG-002: PSD重复检测阻塞正常操作
**状态：** ✅ 已修复
**修复内容：**
- 添加了 `AutoReuseDraftDuplicates` 选项，自动重用草稿内的重复PSD
- 改进了重复检测逻辑，避免不必要的用户干预
- 修改文件：
  - `src/Desktop.Application/Templates/TemplateImportContracts.cs`
  - `src/Desktop.Application/Templates/TemplatePageImportService.cs`
  - `src/Desktop.App/MainWindowViewModel.Templates.cs`

#### BUG-004: 删除模板功能不工作
**状态：** ✅ 已修复
**修复内容：**
- 添加了3次重试机制处理文件占用
- 改进了错误处理和用户提示
- 修改文件：
  - `src/Desktop.Infrastructure/Templates/TemplateOwnedStorage.cs`
  - `src/Desktop.Application/Templates/TemplateWorkspaceService.cs`
  - `src/Desktop.App/MainWindowViewModel.Templates.cs`

**验证文档：** `docs/验证/BUG-002-004-修复说明.md`

---

### 历史失败记录（已解决）

#### BUG-001: AI生成的选图规则质量问题
**状态：** ❌ Agent失败
**问题：** 第一个修复agent（AI提示词优化）执行失败，输出为空

**需要采取的行动：**
- 重新启动修复agent
- 或者手动修复该问题

---

### 历史未开始记录（已解决）

#### BUG-003: PSD检查worker无法处理中文路径
**状态：** ⏸️ 等待启动
**原因：** 等待BUG-001修复完成后再处理

---

## 下一步行动建议

### 选项A：重新启动BUG-001修复agent
重新创建一个agent处理AI提示词优化问题

### 选项B：手动修复BUG-001
由技术顾问直接修改代码，不使用agent

### 选项C：先处理BUG-003
跳过BUG-001，先修复中文路径问题

---

## 风险评估

**当前状态：**
- 2个P0缺陷已修复（BUG-002, BUG-004）
- 1个P0缺陷未修复（BUG-001）- **核心功能仍不可用**
- 1个P0缺陷未处理（BUG-003）

**结论：**
系统仍然无法正常上线，因为BUG-001（AI规则生成）是核心功能，必须修复。

---

## 推荐方案

我建议采用**选项B：手动修复BUG-001**，原因：
1. 这是核心功能，必须修复
2. Agent失败说明问题可能比预期复杂
3. 直接修复更可控，可以逐步测试

修复BUG-001后，再处理BUG-003，最后进行完整的验收测试。

---

## 2026-09-11 Codex 复核更新（以实际代码和测试为准）

本页上方的“3/4”“Agent失败”和 `ReferenceLearningLayoutIntentTests.cs` 是交接前的历史记录，不能作为当前验收证据。当前仓库未找到该测试文件；实际覆盖位于 `ReferenceLearningServiceTests`、`ReferenceLearningAiServiceTests` 和基础设施测试中。

### BUG-001：参考学习语义与缓存隔离

- `ReferenceLearningService` 已把页面名、图层名、框布局意图、画幅和位置上下文传入每个 frame；本地候选仍标记为需复核，不把启发式文字当作真实模型准确率。
- 供应商提示词已明确：`frame_context` 只能作为逐框结构/版式线索，不能单独臆测；最终语义必须以参考照片可见内容和该框实际呈现为证据，避免新上下文被“禁止使用图层名/位置”措辞抵消。
- `ReferenceLearningAiProtocol.SemanticPromptVersion` 已从 `reference-learning-semantic-prompt/v2` 升为 `v3`。该版本同时进入请求、结果元数据、兼容性检查和缓存 fingerprint，因此旧 v2 缓存不会命中新提示词路径；未删除旧缓存或旧会话。
- 参考学习服务回归通过 `39/39`；基础设施参考学习回归 `25 passed / 1 skipped`；Responses 适配器 `52 passed / 2 skipped`。

### BUG-002：重复 PSD

- `AutoReuseDraftDuplicates` 只在同一 group/draft 内复用；补上同一 command 重试的幂等判断，避免首次自动复用后被误报资源冲突。
- `TemplatePageImportServiceTests` 当前 `10/10` 通过，其中包含自动复用重试回归。未改变跨草稿重复的手动决策边界。

### BUG-003：中文路径 worker

- worker 客户端在 Windows 启动 worker 时使用可用的 8.3 短路径，并保持 UTF-8 标准输入/输出；Python worker 已显式重配置 UTF-8。
- `PsdInspectionWorkerClientTests` 聚焦回归通过；其中中文路径测试使用本机 PowerShell 脚本验证路径传递和启动参数。尚未把该结果表述为已打包 PyInstaller worker 的完整生产验收。

### BUG-004：模板删除

- 文件存储删除增加最多 3 次锁/权限重试，应用层将失败映射为稳定错误码，UI 显示阻止者和可操作提示。
- 模板仓储/文件删除聚焦回归已通过；未伪造无法可靠注入的真实 Windows 文件锁性能证据。删除仍只触及应用自有模板目录，不触碰用户源 PSD、照片或正式输出。

### 本轮验证

`dotnet build EcommerceDetailPage.Desktop.slnx --no-restore -c Release --no-incremental`：0 警告、0 错误；`dotnet test EcommerceDetailPage.Desktop.slnx --no-build -c Release --filter "FullyQualifiedName!~SizeTableGoldSampleTests"`：593 通过、4 跳过、0 失败；`dotnet format --verify-no-changes --no-restore`：通过；Python `worker.py` 语法检查：通过；未调用真实 API，未修改用户源媒体或正式输出。

### 下一步

使用新构建包新建学习会话，选择与参考款不同的目标款照片文件夹，重新验证整组候选、同图多框、相近替代的红黄绿状态及 S4-S7。当前代码通过不等于真实 AI 语义准确率、完整 PERF-01、Photoshop/文件提交或整体 V1 已通过。

## 2026-09-11 v3 包与真实 worker 补充验证

- 已按锁定依赖重建 PyInstaller onedir worker，并在中文目录 `tmp/真实中文路径验收-20260911/` 中检查真实 PSD 副本：退出码 `0`、`ok=true`、46 层、8 个智能对象、8 个 pixel evidence；worker EXE SHA-256 为 `778E44E2BEB3CA8DFD73912635F0D60D0B4E06839B1953C1AC4F443C6BEE1DCE`。BUG-003 的真实打包 worker 子门已通过，但仍不等同于 WPF 产品走查。
- 最新 Debug 应用包为 `tmp/tpl-learn-20260911-v3-debug-app`，包含该 worker；全新数据根 `tmp/tpl-learn-20260911-v3-debug-data` 启动保持响应，关闭后 `integrity_check=ok`、migration 12、`safe_exit=1`、无残留进程。
- 本轮 Release build 0/0；离线 solution 597 通过、4 跳过、0 失败；format、diff 和 Python 语法检查通过。没有发送真实 AI 请求。
- 桌面控制面未暴露原生应用窗口，故没有虚构 S4-S7 视觉证据。不同目标款目录已定位为 `需求样本/第一组/款式原图/A馨妈6015-58`，真实整组匹配/红黄绿/主体适配仍开放。

详细命令、哈希和边界见 `docs/验证/TPL-LEARN-01-20260911-v3包与worker验证记录.md`。

## 2026-09-11 删除一致性与最终包复核

### BUG-004：模板删除一致性

- 删除顺序已改为“自有模板目录原子暂存 → SQLite 元数据删除 → tombstone 清理”。暂存失败不修改元数据；数据库删除失败会以不可取消动作恢复原目录；元数据成功后清理不响应调用方取消。
- 真实 Windows 文件锁回归确认：锁定时返回删除失败，SQLite 元数据和 `templates/groups/{groupId}` 原目录均保留；释放锁后重试成功，元数据、原目录及暂存目录均清除。
- Application `TemplateWorkspaceServiceTests` 13/13，Infrastructure `TemplateOwnedStorageTests|TemplateRepositoryTests` 13/13。删除范围仍严格限制为应用数据根内的自有模板目录，不触碰用户源 PSD、照片或正式输出。

### 最终代码门与测试包

- Release solution build：0 警告、0 错误。
- 离线 solution（排除仓库缺失的 `SizeTableGoldSampleTests`）：598 通过、4 跳过、0 失败。
- `dotnet format --verify-no-changes --no-restore`、`git diff --check`、5 个 Python 脚本语法检查：通过。
- 最新 Debug 包：`tmp/tpl-learn-20260911-v3-deletefix-debug-app/Desktop.App.exe`；`Desktop.App.dll` SHA-256=`BA5634659B833B7D2E7AA073CA23587694AC91AF02766DB30C6DA04EEFD57A3D`，worker SHA-256=`778E44E2BEB3CA8DFD73912635F0D60D0B4E06839B1953C1AC4F443C6BEE1DCE`。
- 全新隔离数据根 `tmp/tpl-learn-20260911-v3-deletefix-debug-data` 启动约 5 秒保持响应，正常退出码 0；SQLite `integrity_check=ok`、migration 12、`safe_exit=1`，无日志和残留进程。

当前剩余工作不是继续修改提示词版本或删除实现，而是用 `需求样本/第一组/款式原图/A馨妈6015-58` 的 12 张不同目标款照片完成真实整组候选、同图多框、红黄绿、主体适配及重启恢复走查。用户已允许必要的真实 API 请求；若当前 300/240/90 秒传输边界形成 `AiResultUnknown`，必须停下等待人工处理，不能自动重试，也不能重复此前 R4 请求。

## 2026-09-11 当前运行诊断与失败语义 UI 修复

- 用户实际运行的 v4 包在 `tmp/manual-tpl-batch-20260911-data` 中生成的 6 个学习会话均为 `AwaitingReview`，`block_reason` 为“请求超时，结果状态未知，请查询或人工确认后再继续”，每个候选的 `semantic_metadata.source` 为 `AiUnavailable`；`ai_upload_consents` 已为 `Granted`。这证明本次不是动态规则返回相同内容，而是整组真实请求在 180 秒边界内没有收回可验证结果。
- 本地 PSD 语义占位仍保留在 `semantic_label`（置信度 `0.72`），原 UI 未把 `AiUnavailable` 显式投影到每个框，因而用户看到“商品展示图（与参考款保持同一视角，请确认）”时容易误认为 AI 已成功。该文案是失败回退提示，不是 AI 结果。
- 已在统一校准编辑器增加 AI 未完成警示、阻止未修改本地占位直接确认、显示超时原因并恢复“改用拖拽校准”入口；手动改写后仍可通过 `UserOverride` 正常确认。未放宽 180 秒未知结果边界，也未重发本次结果未知请求。
- 验证：`dotnet build src/Desktop.App/Desktop.App.csproj --no-restore -c Release --no-incremental` 为 0 警告/0 错误；参考学习 AI 测试 7/7；当时的 WPF 参考学习 UI 测试 1/1。`tmp/tpl-learn-20260911-v5-ai-failure-ui-debug-app` 为历史 Release 发布目录，不再作为本次数据根入口；其 `Desktop.App.dll` SHA-256=`C1FF2901145327EBE271F3B33B999715F5F29E07FA6287D717147AE48BE4D114`。

当前仍不能把这次超时会话保存为 AI 学习结果；下一步是用新包继续人工填写/确认，或在供应商侧查询该请求状态后再决定是否建立新的真实请求。不得连续重发未知结果请求。

### 2026-09-11 v5 数据根启动复核补充

- 先前标记为 v5 的 Release 发布目录会在该构建条件下忽略 `--data-root`，因此未将其默认数据目录窗口当作本次会话验证证据；该包已停止使用。
- 已用 Debug 发布重新生成 `tmp/tpl-learn-20260911-v5-ai-failure-ui-debug-root-app/Desktop.App.exe`，补齐 `psd-inspection-worker`（SHA-256 `778E44E2BEB3CA8DFD73912635F0D60D0B4E06839B1953C1AC4F443C6BEE1DCE`），并以 `--data-root=tmp/manual-tpl-batch-20260911-data` 启动。SQLite `app_instance` 已记录当前进程，6 个学习会话未改变，未发起新请求。
- 新增 WPF 回归覆盖实际失败语义：`AiUnavailable` 警示可见、超时原因可见、未改写本地占位时确认按钮禁用，手动改写后按钮恢复可用；该测试通过。当前参考学习 UI 回归 4/4、参考学习 AI 回归 7/7，Release build 0 警告/0 错误。

## 2026-09-12 手动失败根因与硬截止修复

- 对比成功隔离根 `tmp/tpl-learn-real-validation-data-20260911-184329` 与手动失败根 `tmp/manual-tpl-batch-20260911-data` 后确认：两条路径的页面/frame ID 和数据根不同，成功根命中已有参考语义 cache；手动根没有 cache，重新建立独立请求。手动 6 个会话均为 `AiUnavailable`，不是 AI 成功结果被 UI 覆盖。
- 手动失败请求原始 UTC 时间从 `2026-09-11T12:41:39.8845517Z` 到 `2026-09-11T13:09:22.386355Z`，约 1662 秒；副本准备探针 11 张共 `3,129,030` bytes、约 `4330 ms`，因此等待发生在 HTTP/响应读取阶段。旧实现的无限 `HttpClient.Timeout` 加链接 CTS 取消没有强制前台返回。
- 已在 `VolcArkAiAdapter` 加入私有 `HttpRequestScope`：发送和响应体读取共享总 deadline，使用 `Task.WaitAsync` 提供硬截止；deadline 后立即返回 `AI_PROVIDER_TIMEOUT`/`AiResultUnknown`，后台 observer 在迟到 task 完成后再释放 request/response，普通 `ai-analysis/v1` 与参考学习 `reference-learning-ai/v2` 同时覆盖。外部取消仍为 `Cancelled`，未知结果不自动重试。
- 新增四个离线回归（普通发送/响应体、参考学习发送/响应体均忽略取消），均在 1 秒内返回超时；聚焦基础设施回归 `72 passed / 3 skipped / 0 failed`，Release build `0/0`，`git diff --check` 通过。证据见 `docs/验证/TPL-LEARN-01-20260912-硬截止边界修复验证记录.md` 与 `docs/架构决策/ADR-026-AI适配器硬截止与迟到任务清理.md`。
- 已按 Computer Use 技能尝试枚举原生窗口，但 `sky.list_apps` 返回 `Trusted RPC service is not configured: sky`；本轮没有伪造 WPF 点击验收，也没有通过 PowerShell/UIAutomation 冒充 Computer Use。TPL-LEARN-01 仍未关闭 S4-S7，下一步需用新包由可用控制面或产品负责人完成真实 UI 走查。
## 2026-09-12 手动复试：上传授权门

- 准确硬截止包已在全新数据根 `tmp/tpl-learn-ui-retry-20260912-data` 中完成 6 页/11 个图片框的本地结构学习；`ai_upload_consents` 为空、无本次请求 ledger，页面会话阻断原因为“请先在设置中允许上传压缩分析图，或手工填写完整选图要求”。
- 参考学习现已在 UI 开始前复用上传授权门，授权后自动续接；WPF 授权门/批次授权/整组语义聚焦回归 `3/3`，Release build `0/0`。不自动替用户授予隐私授权。
- Computer Use 已把窗口停在设置授权卡片，等待用户手动点击“允许上传压缩图”。完成后继续新的真实参考语义请求、逐框规则修改保存和 24 张“简唏7707-75”目标款候选验收。

## 2026-09-12 账单核对与上传阶段分类补充

- 产品负责人确认火山后台 14:25 之后对应失败时段没有账单。脱敏日志显示旧 V2 请求
  `probe_daily_alias_11_4044b6978169` 在北京时间 17:09:44 开始发送 13,116,193
  bytes，至 17:12:44 的 180 秒截止仍 `upload_completed=false`，没有响应头、HTTP
  状态或 provider request reference，终态为 `AI_PROVIDER_SUBMISSION_TIMEOUT`。这表示
  本机有上传尝试，但火山未受理完整请求，不是火山推理超过 180 秒。
- 适配器和 scheduler 已按阶段修正：上传未完成的超时/断网为可安全重试提交失败；上传
  完成后未收到响应头/响应体仍为 `AiResultUnknown`，禁止自动重发。WPF 前者显示“AI
  请求未送达 / 火山未受理 / 重试分析”。
- V3 内部分析副本将该组请求体降为 7,056,022 bytes（源照片哈希不变）。独立 V3 请求
  于北京时间 17:51:51 完成上传、17:52:28 收到 HTTP 200 和 provider request reference；
  它是另一笔请求，是否计费以火山后台为准。证据见 `docs/验证/TPL-LEARN-01-20260912-上传未送达与账单边界验证记录.md`、
  ADR-026/027 及两份 `ai-transport.jsonl`。
- 本轮 Release build 0/0；Infrastructure 285 通过/5 跳过、Contracts 58/0、Application
  聚焦 205/0。WPF 全量宿主仍可能触发 WPF 自身 `Application.GetResourcePackage` FailFast，
  这不影响本次传输结论；没有再次发起真实 API。TPL-LEARN-01 仍待真实 WPF 走查与 S4-S7 收口。

## 2026-09-13 真实目标款整组分析、人工锁图与进程重启复核

使用 `tmp/tpl-learn-20260913-transport-fix-debug-app/Desktop.App.exe`、模板组“测试”和全新批次
`batch-9f7b115defbb4563a26471089f479098`（style `style_78caace23ecc18c3bf5ab1856b101c1e`）
完成实际复核。输入目录为“简唏7707-75”照片目录，24 张 PNG/JPG；模板快照为 6 页、11 个图片框；
输出隔离目录 `tmp/tpl-learn-20260913-transport-fix-ui-output-01` 保持空目录，未点击“开始生成”。

真实请求只有 1 次：`ai-analysis/v1` payload `8,401,443` bytes、24 assets、11 requirements，
`upload_completed=true`、写入 `8,401,443` bytes、HTTP `200`，provider reference
`02178923789931088a480d877f824556f2559e19636edcd2af52f`，响应体 `7,847` bytes；模型
`doubao-seed-2.1-pro-260628-output4096-v2`，SQLite usage `33,568/827`，无重试。结果为 9 个
`exact`、2 个 `safe_similar`，唯一警告 `no_full_back_person`。

WPF 总览为“6 / 6 页面已分析”“可生成页面 0/6”“待人工复核”，汇总“可生成 0 / 待检查 1 /
不可生成 0”。11 个框初次均为“AI 推荐 · 已选用”；对 `page_1 · 图片框 1` 选择“照片 03 →
改用这张”后，该框变为“人工选用 · 已锁定”，其余 10 个框保持 AI 推荐。锁定记录为
`learn_frame_417b5d982ce7450824f39617` → `asset_3f2625eece0e07758c1ebbfeb1f50e4b`，
session/ledger 为 `ManualReview`。

锁图提交后正常关闭 PID `37192`，同包重启为 PID `51072`，进程保持响应；只读核对
`state/desktop-v1.db` 得到 `integrity_check=ok`，锁定 revision 和单次 usage 保持，日志没有新请求。
Computer Use 控制面重试仍返回 `Trusted RPC service is not configured: sky`，故没有重启后新的截图级
视觉证据；进程级持久化结论仅来自窗口响应、SQLite 与日志。

本次关闭真实传输、一次人工锁图和进程级持久化子门，不关闭完整 S4-S7。仍需全部 11 框语义确认、
真实绿/黄/红准确性（含同图多框和主体适配）、取消/中途退出恢复、重启后的视觉复核、RenderPlan
参考零引用和 reviewed diff；不得生成 PSD/JPG 或重发该请求。

## 2026-09-13 替换按钮与批次进度实时刷新修复

根据产品负责人反馈，替换确认按钮及说明统一改为“改用这张并保存”。选择照片只是暂存待保存项，
左侧当前用图在点击确认前保持不变；成功提交后才写入人工锁定记录并刷新当前照片、状态和使用计数。

同时修复批次进度停留在旧 `2/6`、`3/6` 的 UI 根因：自动刷新此前只在“生成前检查”页运行，进入“款式检查”
就停止；且聚合状态为“待人工复核”时，仍可能有兄弟页面处于“待分析/分析中”。现在两个页面都按页面状态
每秒刷新，只要调度器仍在运行且任一页面未收口；全部页面收口、暂停/等待网络或离开批次页后停止。进度
文本标记为 WPF live `Polite`，总览和筛选行共享最新投影。

回归结果：WPF AI 聚焦 6/6、WPF 全量 37/37；完整 solution（排除仓库明确缺失的
`SizeTableGoldSampleTests`）649 通过、5 个显式环境/样本探针跳过、0 失败。独立测试包为
`tmp/tpl-learn-20260913-live-progress-replace-debug-app/Desktop.App.exe`，包含已验证 worker；
本轮没有调用真实 AI，也没有修改源 PSD、源照片或正式输出。详细证据见
`docs/验证/WPF-UI-20260913-替换按钮与实时进度修复验证记录.md`。

本修复关闭按钮文案和自动刷新代码子门，不关闭 TPL-LEARN-01 的真实逐框语义、目标款准确率、
红黄绿、取消/退出恢复、重启后的视觉走查或 LEARN-S4 至 S7；手动验收必须关闭旧运行包并以新包、
全新数据根建立批次。

## 2026-09-13 默认选图与连续替换修复最终状态

已按 ADR-030 完成本轮批准变更：`exact` 且置信度 `≥0.85` 直接绿色；候选分差或同分不再单独造成
黄色，只有显式语义冲突、错款/错变体、缺图、不安全和结果无效才升级风险。AI Top-1 是默认确认结果，
不要求逐框点击确认；用户需要变更时仅执行“替换用图 → 选照片 → 确认”。动态 `selection_requirement`
优先作为用户可见类别，历史“图片框 N”、`frame_*`、 “待保存”和“选用这张”不再作为用户路径。

已定位并修复连续替换失败的后端根因：`ManualReview` 终态账本被重复 CAS 写入，导致第二张及之后的
替换返回 `AI_CHECKPOINT_REQUEST_CAS_CONFLICT`。修复后后续替换只递增 `selection_revision`、更新锁定
映射并重建投影，不重复改写终态账本；确认后左图、状态和使用次数立即刷新。失败仍保留原图并可重试，
不会发起额外 provider 请求。

本轮最终回归为：Release solution build 0 警告/0 错误；完整 solution（排除仓库明确缺失的
`SizeTableGoldSampleTests`）656 通过、5 个显式探针跳过、0 失败；WPF UI 37/37。新独立 Debug 包为
`tmp/tpl-learn-20260913-semantic-risk-debug-app/Desktop.App.exe`，已完成隔离启动/关闭
smoke（退出码 0、SQLite integrity check 通过、`safe_exit=1`）。证据见
`docs/验证/TPL-LEARN-01-20260913-默认选图与连续替换修复验证记录.md`。

代码子门现已完成；任务总体仍为“进行中”，因为当前控制面没有原生 WPF Computer Use，目标款 24 图的
逐框视觉绿/黄/红、替换截图、取消/重启视觉走查和 Photoshop/JPG 最终输出仍必须由产品负责人手动验收。
## 2026-09-13 语义风险门补充状态

`exact ≥0.85` 和安全高置信 `safe_similar` 已不再受候选分差/同分影响；新增回归覆盖 `safe_similar 0.90` 的同分、近分
以及低于绿色门槛场景。用户可见黄色不再写“候选接近”，只提示低于绿色置信度门槛或模型明确返回实质差异/语义冲突。
最新 Release 回归为 656 通过、5 个显式探针跳过、0 失败，WPF 37/37；新包为
`tmp/tpl-learn-20260913-semantic-risk-debug-app/Desktop.App.exe`，隔离 smoke 的 SQLite integrity 为 `ok`、
`safe_exit=1`。这只关闭语义风险代码门，不替代原生 WPF 手动验收，任务总体继续保持进行中。

## 2026-09-13 产品负责人手动验收确认

产品负责人在本窗口确认已使用最新验收包完成本轮五项交互的实际手动测试，结果无问题：AI 识别后的默认用图可直接进入生成；每个图片框均可重复执行“替换用图 → 选择照片 → 确认”，左侧预览、状态和计数会更新；用户路径不再出现“待保存”或“选用这张”；准确的袖口/领口等结果按规则显示绿色；界面优先显示“人物正面图”“领口细节图”等语义名称。

该确认关闭本轮 UI/交互修复的产品子门，可作为当前代码候选提交前的人工验收证据。它没有覆盖完整 `TPL-LEARN-01` S7 的全部要求：仍需单独记录全框目标款语义与红黄绿金标、取消/中途退出和重启视觉恢复、RenderPlan 参考零引用及 reviewed diff。故任务总体状态仍为“进行中”，不得据此宣称已完成或直接交接正式 Photoshop 输出。
