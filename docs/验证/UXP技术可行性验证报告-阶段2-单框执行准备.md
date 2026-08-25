# UXP 技术可行性验证报告（阶段 2：单框执行与路径修复）

## 结论

`0.2.0` 已在远程 Photoshop 2025 `26.8.1` 执行，但在任何换图前因图层路径顺序解释错误而安全中止。它证明了目标类型检查、结构化错误和部分输出清理路径，不能记为智能对象替换成功。

`0.2.1` 已在同一改版 Photoshop 2025 `26.8.1` 上完成单框实机执行。显式 `psd_bottom_to_top` 路径把 PSD 下标 `2` 转为 UXP 下标 `1`，命中 `back_main` 智能对象；`placedLayerReplaceContents`、cover 变换、JPG/PSD 保存均成功。JPG 与人工参考结果肉眼一致，输出 PSD 重新解析后仍包含内嵌智能对象。

本阶段核心能力通过，TV-03 已由运行后输入哈希正式关闭。`0.2.3` 已解决前两版缺少 ICC 的问题并通过技术与视觉验收，TV-07 已关闭。后续 `0.2.3.1` 人工编辑与回传深检也已通过，TV-08 已关闭。TV-06 仍未关闭：复杂剪贴/蒙版页尚未实测。

针对 TV-07，`0.2.2` 已在远程改版 Photoshop 2025 `26.8.1` 生成新 JPG、PSD 和 evidence，但验收脚本在读取实际 JPEG 后返回 `Generated JPG has no ICC_PROFILE APP2 segment.`。本报告中的 `0.2.2` 本地预检通过与远程产物失败必须同时保留，不能把请求参数或面板成功当成 ICC 已嵌入。

`0.2.3` 已在同一改版 PS2025 `26.8.1` 完成远程技术复测，verifier 返回 `passed: true`，输入哈希不变且 Photoshop 原生嵌入的 sRGB ICC 通过完整验证。用户随后回传三项产物并确认 JPG 肉眼无问题，TV-07 已通过；TV-13 仍等待一次代表性受控失败隔离证据。

远程实机已经用 `0.1.1` 证明插件加载、基础 Photoshop API 和同机 bridge 可用，并成功加载旧的两页 RenderPlan 协议样例。旧样例没有执行任何换图；旧按钮 `Execute gated plan` 不得用于渲染结论。

## 样本映射复核

人工参考结果复核后，修正了早期协议样例的素材映射：

- `G-详情-03` 是背面页面，层路径 `2` 必须使用 `7Y9A3760.jpg`，不能使用正面图 `7Y9A3750.jpg`。
- 单框绑定：路径 `2`、类型 `smartObject`、审计名 `back_main`、注册框 `[0, 0, 790, 1252]`。
- `G-详情-05-细节` 路径 `3` 使用 `7Y9A3762.jpg`（水洗/成分标），路径 `7` 使用 `7Y9A3751.jpg`（领口），路径 `11` 使用 `7Y9A3755.jpg`（袖口），路径 `15` 使用 `7Y9A3750.jpg`（正面）。

图层名称只作审计交叉检查；执行绑定仍以已校准的结构路径、智能对象类型和注册框为准。

## `0.2.x` 安全边界

- 只接受 `validation.mode = single_frame_copy`，且只允许一页一框。
- 强制选择 `G-详情-03-validation-copy.psd`、`7Y9A3760.jpg` 和独立输出目录。
- 只解析顶层结构路径 `2`，并在变更前验证智能对象类型、可见性和注册框在画布内。
- 使用 `placedLayerReplaceContents`，随后按注册框执行 cover、中心焦点、缩放和位移。
- 输出新的可编辑 PSD、JPG 和 evidence JSON；输入 PSD 副本始终 `closeWithoutSaving()`。
- 失败时关闭输入副本且删除本次创建的部分输出。
- UXP 内不调用 AI、不生成图片、不遍历替换其他智能对象。

## `0.2.0` 实机安全中止

- 面板状态：`Single-frame test stopped safely`。
- 错误：`TARGET_NOT_SMART_OBJECT` / `Calibrated layer path 2 is not a smart object`。
- 上下文：`job_id = uxp-g03-single-frame-001`、`page_id = G-详情-03`、`frame_id = G-详情-03.frame-2`。
- 清理标记：`partial_outputs_removed: true`、`source_template_saved: false`。
- 解释：审计路径 `2` 的含义正确，但 `0.2.0` 按 UXP 数组顺序直接取下标，选中了错误图层。失败发生在 `placedLayerReplaceContents` 之前。
- 证据边界：面板清理标记已取得，随后 `0.2.1` 在同一输出目录通过无覆盖检查并成功生成，证明换图前失败没有残留旧半成品；该失败发生在任何输出创建之前，不能单独证明部分输出创建后的清理。`0.2.1` 运行后的独立哈希复核已完成，TV-03 为通过；TV-13 仍需一次会产生部分输出的代表性受控失败。

路径协议和修复方式见 `docs/架构决策/ADR-003-RenderPlan图层路径顺序语义.md`。

## `0.2.1` 实机成功证据

- 面板状态：`Single-frame copy test completed`，`ok: true`。
- 环境：改版 Photoshop 2025 `26.8.1`，UXP 8.3，插件 `0.2.1`。
- 绑定：PSD 路径 `2` -> UXP 下标 `1` -> 图层 ID `257` -> `back_main` -> `smartObject`；审计名匹配。
- 图层安全：顶层图层数保持 `4 -> 4`；隐藏的原智能对象仍隐藏，固定组和像素层仍存在。
- 几何：替换后的最终 bounds 为 `[-22, 0, 813, 1252]`，完整覆盖 `[0, 0, 790, 1252]` 注册框。
- 时间：Photoshop modal `2503 ms`；PSD 保存 `231 ms`；JPG 保存 `86 ms`。总计 `27274 ms` 包含三次人工文件选择，不属于自动批量吞吐样本。
- 输出：JPG、可编辑 PSD 和 evidence JSON 均生成；输入 PSD 副本通过 `closeWithoutSaving()` 关闭，evidence 记录 `source_template_saved: false`。

### JPG 检查

- 输出为 JPEG、`790x1252`、8 位 RGB。
- 对应人工参考 `需求样本/第一组/人工参考结果/e30b22aa4c41f92338f65b1830a7a10c.jpg`。
- 主体、正反方向、位置、文字、装饰和页面边界肉眼一致；无白边、旧商品残留或固定内容误改。
- 两次 JPEG 编码导致文件字节不同；像素平均绝对通道差 `2.44/255`，RMSE `4.30`，不构成肉眼排版差异。
- 输出 JPG 没有嵌入 ICC profile。虽然当前视觉正确，仍不满足 PRD 16.2 的明确 sRGB 输出要求，TV-07 保持进行中。

### PSD 检查

- 收到的输出 PSD 为 `31,030,254` 字节，可由 `psd-tools` 重新解析为 `790x1252`、8 位 RGB、4 个顶层图层。
- `back_main` 仍为可见 `SmartObjectLayer`，内嵌文件名为 `7Y9A3760.jpg`，payload `13,326,074` 字节；payload SHA-256 与批准源图 `047204...BDB` 完全一致，不是扁平死图或重编码替代物。
- 隐藏智能对象 `I36A3058` 仍保持隐藏，目标智能对象、像素基底和固定装饰组均存在。
- 通过 PSD 缓存重新合成的画面与配对 JPG一致，仅存在正常 JPEG 编码差异。
- 已在 Photoshop 中重新打开输出 PSD，双击 `back_main` 后成功进入名为 `7Y9A3760.jpg` 的智能对象内容；随后未保存关闭，避免改变本轮金标输出。
- 该检查当时只证明智能对象内容可进入；后续 `0.2.3.1` 已补齐智能对象替换、普通文字编辑、保存关闭重开和逐层回传深检，TV-08 已通过。

### 运行后输入与输出复核

- 在远程电脑使用修正版 `tools/verify_uxp_single_frame_fixture.ps1 -RequireOutputs` 复核，最终返回 `passed: true`、`outputs_required: true`。
- PSD 模板副本运行后 SHA-256：`B94E7458A33F645E322F30EFDE675CCB268BE5A72CA2FF4B7618F241EED14A35`，与批准值一致。
- 源 JPG 运行后 SHA-256：`04720404A97FBE6DFD6EB138946C129545F37020F81346D98AA0616668C0B0DB`，与批准值一致。
- 输出 PSD SHA-256：`DB9E7C6B8724CC236EC863A0EFDC825D2F4AAC1E8DB1F45360CA662FE6B596B3`。
- 输出 JPG SHA-256：`EB32016E94C7F3282F35CEBCAD143AF75D08C4DF03551E2E6D600264E2961017`。
- evidence JSON SHA-256：`8E813D3E6A3C9B1B16D159B5249A26533A8030CFE6F6F1B382C402CEEFFBCE34`。
- 首次复核暴露 Windows PowerShell 5.1 会按系统编码读取无 BOM UTF-8 evidence JSON；验收脚本现已对 RenderPlan 和 evidence 显式使用 UTF-8。该问题仅影响验收解析，不影响 Photoshop 渲染或已有输出。

## `0.2.2` 显式 sRGB 尝试与实机失败

- 依据 Adobe 官方 UXP 2022 文档，`Document.convertProfile` 从 Photoshop 23.0 起可用，`constants.Intent.RELATIVECOLORIMETRIC` 从 22.5 起可用，覆盖本项目 Photoshop 23.3+ 基线。
- RenderPlan 明确声明 `sRGB IEC61966-2.1`、`relative_colorimetric`、黑点补偿、抖动和嵌入配置文件，不再使用含糊的 `preserve_and_embed`。
- 执行顺序固定为：完成替换与排版 -> 保存可编辑 PSD 副本 -> 对未保存工作文档转换为 sRGB -> 导出 JPG，并请求 Photoshop 嵌入 profile -> 不保存关闭输入副本。
- evidence 记录的是目标配置文件、转换参数、嵌入请求和转换耗时；远程验收脚本同时解析 JPG 段，必须检出 `ICC_PROFILE` APP2，不能只依据 evidence 自报。
- 完整决策见 `docs/架构决策/ADR-004-JPG-sRGB导出策略.md`。

远程实机结果：

- 环境：改版 Photoshop 2025 `26.8.1`，插件和 fixture 均为 `0.2.2`。
- Photoshop 已生成 JPG、PSD 和 evidence；`verify_fixture.ps1 -RequireOutputs` 已通过输入哈希、输出存在性、evidence 版本、绑定和转换请求参数检查。
- 验收在实际 JPEG 文件检查处失败，错误为 `Generated JPG has no ICC_PROFILE APP2 segment.`。
- 这证明 `embedColorProfile: true` 在该具体宿主上没有把 ICC 写入 JPG；evidence 中原来的 `jpg_profile_embedded: true` 只是请求回显，字段语义不足。
- TV-07 保持进行中；`0.2.2` 产物保留为失败证据，不删除、不覆盖，也不再次执行。

## `0.2.3` 本地候选实现

- 保留 `Document.convertProfile`，并在导出前读回 Photoshop 文档颜色配置状态，确认已分配 sRGB；转换失败时不得只给 JPG 打标签。
- Photoshop 导出并关闭输入副本后，适配器以二进制读取 JPG。无 ICC 时补写固定 sRGB APP2；已有完整有效 sRGB ICC 时保留；损坏或非 sRGB ICC 明确失败。
- 补写不重新编码 JPEG 扫描数据，不生成或修改商品像素；写回后再次读取并验证 ICC 完整性与身份。
- evidence 升级为 `uxp-validation-evidence/v2`，区分 `embedding_requested`、`photoshop_embedded_before_postprocess`、`adapter_injected` 和 `embedding_verified`，并记录 profile 身份和后处理耗时。
- 固定 profile 为 `588` 字节，SHA-256 为 `14FB97CEB79AB10B4A72C839A16A334CAC46E7991C6A7F46DE4E56136675920D`；生成来源与许可见 `tools/generate_srgb_icc_profile.py` 和 `uxp-validation-plugin/THIRD_PARTY_NOTICES.md`。
- 主开发机语法、单元测试、固定 profile 复现、PowerShell 5.1 verifier、静态预检和合成输出闭环均已通过；合成输出为 `adapter_injected: true`，最终 ICC 为 `588` 字节且 SHA-256 与批准值一致。最终打包和 ZIP 重解压复验也已通过。

## `0.2.3` 远程实机与视觉通过

2026-08-24，改版 Photoshop 2025 `26.8.1` 执行 `0.2.3` 后，远程 verifier 返回 `passed: true`：

- 输入 PSD SHA-256：`B94E7458A33F645E322F30EFDE675CCB268BE5A72CA2FF4B7618F241EED14A35`；
- 输入源 JPG SHA-256：`04720404A97FBE6DFD6EB138946C129545F37020F81346D98AA0616668C0B0DB`；
- 输出 PSD SHA-256：`DD997E7F47B8792744C4EBF57261A5109B50D195BEDFCC8B89FE836DCE7F6949`；
- 输出 JPG SHA-256：`D18AD1D8389289E06D9F0A66E544DA15D85F181F9D49F2229BA410A9245180A4`；
- evidence SHA-256：`358EE8A940A6217BB7A8EF815D2246EEAC3BC3EEABE8E2151E3674E9603CFB0F`；
- profile：`sRGB IEC61966-2.1`；
- `photoshop_embedded_before_postprocess: true`、`adapter_injected: false`、`embedding_verified: true`；
- 实际 ICC：`3144` 字节，SHA-256 `2B3AA1645779A9E634744FAF9B01E9102B0C9B88FD6DECED7934DF86B949AF7E`。

本次由 Photoshop 原生嵌入有效 sRGB ICC，适配器没有补写，并正确保留了这个非 canonical 的原生 profile。用户随后回传 JPG、PSD、evidence 和视觉截图；主开发机重算三项 SHA-256，结果与远程 verifier 完全一致。用户明确确认“肉眼没问题”，最终 JPG 复核未见白边、旧商品残留、错向、商品裁断或固定文字/装饰异常。技术校验、输入安全和视觉验收全部通过，TV-07 为通过。正常成功路径不构成失败隔离证据，TV-13 继续进行中。

## 本地验证证据

| 日期 | 命令或检查 | 结果 |
| --- | --- | --- |
| 2026-08-23 | bundled Node `--check uxp-validation-plugin/main.js` | PASS |
| 2026-08-23 | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/validate_uxp_prototype.ps1` | PASS：Manifest `0.2.0`、两页协议样例 2 页/5 框、单框计划 1 页/1 框和安全执行标记通过 |
| 2026-08-23 | `git diff --check` | PASS |
| 2026-08-23 | 创建样本包前运行 `tools/verify_uxp_single_frame_fixture.ps1` | PASS：PSD 副本与背面 JPG 哈希匹配批准样本 |
| 2026-08-23 | 从两个 ZIP 重新解压后复验 | PASS：插件 Manifest 版本为 `0.2.0`；样本包输入哈希不变 |
| 2026-08-23 | bundled Node `--check uxp-validation-plugin/main.js`、`tools/validate_uxp_prototype.ps1`、`git diff --check` | PASS：`0.2.1` 语法、Manifest、显式路径顺序、两页/单框计划和安全执行标记通过 |
| 2026-08-23 | 打包 `0.2.1` 并从两个 ZIP 重新解压复验 | PASS：插件 Manifest `0.2.1`；插件包 9 个预期文件；fixture 包 6 个预期文件；PSD/JPG 输入哈希不变 |
| 2026-08-23 | 修正验收脚本显式 UTF-8 读取；PowerShell 语法解析及回传 evidence 解析 | PASS：兼容 Windows PowerShell 5.1 的中文路径 JSON |
| 2026-08-23 | 远程执行 `tools/verify_uxp_single_frame_fixture.ps1 -RequireOutputs` | PASS：`passed: true`；输入 PSD/JPG 哈希不变；PSD/JPG/evidence 三项输出哈希已回收 |
| 2026-08-23 | `0.2.2` Node 语法、PowerShell 语法、Manifest/RenderPlan 静态预检 | PASS：版本、显式 sRGB 契约、`Document.convertProfile` 和安全执行标记通过 |
| 2026-08-23 | 最小 JPEG 有/无 ICC 正反样本 | PASS：APP2 `ICC_PROFILE` 检测器无误报和漏报 |
| 2026-08-23 | 打包 `0.2.2` 并从两个 ZIP 重新解压复验 | PASS：插件 9 个文件、fixture 6 个文件；Manifest `0.2.2`、sRGB RenderPlan 和输入 PSD/JPG 哈希通过 |
| 2026-08-23 | 远程运行 `0.2.2` 后执行 `verify_fixture.ps1 -RequireOutputs` | FAIL：前置输入、输出和 evidence 检查通过；实际 JPG 没有 `ICC_PROFILE` APP2，TV-07 未通过 |
| 2026-08-23 | `0.2.3` Node 语法、JPEG ICC 单元测试和固定 profile 复现 | PASS：`main.js`/`jpeg-icc.js` 语法通过；Pillow `12.3.0` + LittleCMS `2.19` 生成 `588` 字节 profile，SHA-256 `14FB97CE...5920D` |
| 2026-08-23 | `0.2.3` PowerShell 5.1 verifier、静态预检和合成输出闭环 | PASS：parser 与静态契约通过；`-RequireOutputs` 返回成功，`adapter_injected: true`，ICC 字节数和 SHA-256 与批准 profile 一致 |
| 2026-08-23 | `0.2.3` 最终打包和 ZIP 重解压复验 | PASS：Manifest `0.2.3`；插件 12 个文件、fixture 6 个文件；插件不含 PSD/JPG/PNG，包含 `jpeg-icc.js`、NOTICE 和生成脚本；fixture 输入哈希与包内 PowerShell int-cast 修复通过 |
| 2026-08-24 | 改版 PS2025 `26.8.1` 远程执行 `0.2.3` | PASS（技术校验）：verifier `passed: true`；输入哈希不变；Photoshop 原生嵌入 `3144` 字节 sRGB ICC，适配器未补写，最终 embedding 验证通过 |
| 2026-08-24 | 主开发机复核用户回传 JPG/PSD/evidence 和截图 | PASS（TV-07）：三项输出 SHA-256 与远程 verifier 一致；用户确认肉眼无问题；最终 JPG 无白边、旧商品残留、错向或固定文字/装饰异常 |
| 2026-08-24 | TV-08 样本结构审计与独立远程包闭环 | PASS（仅准备）：金标 PSD 含 `back_main` 与 16 个真实文字层；Windows PowerShell 5.1 前置检查、ZIP 重解压、working 生成、未编辑副本拒绝和后验完整路径通过；主机深检器完成基线解析并拒绝未编辑负例；仍等待 Photoshop 人工关闭重开 |
| 2026-08-24 | TV-08 首次远程启动与修正版包 | SAFE FAIL 后已修复（本地）：旧 BAT 将尾随反斜杠目录与 `-Prepare` 拼接，失败在任何复制前；`0.2.3.1` 改用脚本目录回退、系统 Windows PowerShell 绝对路径和内置 .NET SHA-256，已在中文目录用真实 BAT 完成准备并正确拒绝未编辑副本 |
| 2026-08-24 | TV-08 改版 PS2025 人工编辑与主机回传深检 | PASS（TV-08）：人工替换 `back_main`、编辑既有文字、保存关闭重开和视觉确认完成；远程自动校验通过。主机解析回传 PSD，确认目标 ID/类型和正面 payload 准确，40 个非目标图层不变、36 个可解码像素层一致；working PSD SHA-256 `6B221521...BBBC8` |

输入哈希：

- `G-详情-03-validation-copy.psd`：`B94E7458A33F645E322F30EFDE675CCB268BE5A72CA2FF4B7618F241EED14A35`
- `7Y9A3760.jpg`：`04720404A97FBE6DFD6EB138946C129545F37020F81346D98AA0616668C0B0DB`

远程交接包：

- `uxp-validation-probe-0.2.1.zip`：SHA-256 `6222B336E94AFE1A0EC884727C2589CFAF77A5EBB5A2FCCF0241F3D5F825A3AA`；不含用户 PSD/JPG。
- `uxp-validation-g03-fixture-0.2.1.zip`：SHA-256 `926E423624C00CF6B18C263AB1BF6CF68C5B1659A3A16176DCDE1984DB3BCB36`；只含本轮明确 PSD 副本、背面图、更新后的单框计划、校验脚本和输出目录说明。
- `uxp-validation-probe-0.2.2.zip`：SHA-256 `20958E4710159F75C4A14481E8D2978D66EE0C3217C96B9954E46DFA46D7928E`；包含显式 sRGB 实现和更新后的验收工具，不含用户 PSD/JPG。
- `uxp-validation-g03-fixture-0.2.2.zip`：SHA-256 `7D016C96C0DF63A7A25491A9B54682E5E87AA7CC9A57F1ACA58A75222BE16F2E`；包含批准样本副本、新 RenderPlan、验收脚本和空输出目录说明。
- `uxp-validation-probe-0.2.3.zip`：SHA-256 `A76883E842D5DBA02309755BD44D8109B8C09657231ACE04B362A884E20E9AA3`；最终重解压复验通过，不含 PSD/JPG/PNG；失败清理 evidence 按重新枚举后的实际残留结果报告。
- `uxp-validation-g03-fixture-0.2.3.zip`：SHA-256 `204DAFC66594500EBCFAF16EB2B6C867D000FE86A282419CB7D37B3F575F12F7`；最终重解压复验通过，fixture 输入哈希不变。
- `uxp-validation-tv08-editability-0.2.3.zip`：SHA-256 `D31457A9A9E87B73C1907F5E3F123C5319739F48603CFC32141557B2829954A5`；首次远程启动暴露 BAT 尾随反斜杠参数问题，失败发生在复制前，保留为失败证据，不再重试。
- `uxp-validation-tv08-editability-0.2.3.1.zip`：SHA-256 `B89F40E94C7272D363A6F49803525BCE7AB5A1DAE5A582701843478A4285E377`；包含相同批准输入和修正后的 working 生成器、后验校验器与人工操作卡；已在中文目录通过真实 BAT 准备入口和未编辑负例。
- 历史 `0.2.0` 包保留为失败证据，不得再次用于执行。

两个 ZIP、PSD、JPG 和将来的输出均由 `.gitignore` 排除，不进入仓库提交。

## 尚未证明

- 四框复杂页面的剪贴、蒙版、多个智能对象和固定图层是否保持正确。
- 一个真实款式的整组页面是否能够连续正确生成。
- 一次会产生部分输出的受控失败是否能够定位、清理且不阻断后续任务。
- 使用预生成 `RenderPlan` 的 10 款约 70 张纯 JPG 渲染通道绝对耗时，以及完整产品从 AI 分析开始的最终机器总时间。

## 下一动作

四框执行器和受控样本包已在阶段 3 完成本地准备。下一步使用 `uxp-validation-tv06-multi-frame-0.3.0.zip` 在改版 PS2025 实机核对剪贴、蒙版、层级、固定内容和视觉结果；详见 `docs/验证/UXP技术可行性验证报告-阶段3-四框执行准备.md`。后续顺序为整款页面、一次代表性受控失败隔离、10 款约 70 张纯 JPG 绝对批量性能。
