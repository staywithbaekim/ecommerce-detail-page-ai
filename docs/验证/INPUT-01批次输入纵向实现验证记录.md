# INPUT-01 批次输入纵向实现验证记录

## 1. 结论

截至 2026-08-29，`INPUT-01` 的正式代码、合同、SQLite v5、`asset-manifest/v1`、WPF B-01、故障回滚、自动化证据和产品负责人人工页面走查均已完成；完成态恢复与配置冻结补正后，完整 solution 构建为 0 警告、0 错误，148 项测试通过。本分支验收结论为“通过，待主任务复核提交与合并”；本分支不自行合并主线或恢复 `AI-01`。

## 2. 基线与范围

- 分支：`codex/input-01-batch-input`；
- 起点：`3c5d70d docs: gate AI work on INPUT baseline`，包含完整 TPL-01 主线和 INPUT 依赖门；
- 范围：真实 WPF B-01 到持久化 `BatchDraft`、只读一级目录扫描、源资产元数据、模板快照和 `asset-manifest/v1`；
- 非范围：AI 语义分析、动态尺码、Photoshop 渲染、账号/支付/云同步、平台上传和完整 FLOW/RENDER。

## 3. 已验证行为

### 3.1 领域与目录合同

- `Draft -> Scanning -> ScanReady|ScanBlocked` 状态守卫成立；非 ready 状态不保存部分款式、资产或 manifest；
- 根目录直接含支持图片且无图片型一级子目录时识别单款，目录名作为默认款号；
- 根目录无支持图片、一个或多个一级子目录直接含支持图片时按一级子目录识别正常批次；
- 混合结构和空结构均阻断，零款式、零资产、零 ready manifest；
- 深层目录不参与归属，路径逃逸、设备/网络路径和 reparse point 不跟随；
- 稳定 batch/style/asset/manifest/snapshot ID 和相对来源引用可供后续 AI-01 消费。

### 3.2 文件、资源与故障边界

- JPEG 校验 EOI/段结构，PNG 校验 IHDR/IDAT/IEND 和 CRC，常规 TIFF 校验 IFD 宽高以及 strip/tile offset/byte-count 的实际文件边界；扩展名与文件头不符、损坏和截断均阻断；
- 文件使用只读、流式 SHA-256 和默认 4 路有界并发；200 款真实临时目录集成功能测试通过；
- 中文、英文、数字、空格和超过 260 字符的合法长路径通过；锁定不可读、目录消失、深层路径、路径逃逸和可创建环境下的符号链接门均有覆盖；
- 扫描、失败、删除草稿和重启恢复前后源 hash 不变；测试生成的图片、数据库、manifest 和 marker 均位于临时或应用自有目录，没有进入 Git；
- manifest 采用 staging、原子写、digest 读回和 committed marker；SQLite 提交失败时回滚应用自有 manifest，不产生 `ScanReady`；
- SQLite v5 单调新增批次、款式和资产表；既有模板组/修订仍可读取；草稿阶段允许同一批次快照原位更新，`Scanning`/`ScanReady` 后冻结；重启恢复同一 ready 款式、资产、模板页和选项。

### 3.3 WPF B-01

- 工作台“新建批次”接入真实页面；模板、输入目录、输出目录、页面选择、自动/人工选图和 PSD 选项进入正式批次合同；
- 原生 Windows 文件夹选择器、Draft/Scanning/ScanReady/ScanBlocked、取消、重试和恢复投影已接通；
- 单款、正常批次、混合、空目录和系统失败使用中文可行动文案；普通页面不显示内部 ID、结构签名、堆栈或诊断绝对路径；
- 复用 `DesignSystem.xaml`；宽版/1100×760 窄版、AutomationId、至少一页门和扫描中按钮状态通过 UI 测试；
- 模板选择器使用显式名称模板，实际页面不再显示 `TemplateLibraryCardViewModel`、`GroupId` 等内部对象字符串；UI 回归直接检查渲染树中的中文模板名称；
- WPF 不再直接引用 Domain，批次工作流通过 Application facade 投影，架构测试保持通过。

## 4. 自动化命令与结果

NuGet restore 经用户批准联网完成，没有新增依赖。

```powershell
dotnet format EcommerceDetailPage.Desktop.slnx --no-restore --verify-no-changes --verbosity minimal
```

结果：通过，无格式差异。

工作区应用进程锁定默认 App 输出时，按既有验证惯例使用隔离临时 `OutDir`，未终止用户进程：

```powershell
dotnet build EcommerceDetailPage.Desktop.slnx -c Debug --no-restore --no-incremental -p:OutDir=<临时目录>
dotnet test EcommerceDetailPage.Desktop.slnx -c Debug --no-build --no-restore -p:OutDir=<同一临时目录>
```

结果：构建 0 警告、0 错误；测试 148/148：Domain 34、Application 35、Contracts 23、Infrastructure 46、Architecture 5、WPF UI 5。

聚焦结果：

- Domain：34/34；
- Application：35/35；
- Contracts：23/23；
- Infrastructure：46/46；
- WPF UI：5/5。

`git diff --check` 通过；状态检查未发现 PSD/JPG/PNG 样本、数据库、manifest、marker、缓存、日志、凭据、dump 或测试输出进入待提交文件。

## 5. 人工走查状态

2026-08-28，产品负责人在隔离临时数据根启动的实际 `Desktop.App.exe` 中完成 B-01 走查，并明确确认“走查了一遍没什么问题”。走查覆盖：

1. 单款显示“已识别为单款批次 · 1 款”；
2. 正常批次显示“已识别为正常批次 · 2 款”；
3. 混合结构显示可行动阻断文案且零部分任务；
4. 空目录显示可行动中文提示；
5. 扫描、取消/重试和重启恢复无虚假完成；
6. 1440×900 与 1100×760 的关键内容、焦点、状态灯和文案可见。

2026-08-29，Codex 经产品负责人授权再次打开同一隔离应用检查。复核发现模板下拉框由共享自绘 `ComboBox` 回显了 ViewModel 对象字符串；该问题会暴露内部字段，已改为显式中文名称模板并增加 WPF 实际渲染树回归断言，聚焦 UI 测试 5/5 通过。Computer Use 在检测到用户窗口输入后停止继续注入，不抢占用户操作。该修复不改变业务合同或走查范围，最终页面合同保持通过。

同日主任务复核 `e11cd96` 时发现两处状态合同缺口并在本分支补正：

- `BatchInputWorkflowAdapter.RestoreLatestDraftAsync` 不再过滤 `ScanReady`；应用重启会恢复最新 ready 批次及其模板、页面、目录、选图方式和 PSD 选项；
- ready 投影的主按钮显示“扫描完成”且不可执行，模板、目录、页面选择、选图方式和 PSD 开关同步冻结，避免出现不可兑现的“重新扫描”或向已冻结 `BatchDraft` 写入配置。

补正后的聚焦 WPF 测试为 5/5，完整 solution 测试为 148/148；`git diff --check` 通过，未引入源素材、数据库、缓存或生成物。

## 6. 差异与安全审查

- 改动只覆盖 INPUT 合同、Domain/Application/Infrastructure、SQLite v5、B-01、共享视觉资源引用、测试和路由/验证文档；
- 没有实现 AI、Photoshop、动态尺码、账号、支付、云同步或平台上传；
- 没有修改 v1-v4 迁移 SQL/checksum，没有降低 TPL 修订和批次快照安全门；
- 输入根只进入只读 inventory/metadata 端口；应用自有文件路径由 `LocalAppPathPolicy` 限制，回滚不接触输入根；
- WPF 只依赖 Application/Infrastructure；Domain 继续拥有 `BatchDraft` 和状态不变量，Presentation 使用 Application facade 的 DTO/枚举，不形成反向依赖；
- 默认 build 的一次失败由本任务为人工走查启动的 `Desktop.App` 锁定输出造成；隔离 `OutDir` 的同代码完整构建和测试随后通过。

## 7. 收口结论

- 产品负责人人工走查已通过；
- 验收矩阵 `INPUT-01` 可标记为通过；
- 当前状态和项目总览转为“待主任务复核提交与合并”；
- 完成最终 reviewed diff、清晰提交并报告 SHA 后，本分支交付结束。

本分支不得自行合并 `codex/desktop-foundation-01` 或恢复 `AI-01`；只有主任务复核本记录、diff 与提交并形成新主基线后，才能处理后续恢复。
