# UXP 技术可行性验证

## 基本信息

| 项目 | 内容 |
| --- | --- |
| 状态 | 已完成（TV-01 至 TV-08、TV-11 至 TV-14 已收口；UXP 进入 V1 实现） |
| 创建日期 | 2026-08-22 |
| 更新日期 | 2026-08-24 |
| 关联 PRD | 第 15、21、22、23、25 节 |
| 关联 ADR | `ADR-001-Photoshop执行通道验证策略.md`、`ADR-002-改版Photoshop2025试点兼容目标.md`、`ADR-003-RenderPlan图层路径顺序语义.md`、`ADR-004-JPG-sRGB导出策略.md`、`ADR-005-替换栈逐层结构契约.md`、`ADR-006-替换后目标智能对象名称契约.md`、`ADR-007-TV13受控失败与恢复证据契约.md`、`ADR-008-大型验证夹具增量更新安全策略.md`、`ADR-009-TV14执行通道性能外推收口.md` |
| 候选环境 | 正式候选基线为 Windows + Photoshop `23.3+`；当前首轮试点固定使用改版 PS2025 `26.8.1`，PS2022 冒烟暂不阻塞本轮 |
| 工作分支 | `codex/uxp-validation-v1` |

## 目标

使用用户提供的真实 PSD、源素材和人工参考结果，交付一个最小纵向 UXP 原型和可复现验证报告，证明或否定 UXP 作为 V1 Photoshop 生产执行通道的能力、视觉一致性、稳定性、安装通信和绝对处理速度。

## 必读入口

- `AGENTS.md`
- 当时依据 PRD 1.0；当前唯一基线为 `产品需求文档-电商详情页智能生产系统-PRD-1.2.md`，中间版本和历史内容通过 Git 追溯。
- `docs/当前项目状态.md`
- `docs/架构决策/ADR-001-Photoshop执行通道验证策略.md`
- `docs/验收矩阵.md`
- `需求样本/第一组/模板PSD/output/_inspection/inspection-report.json`
- `需求样本/第一组/模板PSD/output/_inspection/manifest-skeleton.json`

外部参考 Demo：

`C:\Users\79122\Desktop\详情页自动化\releases\详情页自动化-完整包-0.2.8.3.1.zip`

Demo 只用于了解已证明的 Photoshop 操作、人工流程和操作描述符。不得复制其固定七页业务模型作为新架构，也不要求运行它建立正式性能基准。

## 开发机与实机分工

- 当前对话所在电脑是主开发机，不要求安装 Photoshop。
- 主开发机负责桌面端代码、UXP 插件代码、`RenderPlan`、静态预检、协议 bridge、单元测试、日志解析和文档。
- 远程 Photoshop 电脑只负责 UXP 宿主运行时验证、真实 PSD 副本操作、JPG/PSD 导出、弹窗观察和绝对批量性能测试。
- 每次需要实机验证时，从主开发机打包或复制插件目录到远程电脑；回收结构化日志、截图和输出核对结果后再继续开发。
- 没有 Photoshop 的主开发机不能单独证明插件 API、智能对象替换或最终速度通过；所有这类结论必须以远程实机证据为准。

本轮硬验证目标是远程改版 Photoshop 2025 `26.8.1 20250624.r.8 5e0d05d x64`。目标是让该具体构建能够运行执行组件；这不等同于承诺所有改版 Photoshop 兼容。

## 实机环境

- Photoshop 安装在另一台电脑；当前可远程操作 PS2025 `26.8.1`。
- 用户可通过 UU 远程操作，或在需要时协助完成必须的人机步骤。
- 首次连接后记录 Photoshop 精确版本、Windows、CPU、内存、GPU、磁盘类型和 Photoshop 暂存盘设置。
- 若 Photoshop 低于 `23.3`，先报告环境差距，不擅自安装或升级软件。

## 范围

1. 定义最小、渲染器中立的 `RenderPlan` 样例，覆盖页面、图片框、源图、裁切、缩放、偏移、输出 JPG 和可选 PSD。
2. 搭建轻量 UXP Manifest v5 验证插件，不建设正式产品面板。
3. 验证插件启动、桌面端本机握手、任务接收、进度和结构化错误返回。
4. 在模板副本上完成一个真实图片框的读取、置入、裁切、缩放、移动和 JPG 导出。
5. 验证可选 PSD 中的智能对象和文字能够实际修改、保存并再次打开。
6. 扩展到四图片框复杂页面，验证剪贴、蒙版、层级和固定内容。
7. 扩展到一个真实款式的整组可验证页面。
8. 在一个会产生部分输出的稳定检查点执行一次受控失败，验证定位、清理和后续可继续。
9. 使用预生成 `RenderPlan` 验证 10 款约 70 张纯 JPG 渲染通道的绝对机器时间，并为完整产品的 5 分钟总预算提供证据。
10. 更新验收矩阵并提交技术验证报告和生产选型建议。

## 非目标

- 不开发完整桌面 UI、模板库、批次队列或本地数据库。
- 不开发正式 AI 选图、变体分组或动态尺码表模块。
- 不支持 200 款长批次。
- 不执行 UXP 与 JSX 的正式速度或稳定性对比。
- 不建设通用故障注入平台，也不穷举每个内部失败点；TV-13 只验证一次能够覆盖部分输出清理的代表性失败。
- 不实现商业账号、授权、计费、自动更新或电商平台上传。
- 不承诺所有用户 PSD 或所有非官方 Photoshop 安装兼容；首轮必须验证指定的改版 PS2025 `26.8.1`。
- 不在验证结果出来前建设 JSX 和 UXP 两套完整生产引擎。

## 关键约束

- 用户原始文件只读；每次运行前确认实验目标是副本。
- UXP 只执行结构化动作，不接收自然语言或任意模型生成的 Photoshop 指令。
- 不通过降低 JPG 质量、扁平化可编辑 PSD 或减少必要页面来换取速度。
- 不把首次插件开发环境步骤包装成最终用户安装流程。
- 无法自动处理的系统弹窗必须安全暂停，不盲目点击。

## 完成条件

- `TV-01` 至 `TV-08`、`TV-11` 至 `TV-13` 均有证据并被标记为通过或失败，不允许无证据地跳过。
- UXP 原型和验证工具具有从空环境开始的复现说明。
- 输出视觉达到 PRD 的人工参考标准，且无旧商品残留、错款或固定图层误改。
- 使用预生成 `RenderPlan` 的 10 款约 70 张纯 JPG 渲染通道不单独超过 5 分钟，并报告分段耗时和瓶颈。该结果是完整产品 5 分钟目标的必要条件，不替代桌面端、AI 和批次队列接通后的最终端到端验收。
- 最终报告明确给出生产 Photoshop 执行通道建议，并说明证据、限制和失败回退边界。
- `docs/当前项目状态.md`、本任务书和 `docs/验收矩阵.md`完成交接更新。

## 后续交接顺序

1. 将 UXP 执行通道结论交接给 V1 桌面端实现；完整产品 `machine_total` 在桌面端 MVP 接通后作为产品验收复核。
2. 独立开展 AI-01 与 SIZE-01，不再把它们混入 UXP 可行性门。

## 验证记录

| 日期 | 环境或命令 | 结果 | 证据位置 |
| --- | --- | --- | --- |
| 2026-08-22 | 任务创建 | 尚未开始实机验证 | - |
| 2026-08-22 | `tools/validate_uxp_prototype.ps1` | PASS：Manifest v5、PS 23.3.0 候选下限、示例 RenderPlan 2 页/5 框、源文件只读策略通过静态预检 | `docs/验证/UXP技术可行性验证计划.md`、`uxp-validation-plugin/` |
| 2026-08-22 | `tools/uxp_validation_bridge.ps1` + loopback POST | PASS：本机 bridge 返回 200 和 `render-plan/v1`，一次握手后退出；UXP 内调用待实机 | `docs/验证/UXP技术可行性验证报告-阶段1.md` |
| 2026-08-22 | 样本审计复核 | 选定 `G-详情-03.psd`（单框）和 `G-详情-05-细节.psd`（四框）；确认 `G-详情-01` 与 backup 哈希重复 | `docs/验证/UXP技术可行性验证报告-阶段1.md` |
| 2026-08-22 | Git 基线检查 | 初次在受限沙箱中创建分支失败；获准写入 Git 元数据后已创建 `codex/uxp-validation-v1`。`.gitignore` 排除样本和生成物 | `docs/验证/UXP技术可行性验证报告-阶段1.md` |
| 2026-08-22 | 首个白名单基线提交 | PASS：只提交治理、PRD、验证插件和工具；`需求样本/` 未进入提交 | `4ad6be211030c9ad82bc7f5dbfc79caeb5e39c59` |
| 2026-08-23 | `0.1.1` 诊断版预检与远程打包 | PASS：Manifest v5、临时全域网络诊断权限、RenderPlan 2 页/5 框通过静态预检；生成单文件包且不含用户数据 | `uxp-validation-probe-0.1.1.zip`，SHA-256 `3244521E66F532BDF70D537EB7A12560DC3DC247E47FCA85B7AF63259E5DEE79` |
| 2026-08-23 | 远程加载两页 RenderPlan 协议样例 | PASS（仅协议加载）：面板显示 `ok: true`、`pages: 2`；旧执行按钮未运行换图 | 用户截图；`docs/验证/UXP技术可行性验证报告-阶段2-单框执行准备.md` |
| 2026-08-23 | `0.2.0` 单框执行器、预检、fixture 和远程打包 | PASS（本地）：JS 语法、Manifest、单框计划、输入哈希和 ZIP 重新解压均通过；Photoshop 换图尚未实机执行 | `docs/验证/UXP技术可行性验证报告-阶段2-单框执行准备.md` |
| 2026-08-23 | 改版 PS2025 执行 `0.2.0` 单框测试 | SAFE FAIL：审计路径 `2` 被按 UXP 顶部到底部数组下标直接解释，目标不是智能对象；面板返回 `TARGET_NOT_SMART_OBJECT`，`partial_outputs_removed: true`、`source_template_saved: false` | 用户实机截图；`docs/验证/UXP技术可行性验证报告-阶段2-单框执行准备.md` |
| 2026-08-23 | `0.2.1` 路径顺序修复、本地预检与 ZIP 重解压复验 | PASS（本地）：显式 `psd_bottom_to_top`，逐级转换 UXP 下标并增加审计名交叉检查；JS 语法、Manifest、两份 RenderPlan、fixture 输入哈希和包内容通过 | `docs/架构决策/ADR-003-RenderPlan图层路径顺序语义.md`、`docs/验证/UXP技术可行性验证报告-阶段2-单框执行准备.md` |
| 2026-08-23 | 改版 PS2025 执行 `0.2.1` 单框测试并回收输出 | PASS（核心能力）：正确绑定 `back_main`，完成替换、cover、JPG/PSD/evidence 导出；JPG 与人工参考视觉一致；PSD 保留内嵌智能对象。JPG 未嵌入 ICC profile | evidence JSON、用户回传 JPG/PSD；阶段 2 报告 |
| 2026-08-23 | Photoshop 打开输出 PSD 并进入 `back_main` 内容 | PASS（部分可编辑性）：成功打开 `7Y9A3760.jpg` 智能对象内容，未保存修改；实际编辑回写和文字编辑待测 | 用户实机截图；阶段 2 报告 |
| 2026-08-23 | 修正 Windows PowerShell 5.1 UTF-8 读取并执行运行后复核 | PASS：`passed: true`；输入 PSD/JPG 哈希不变，PSD/JPG/evidence 三项输出哈希已记录 | `tools/verify_uxp_single_frame_fixture.ps1`；阶段 2 报告 |
| 2026-08-23 | `0.2.2` 显式 sRGB 修复、ICC 验收和交接包复验 | PASS（本地）：使用官方 `Document.convertProfile`；PSD 在转换前保存；RenderPlan/evidence/APP2 校验完整；两个 ZIP 重解压后版本、文件数和输入哈希通过 | `docs/架构决策/ADR-004-JPG-sRGB导出策略.md`；阶段 2 报告 |
| 2026-08-23 | 改版 PS2025 执行 `0.2.2` 单框 sRGB 复测 | FAIL（TV-07）：JPG、PSD 和 evidence 已生成，验收脚本通过前置结构与请求参数检查后，在实际 JPEG 检查处返回 `Generated JPG has no ICC_PROFILE APP2 segment.`；`embedColorProfile: true` 未在该宿主产生 ICC APP2 | 用户回传 PowerShell 输出；阶段 2 报告 |
| 2026-08-23 | `0.2.3` JPG ICC 后处理候选实现与本地验证 | PASS（仅本地，未构成实机通过）：Node 语法、JPEG ICC 单元测试、Pillow `12.3.0`/LittleCMS `2.19` 固定 profile 复现、PowerShell 5.1 verifier、静态预检和合成输出 `-RequireOutputs` 闭环通过；合成结果 `adapter_injected: true`，ICC `588` 字节，SHA-256 `14FB97CE...5920D` | `uxp-validation-plugin/jpeg-icc.js`、`uxp-validation-plugin/main.js`、`tools/generate_srgb_icc_profile.py`、`tools/verify_uxp_single_frame_fixture.ps1`、ADR-004 |
| 2026-08-23 | `0.2.3` 最终打包与 ZIP 重解压复验 | PASS（本地）：Manifest `0.2.3`；插件包 12 个文件、fixture 包 6 个文件；插件包不含 PSD/JPG/PNG，包含 `jpeg-icc.js`、NOTICE 和 profile 生成脚本；fixture 输入哈希通过，包内 verifier 包含 PowerShell int-cast 修复；失败清理 evidence 改为按重新枚举后的实际残留结果报告 | 插件 ZIP SHA-256 `A76883E842D5DBA02309755BD44D8109B8C09657231ACE04B362A884E20E9AA3`；fixture ZIP SHA-256 `204DAFC66594500EBCFAF16EB2B6C867D000FE86A282419CB7D37B3F575F12F7` |
| 2026-08-24 | 改版 PS2025 `26.8.1` 执行 `0.2.3` 单框 sRGB 复测 | PASS（技术校验）：远程 verifier 返回 `passed: true`；输入 PSD/JPG 哈希不变；`photoshop_embedded_before_postprocess: true`、`adapter_injected: false`、`embedding_verified: true`，证明 Photoshop 原生嵌入路径生效；ICC `3144` 字节，SHA-256 `2B3AA164...AF7E` | 输入 `B94E7458...4A35` / `04720404...BDB`；输出 PSD `DD997E7F...6949`、JPG `D18AD1D8...80A4`、evidence `358EE8A9...FB0F`；阶段 2 报告 |
| 2026-08-24 | 回收 `0.2.3` JPG/PSD/evidence 并完成视觉验收 | PASS（TV-07）：主开发机重算三项输出哈希均与远程 verifier 一致；用户确认“肉眼没问题”，最终 JPG 无白边、旧商品残留、错向或固定文字/装饰异常 | 用户回传三项产物和截图；阶段 2 报告 |
| 2026-08-24 | TV-08 样本结构审计、校验器和独立远程包 | PASS（本地准备）：确认金标 PSD 含 `back_main` 和 16 个真实文字层；固定正面图 `7Y9A3750.jpg` 与现有文字层；Windows PowerShell 5.1 前置检查、ZIP 重解压、工作副本生成、未编辑副本拒绝和后验完整路径均通过；主机深检器已完成基线解析并拒绝未编辑 PSD。TV-08 仍等待 Photoshop 人工关闭重开证据 | `docs/验证/UXP可编辑PSD实机验证操作卡.md`、`tools/tv08/verify_returned_psd.py`；`uxp-validation-tv08-editability-0.2.3.zip`，SHA-256 `D31457A9A9E87B73C1907F5E3F123C5319739F48603CFC32141557B2829954A5` |
| 2026-08-24 | TV-08 首次远程启动与中文路径修复 | SAFE FAIL 后已修复（本地）：旧包的批处理将尾随反斜杠目录与 `-Prepare` 拼接，失败发生在复制和校验前，未修改 baseline、源图或 evidence。`0.2.3.1` 移除易错目录实参、在脚本绑定后回退 `$PSScriptRoot`、显式调用系统 Windows PowerShell，并使用内置 .NET SHA-256；已在中文目录通过真实 BAT 准备入口，且未编辑副本由验证入口正确拒绝。TV-08 仍等待 Photoshop 人工证据 | `uxp-validation-tv08-editability-0.2.3.1.zip`，SHA-256 `B89F40E94C7272D363A6F49803525BCE7AB5A1DAE5A582701843478A4285E377` |
| 2026-08-24 | TV-08 改版 PS2025 人工编辑、关闭重开与回传深检 | PASS（TV-08）：用户在 working 副本中将 `back_main` 替换为 `7Y9A3750.jpg`，把既有文字层内容改为 `TV08 EDIT OK`，保存、关闭并重开确认；远程自动校验通过。主开发机解析回传 PSD，确认 `790x1252`、8 位 RGB、42 个递归图层、16 个文字层；目标图层 ID `257`/`55` 和类型保持，正面 payload 哈希准确，40 个非目标图层不变，36 个可解码像素层一致 | `tools/tv08/verify_returned_psd.ps1`、`tools/tv08/verify_returned_psd.py`；working PSD SHA-256 `6B2215210F2FEBA224F0263E3EB640DA4A7739D5CED6BAEE91B95EF6229BBBC8`，远程 evidence SHA-256 `7470AF8C84ED88D1EABBC83ED4B440796975A9BAB1B99166298ACCDBE107DDB3`；用户重开截图 |
| 2026-08-24 | UXP `0.3.0` 四框执行器、TV-06 fixture 和单 ZIP 回环 | PASS（本地准备）：四目标先统一预检路径/类型/可见性/剪贴/窗口/旧栈/唯一 ID；替换后递归比对固定结构和非目标 bounds；Node 语法、RenderPlan 正反协议测试、JPEG ICC 单测、Windows PowerShell 5.1 静态预检、输入哈希、打包和 ZIP 重解压复验均通过。TV-06 等待实机 | `docs/验证/UXP技术可行性验证报告-阶段3-四框执行准备.md`；`uxp-validation-tv06-multi-frame-0.3.0.zip`，SHA-256 `0ED7F934897A9D5C8561E5A3EAFCD6DC671A6AE2FFD48D2212F39052F697B5F9` |
| 2026-08-24 | TV-06 首次远程输入检查与 PowerShell 5.1 启动修复 | SAFE FAIL 后 PASS：旧 BAT 在参数绑定阶段得到空 `FixtureRoot`，失败发生在 Photoshop 运行前；显式传入 fixture 后远程返回 `passed: true`，五项输入哈希全部正确。`0.3.0.1` 将目录回退移到参数绑定后、BAT 显式传目录，并在打包及 ZIP 回环中加入无参数 PowerShell 5.1 回归检查；插件执行代码仍为 `0.3.0` | `docs/验证/UXP技术可行性验证报告-阶段3-四框执行准备.md`；修正版 ZIP SHA-256 `1193BF69A7A7AC2BC01866D6B190A854F65F2867B66CBA4583FBEBDE151205F3` |
| 2026-08-24 | TV-06 `0.3.0` 首次 Photoshop 执行与 `0.3.1` 诊断修复 | SAFE FAIL 后完成复测准备：四框统一预检在任何替换前发现审计名不一致并关闭未保存工作副本；`executeAsModal` 把自定义错误包装为 `UNEXPECTED_ERROR`，具体 frame/名称详情丢失。`0.3.1` 只修复模态错误保真，原映射和执行逻辑不变；本地语法、错误保真、协议、ICC、静态预检及 ZIP 回环通过 | `uxp-validation-tv06-plugin-0.3.1.zip`，SHA-256 `55908E755B042D587AE01B21287E0A07C3892372DB5FEFEF491DE99BA6C793A1`；阶段 3 报告 |
| 2026-08-24 | TV-06 `0.3.1` 远程安全证据与 `0.3.2` 双错误保真 | SAFE FAIL 后完成复测准备：远程证据为零个 mutated frame、无部分变更、输出清理确认通过；文档关闭异常覆盖原始预检错误。`0.3.2` 在模态外部保存原始错误，并把关闭异常降为附加详情；本地双错误保真、协议、ICC、静态预检和 ZIP 回环通过 | `uxp-validation-tv06-plugin-0.3.2.zip`，SHA-256 `331D65EFBAAF469C058B5CA2E882702669F28B04512C22EBF8B21974EFBF5167`；用户四张面板截图；阶段 3 报告 |
| 2026-08-24 | TV-06 `0.3.2` 根因证据与 `0.3.3` 审计名兼容 | SAFE FAIL 后完成根因修复：frame-11 路径正确命中 ID `539` 智能对象；期望 `\tsleeve`、实际 `sleeve`，证明是 UXP 首尾空白规范化而非错层。`0.3.3` 只接受非空名称主体相同的首尾空白差异，其他结构门控不变；本地正反测试和 ZIP 回环通过 | `uxp-validation-tv06-plugin-0.3.3.zip`，SHA-256 `83A176BA9817C656343B041A6B70B96E7C953BA2E37098C2ADF32BA075FD1963`；用户六张面板截图；阶段 3 报告 |
| 2026-08-24 | TV-06 `0.3.3` 文档句柄失效与 `0.3.4` DOM 重新获取 | SAFE FAIL 后完成根因候选修复：越过审计名后，替换函数返回前出现 document id undefined，关闭旧对象时同样失败；输出清理确认。`0.3.4` 每框替换前后重新获取受控 Document/Layer 并复核目标 ID，细分 attempted/replaced/completed 阶段；本地文档唯一性、协议、ICC、静态预检和 ZIP 回环通过 | `uxp-validation-tv06-plugin-0.3.4.zip`，SHA-256 `E7EE8A6CFC45B87A970A77DEC58796B522B2ACA610B602A698F7411B94CC4F75`；用户五张面板截图；阶段 3 报告 |
| 2026-08-24 | TV-06 `0.3.4` 实机打开阶段回归与 `0.3.5` 修复 | SAFE FAIL：`app.open()` 返回后同一 modal 内 `app.documents` 为空，`WORKING_DOCUMENT_NOT_UNIQUE`，四个 frame 均未执行，输出清理确认通过；Photoshop 画布已出现，说明文档对象和集合存在发布时序差异。后续确认原版 Photoshop 也有同类行为，不能归因于改版环境。`0.3.5` 改为信任并校验 `app.open` 返回值，替换后优先使用 `app.activeDocument`，集合仅作后备和重复检查；本地竞态回归、协议、ICC、静态预检和 ZIP 回环通过 | `uxp-validation-tv06-plugin-0.3.5.zip`，SHA-256 `5EC2C63B59B6C971CA4B9535A878204E1F5C94DEFB976305BF7ADD2179AC4E60`；用户五张面板截图 |
| 2026-08-24 | TV-06 `0.3.5` 清洁重启后四框执行 | PASS（执行阶段）：确认无遗留验证副本文档后，面板返回 `Multi-frame copy test completed`、`ok: true`；四框执行和三项输出已完成。TV-06 尚待自动 verifier、JPG 人工对照和输出 PSD 重开证据 | 用户面板截图；`uxp-validation-tv06-verifier-0.3.5.zip`，SHA-256 `76E5A13FB93E696606E5AFEF32F5C1DB80A504C23CE44609FAF7E189B07B0D2F` |
| 2026-08-24 | TV-06 `0.3.5` 回传产物深检与视觉验收 | PASS（部分）：evidence 记录四个 frame 全部 attempted/mutated/completed、46 层结构审计通过、输入模板未保存；主开发机深检确认输出 PSD 可重新解析，四个目标仍为可编辑智能对象且 payload 哈希准确，42 个非目标图层和 37 个可解码像素层保持；用户及主开发机确认 JPG 与人工参考肉眼一致。微信回传 JPG 被重新编码并移除 ICC，只用于视觉检查。TV-06 尚待远程 verifier 检查原始 JPG 及 Photoshop 重开截图 | `docs/验证/TV06回传PSD深检结果-0.3.5.json`；输出 PSD SHA-256 `193CABA7...E6E`，evidence SHA-256 `FD9CF797...45E3`；用户回传 JPG 与视觉确认 |
| 2026-08-24 | TV-06 `0.3.5` 远程最终自动校验 | PASS（自动验证）：更新后的 `VERIFY-TV06.bat` 对原始 fixture 返回 `passed: true`；输入模板和四张源图哈希保持批准值；输出 PSD/JPG/evidence 哈希分别为 `193CABA7...E6E`、`07741D43...567E`、`FD9CF797...45E3`；画布 `790x1115`、PSD 为 8 位 RGB、原始 JPG 含 ICC、`fixed_structure_unchanged: true`、`frame_count: 4`。TV-06 只待 Photoshop 重开截图 | 用户回传完整 PowerShell 结果；`tools/tv06/verify_fixture.ps1` |
| 2026-08-24 | TV-06 输出 PSD Photoshop 重开与最终收口 | PASS（TV-06）：顶部标签确认重开的是 `G-详情-05-validation-output.psd`；画布与已验收 JPG 一致，图层面板显示 `front`、`sleeve`、`collar` 等目标仍为智能对象。结合远程 verifier、主机逐层深检和视觉验收，TV-06 全部完成条件满足 | 用户回传 Photoshop 画布/图层面板截图，SHA-256 `8EE1EA28...7C91`；阶段 3 报告 |
| 2026-08-24 | TV-11 整款六页 RenderPlan、执行器、fixture 和远程包 | PASS（本地准备）：整款计划固定为 6 个非尺码页面、11 个图片框、默认 JPG-only；Node 语法、RenderPlan 正反协议（含缺页、重复页/框、错误哈希、错误路径、错误窗口、生成图和 PSD 输出等反例）、JPEG ICC 单测、Windows PowerShell 5.1 静态预检、6 个模板副本/6 个源图/6 个人工参考哈希、ZIP 解压回环全部通过。TV-11 尚未在 Photoshop 实机执行或视觉验收 | `docs/验证/UXP整款六页实机验证操作卡.md`、`tools/tv11/verify_fixture.ps1`、`uxp-validation-plugin/render-plans/whole-product-a6015-render-plan.json`；`uxp-validation-tv11-whole-product-0.4.0.zip`，SHA-256 `9022735640C43D5635955C1ECC19B7181E87A1BCC551ACCB84DE5BCA07DAFABA`，421445651 bytes |
| 2026-08-24 | TV-11 `0.4.0` 首次整款远程打开阶段 | SAFE FAIL：第一张 `G-详情-01-validation-copy.psd` 在打开阶段没有得到可用的返回/活动/文档集合对象；0 个 frame attempted/mutated/completed，输出清理确认无残留，输入未修改。原版 Photoshop 也存在同类行为，因此不能归因于改版环境；旧 evidence 还会把 Document getter 异常折叠为 `null`/`[]`，目前只能确认打开适配器不可靠 | 用户面板错误截图；`OPENED_DOCUMENT_MISMATCH` evidence |
| 2026-08-24 | TV-11 `0.4.1` 整款远程复测 | SAFE FAIL：第一张 G01 已成功打开并进入 `preflight_bindings`，证明 `0.4.0` 的打开阻塞已越过；`G-详情-01.frame-3` 计划要求 `expected_clipping: true`，Photoshop 实际返回 `false`，因此在任何 frame attempted/mutated/completed 前停止。部分输出清理确认，输入未保存。主开发机同哈希 PSD 结构报告确认 `front_main` 路径 `3` 为可见智能对象且真实 `clipping: false`，根因是 RenderPlan 校准值写错，不是 Photoshop 版本问题 | 用户四张插件面板截图；本地 `inspection-report.json`（G01 模板 SHA-256 `164E4AF1...2F4`） |
| 2026-08-24 | TV-11 `0.4.2` G01 剪贴校准修复、完整包和增量包 | PASS（本地准备）：只将 G01 `front_main` 的剪贴期望修正为 `false`；对照 6 个同哈希真实模板审计 11 个目标，另外 10 个路径、名称、智能对象类型和剪贴状态均一致。统一 manifest、界面、evidence 和 verifier 版本；Node 语法、RenderPlan 正反测试、ICC、静态预检、两套 PowerShell 5.1 输入检查和完整 ZIP 解压回环通过。另提供不含 PSD/JPG 的 45 KB 增量包，自动选择并校验现有 `0.4.1`、备份少量配置、原地更新并重命名；已在完整 `0.4.1` ZIP 副本上端到端演练通过，G01 模板哈希保持 | 完整包 `uxp-validation-tv11-whole-product-0.4.2.zip`，SHA-256 `ECC8D64A...EAF1`；优先使用增量包 `uxp-validation-tv11-update-0.4.2.zip`，SHA-256 `D828F8D4BD7059E235D3AB1D9CC4DAE22B1857001FF028088EC0DB6397A2C9AA`，45274 bytes |
| 2026-08-24 | TV-11 `0.4.2` 实机文档发布时序 | SAFE FAIL：第一张 G01 在 `open_template_copy` 阶段等待 25 次仍只得到 `opened_document_name: null`、`active_document_name: null`、`open_document_names: []`；0 个 frame attempted/mutated/completed，输入未保存，部分输出清理确认。失败说明在同一 opening modal 内等待不会促使该次宿主发布 Document，不是素材或模板结构错误 | 用户五张插件面板截图；`OPENED_DOCUMENT_MISMATCH` evidence |
| 2026-08-24 | TV-11 `0.4.3` 跨 modal 文档发布修复 | PASS（本地准备）：每页第一个短 modal 只调用 `app.open()`，退出后在 modal 外等待 Document，再由第二个 modal 执行画布/11 框结构门控、替换、审计、sRGB/JPG 和不保存关闭；失败清理保持。新增 `app.open` 返回空且 Document 仅在 modal 退出后出现的竞态回归；Node 语法、RenderPlan 单测、ICC、静态预检和增量 ZIP 回环通过。增量更新器已在完整 `0.4.2` 副本上完成输入哈希校验、备份、更新、重命名和更新后复验 | `uxp-validation-tv11-update-0.4.3.zip`，SHA-256 `C08E81BC33734551FF5AFCB7BEA84A58035B779AED9B0A5124527B5AA953B6CE`，45868 bytes，不含 PSD/JPG/PNG |
| 2026-08-24 | TV-11 `0.4.3` 实机跨 modal 复测 | SAFE FAIL：opening modal 约 157 ms，退出后继续等待约 5.3 秒，25 次观测仍为 `opened_document_name: null`、`active_document_name: null`、`open_document_names: []`。因此 modal 边界不是充分根因，更可能是文件夹子 Entry 与 DOM `app.open()` 的宿主兼容；0 frame 修改、输入未保存、输出清理确认 | 用户四张插件面板截图；`OPENED_DOCUMENT_MISMATCH` evidence |
| 2026-08-24 | TV-11 `0.4.4` Action Manager session-token 打开适配 | PASS（本地准备）：按 Adobe 官方文档以临时 session token 和 `target: {_path, _kind: "local"}` 调用 `batchPlay open`；短 modal 内 silent 执行，要求恰好一个结果，识别 resolved error、Promise reject 并脱敏，modal 外等待 Document 且校验返回/DOM 文档 ID。token 不持久化也不进入 evidence；11 框结构门控、失败清理和不保存输入保持。Node 语法、RenderPlan/竞态/错误路径、ICC、静态预检、无媒体 ZIP 回环通过；从完整 `0.4.2` 经 `0.4.3` 再到 `0.4.4` 的升级演练三轮输入哈希均通过 | `uxp-validation-tv11-update-0.4.4.zip`，SHA-256 `497C5964D8FD0D8BD50503CAB360222705CB8F7294D1904CF6DB8E9B085A8B8B`，46929 bytes，不含 PSD/JPG/PNG；AdobeDocs `createSessionToken` / `batchPlay` / `executeAsModal` |
| 2026-08-24 | TV-11 `0.4.4` 最大化窗口后实机复测 | SAFE FAIL，但打开通道通过：此前 Photoshop 返回显示器工作区空间不足；保持 Photoshop 最大化后，第一张 G01 已由 Action Manager 成功打开并进入 `preflight_bindings`。预检准确停在 `G-详情-01.frame-3` 的下层路径 `2`：执行器统一要求剪贴，Photoshop 实际为非剪贴。零个 frame attempted/mutated/completed，输入未保存，输出清理确认。主开发机同哈希结构报告确认路径 `2` 为可见智能对象且真实 `clipping: false` | 用户四张面板截图；错误码 `SUPERSEDED_CLIPPING_MISMATCH`；模板 SHA-256 `164E4AF1...2F4` |
| 2026-08-24 | TV-11 `0.4.5` 替换栈逐层结构合同和小型增量包 | PASS（本地准备）：整款计划将纯路径 `superseded_paths` 升级为逐层 `superseded_layers`，明确路径顺序、类型、可见性和剪贴状态；14 个下层图层中只允许 G01 路径 `2` 为非剪贴，其余 13 个仍严格要求剪贴。旧单框/四框协议保留兼容。Node 语法、RenderPlan 正反例、ICC、Windows PowerShell 5.1 静态预检、无媒体 ZIP 回环和 `0.4.2 → 0.4.3 → 0.4.4 → 0.4.5` 升级回环通过，6 个模板及 6 张源图哈希保持 | `docs/架构决策/ADR-005-替换栈逐层结构契约.md`；`uxp-validation-tv11-update-0.4.5.zip`，SHA-256 `846FC878EDCDC32F7B01BFB4DC7EF44D14F1176B178740F421F40559014E6E85`，47921 bytes，不含 PSD/JPG/PNG |
| 2026-08-24 | TV-11 `0.4.5` 实机结果与 `0.4.6` 名称审计修复 | SAFE FAIL（实机）后 PASS（本地修复）：`0.4.5` 已生成六张视觉正确的 JPG，但 `snapshot_after` 将两个已替换目标智能对象改名为源文件名而误报 `LAYER_TREE_CHANGED`，输出清理已执行。`0.4.6` 仅允许已注册目标路径的名称变化并记录 `target_name_changes`，非目标名称仍严格失败；Node 正反例、ICC、PowerShell 5.1 静态预检、无媒体 ZIP 回环和完整 `0.4.2 → 0.4.3 → 0.4.4 → 0.4.5 → 0.4.6` 升级回环通过，14 个替换栈合同与六个模板/六张源图哈希保持 | `docs/架构决策/ADR-006-替换后目标智能对象名称契约.md`；`uxp-validation-tv11-update-0.4.6.zip`，SHA-256 `B066D45012016545FDEDBB768D7FE87FD5D682B53E828C2BDBC1DCF80F256B4E`，48177 bytes，不含 PSD/JPG/PNG |
| 2026-08-24 | TV-11 `0.4.6` 整款实机自动验收与视觉收口 | PASS：6 个 JPG、11 个 frame、输入模板/源图/人工参考哈希全部符合；6 个 JPG 均有 ICC marker，固定结构未变化，默认未输出 PSD。输出 evidence SHA-256 `DC36074580D465177FFAC26AE2F18A20E950F08A9F2DF92B879059F03666D5E4`；六张 JPG SHA-256 已由远程 verifier 固定记录。用户已逐张与 `fixture/reference` 对照并确认无误 | 用户回传 `TV-11 automated verification passed` 完整 PowerShell 结果及“确认无误” |
| 2026-08-24 | TV-13 `0.4.7` 受控失败和继续运行包 | PASS（本地准备）：故障合同固定在前两页 JPG 完成后、第三页首个 frame 前，错误码固定为 `TV13_CONTROLLED_FAILURE`；清理结果保留到同会话正常六页 evidence。Node 正反协议、ICC、PowerShell 5.1 静态预检、无媒体 ZIP 回环和完整 `0.4.6 → 0.4.7` 升级回环通过；更新后输入哈希和六页/十一框计划保持，两个 TV-13 输出目录为空 | `docs/架构决策/ADR-007-TV13受控失败与恢复证据契约.md`；`uxp-validation-tv13-update-0.4.7.zip`，SHA-256 `869D447C2C868759B48CC30D00E317B5896FED1CD99BA3E8E49C705DB682EB1C`，48831 bytes，不含 PSD/JPG/PNG |
| 2026-08-24 | TV-13 `0.4.7` 远程受控失败、清理和继续运行 | PASS：改版 PS2025 `26.8.1` 在前两页完成后返回固定错误 `TV13_CONTROLLED_FAILURE`；已生成的 G01/G03 两张 JPG 均删除，失败目录零残留，输入未保存。同一插件会话未重载即正常完成六页，最终为 6 JPG、0 PSD，且 evidence 嵌入前一次失败定位和清理记录 | 远程 `VERIFY-TV13.bat` 返回 `passed: true`、`continued_run_passed: true`；evidence SHA-256 `11FB9555A0B273561794568771E510418EF0413D6291998735FA6C9690127F1D` |
| 2026-08-24 | TV-13 增量更新目录占用与 `0.4.7.1` 交付修复 | SAFE FAIL（交付步骤）后 PASS（本地修复）：旧更新器最后移动 421 MB fixture 时被旧进程占用，产生两个部分目录；受保护输入未修改，人工只复制已验证插件目录后输入检查通过。`0.4.7.1` 改为不移动根目录、白名单原地更新、可重复恢复并比较受保护目录全树哈希；完整升级、幂等重跑、半更新恢复和非空输出保护均通过。插件仍为 `0.4.7`，无需 Photoshop 重测 | `docs/架构决策/ADR-008-大型验证夹具增量更新安全策略.md`；`uxp-validation-tv13-update-0.4.7.1.zip`，SHA-256 `45E26EAA4AE945798AF810321110D4F084828B6142FA67FD8817E14D0648881C`，50113 bytes，不含媒体文件 |
| 2026-08-24 | TV-14 执行通道性能外推收口 | PASS（阶段性执行通道门）：产品负责人确认单款纯 Photoshop 热运行仅十几秒；按每款保守上限 20 秒外推，10 款约 200 秒，低于 300 秒目标，保留约 100 秒 ICC/文件操作余量。完整产品 `machine_total` 仍待桌面端 MVP 接通后复核，不阻塞 UXP 进入 V1 实现 | `docs/架构决策/ADR-009-TV14执行通道性能外推收口.md`；产品负责人本轮批准记录 |

## 交接

- 已完成：改版 PS2025 已通过加载、probe、bridge、RenderPlan、正确智能对象绑定、替换、cover、JPG/PSD 导出、可编辑 PSD、整款六页、连续运行和部分输出失败恢复；TV-01 至 TV-08、TV-11 至 TV-14 已关闭。
- 遗留问题：UXP 执行通道验证已完成；完整产品 `machine_total`、AI-01 与 SIZE-01 属于 V1 实现后的独立验收，不回写为 UXP 阻塞项。
- 下一步：进入 V1 桌面端方案与实现，沿用 UXP + renderer-neutral `RenderPlan` 边界；`0.4.7.1` 仅作为未来安全更新器保留。
- 阶段 1 基线提交：`4ad6be211030c9ad82bc7f5dbfc79caeb5e39c59`；本轮交接更新见分支最新提交。
