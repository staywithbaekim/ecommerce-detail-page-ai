# UXP 技术可行性验证计划

## 目标与边界

本轮只验证 Photoshop 执行通道的最小纵向能力，不建设完整桌面应用。UXP 插件只接收结构化 `RenderPlan`，不承载 AI、选图规则、批次队列、本地数据库或自然语言到 Photoshop 指令的转换。所有 Photoshop 操作必须针对模板副本和临时输出目录进行。

通过条件沿用 `docs/任务/UXP技术可行性验证.md`、`docs/架构决策/ADR-001-Photoshop执行通道验证策略.md` 和 `docs/验收矩阵.md`：`TV-01` 至 `TV-08`、`TV-11` 至 `TV-13` 必须有可复现证据；视觉结果不得有旧商品残留或固定图层误改；TV-14 按 ADR-009 以单款实测和保守线性外推关闭 UXP 执行通道门，完整产品 `machine_total` 后验。

## 样本选择

样本审计文件：`需求样本/第一组/模板PSD/output/_inspection/inspection-report.json` 和 `manifest-skeleton.json`。

| 用途 | 模板副本 | 选择理由 | 绑定目标 |
| --- | --- | --- | --- |
| 单图片框纵向冒烟 | `G-详情-03.psd` | 790×1252；一个候选商品位；剪贴结构 | 层路径 `2` |
| 复杂页面纵向验证 | `G-详情-05-细节.psd` | 790×1115；四个候选商品位；多个裁切和层级 | 层路径 `3`、`7`、`11`、`15` |
| 重复模板控制 | `G-详情-01.psd` 与 `G-详情-01-backup.psd` | 审计哈希相同，验证不重复执行 | 不执行替换 |

审计显示存在隐藏智能对象和候选堆叠，因此不能按“所有智能对象都替换”。`RenderPlan` 必须同时携带层路径、显式 `psd_bottom_to_top` 路径顺序、结构证据和源图 SHA-256；执行适配器按 ADR-003 转换自身图层集合下标，不得猜测路径含义。

## 绝对性能计时边界

每次运行记录单调时钟时间戳，分段如下：

1. `prepare`: 打开模板副本、确认输出目录和清理上一次临时文件；不计 AI 和桌面端扫描。
2. `photoshop_cold_start`: Photoshop 未运行时，从启动到插件握手完成。
3. `photoshop_render`: 从提交页面级计划到 Photoshop 完成 JPG 初次写入；包含置入、裁切、变换、图层恢复、颜色转换和导出。
4. `jpg_icc_postprocess`: 从读回 Photoshop JPG 到 ICC 检查、必要补写、落盘和再次读回验证完成。
5. `photoshop_hot_render`: Photoshop 保持打开时重复执行同一计划。
6. `save_editable_psd`: 可选 PSD 保存和文件落盘。
7. `verify`: 尺寸、完整 ICC、输出命名、哈希、视觉和图层结构检查。

当前 UXP 任务已经使用真实整款热运行记录单款通道耗时，并按 ADR-009 采用每款 20 秒的保守上限外推 10 款约 200 秒，关闭阶段性 `render_channel_total` 性能门。后续桌面端批次验收仍记录冷启动、热运行、文件保存、ICC、异常值、机器配置、Photoshop 版本和样本标识；`jpg_icc_postprocess` 不得从完整机器时间中排除。

PRD 的最终 `machine_total` 从开始 AI 分析计时，到全部 JPG 完成文件校验结束，包含扫描、AI、计划生成、Photoshop 渲染、ICC 后处理和必要文件操作。当前任务不建设 AI、桌面批次队列或正式缩略图流程，因此本次 TV-14 只关闭 UXP 执行通道性能门；完整端到端 `machine_total` 在桌面端 MVP 接通后复核。

## 输出核对清单

- 原始 PSD、源照片和人工参考结果运行前后 SHA-256 不变。
- 输出目录只包含预期 JPG，以及勾选时的可编辑 PSD；不接受旧文件冒充成功结果。
- JPG 尺寸、质量和命名符合 `RenderPlan.output`；实际文件包含完整、有效且可识别为 sRGB 的 ICC，不能只依据 `embedColorProfile` 请求或 evidence 自报。
- 输出视觉与人工参考对比：主体、裁切、位置、页面尺寸和固定装饰一致；无错款、旧商品残留或白边。
- 可编辑 PSD 中智能对象、文字、蒙版和必要图层效果仍可人工修改。
- 在一个会产生部分输出的稳定检查点执行一次代表性受控失败，返回 `job_id`、`page_id`、`frame_id`、错误码和清理动作；验证输入哈希不变、JPG/PSD/成功 evidence 不残留在正式输出目录，并确认后续任务仍可执行。

## 当前 sRGB 验证状态

- `0.2.2` 已在改版 Photoshop 2025 `26.8.1` 实机执行，实际 JPG 没有 `ICC_PROFILE` APP2，TV-07 未通过。
- `0.2.3` 的 Node 语法、JPEG ICC 单元测试、固定 profile 复现、PowerShell 5.1 verifier、静态预检和合成输出闭环已通过；合成输出的 ICC 为 `588` 字节，SHA-256 为 `14FB97CEB79AB10B4A72C839A16A334CAC46E7991C6A7F46DE4E56136675920D`。
- `0.2.3` 插件 ZIP SHA-256 为 `A76883E842D5DBA02309755BD44D8109B8C09657231ACE04B362A884E20E9AA3`，fixture ZIP SHA-256 为 `204DAFC66594500EBCFAF16EB2B6C867D000FE86A282419CB7D37B3F575F12F7`；最终重解压复验已通过。
- `0.2.3` 已在改版 PS2025 `26.8.1` 通过远程 verifier：输入哈希不变；Photoshop 原生嵌入 `3144` 字节 sRGB ICC，适配器未补写，最终嵌入验证通过。用户回传产物的哈希与远程结果一致，并确认 JPG 肉眼无异常，TV-07 已通过；TV-13 的一次代表性受控失败隔离尚未完成。

## 当前可编辑 PSD 验证状态

- 已对 `0.2.3` 金标输出 PSD 的明确副本完成只读结构审计：`back_main` 是内嵌智能对象，PSD 含 16 个真实文字层，现有样本足以完成 TV-08。
- TV-08 采用 Photoshop 人工二次编辑，不新增 UXP 自动编辑按钮。自动再执行一次 UXP 不能证明美工日常可编辑性。
- 独立包 `uxp-validation-tv08-editability-0.2.3.zip` 首次远程启动在复制前暴露 BAT 尾随反斜杠参数问题，输入未修改，该包不再重试。
- 修正版 `uxp-validation-tv08-editability-0.2.3.1.zip` 已通过 ZIP 重解压、脚本预检、中文目录下真实 BAT 工作副本生成和未编辑副本拒绝，SHA-256 为 `B89F40E94C7272D363A6F49803525BCE7AB5A1DAE5A582701843478A4285E377`。
- 改版 PS2025 已完成人工替换、文字编辑、保存关闭重开和视觉确认；远程自动校验通过。主开发机回传深检确认路径 `2` 和 `3/14/6` 的智能对象 payload、文字内容、图层类型与 ID 正确，40 个非目标图层不变，36 个可解码像素层一致。
- TV-08 已通过；working PSD SHA-256 为 `6B2215210F2FEBA224F0263E3EB640DA4A7739D5CED6BAEE91B95EF6229BBBC8`。

## 后续产品验收

1. 在 V1 桌面端接通扫描、AI、计划生成和批次队列后复核完整 `machine_total`。
2. 使用真实多款批次记录冷启动、热运行、ICC 和文件操作分段，出现超过 5 分钟或非线性退化时重新打开 TV-14。

## 实机前置条件

当前探索性实机是另一台电脑上的改版 Photoshop 2025 `26.8.1`。实机动作需要用户通过 UU 远程完成或明确协助。环境盘点记录 Windows 版本、CPU、内存、GPU、磁盘类型、暂存盘、Photoshop 精确版本、UXP Developer Tool 版本和插件安装路径。正式候选下限仍是 Photoshop `23.3+`；若待测环境低于 23.3，记录差距并停止安装，不自行升级。

当前本地可复现命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/validate_uxp_prototype.ps1
```

该命令只解析 Manifest 和示例 `RenderPlan`，不读取或写入样本 PSD/JPG。

桌面端握手对端使用 Windows 自带 PowerShell，仅监听 loopback，并在接受一个有效握手后退出：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/uxp_validation_bridge.ps1
```

该 bridge 只验证协议、插件 ID 和连接路径，不接收任意命令，也不替代正式桌面应用。
