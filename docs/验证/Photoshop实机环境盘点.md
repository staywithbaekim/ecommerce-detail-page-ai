# Photoshop 实机环境盘点

本记录只在用户通过 UU 远程协助或明确确认后填写。首次连接只读，不安装、升级或修改 Photoshop。若 Photoshop 精确版本低于 23.3，记录差距后停止 UXP 安装验证。

## 机器信息

| 项目 | 实测值 | 证据方式 |
| --- | --- | --- |
| 记录时间（Asia/Shanghai） | 2026-08-23（系统信息截图） | 用户提供的 Photoshop 系统信息 |
| Windows 版本与构建号 | Windows 11 64 位，10.0.26200.9168 | Photoshop 系统信息 |
| CPU | AMD，8 个物理处理器 / 16 个逻辑处理器，3.8 GHz；具体型号未列出 | Photoshop 系统信息 |
| 内存 | 31,893 MB；Photoshop 可用 24,490 MB | Photoshop 系统信息 |
| GPU 与驱动 | NVIDIA GeForce RTX 4060 Ti，约 7,949 MB GPU RAM；GPU/OpenCL 可用 | Photoshop 系统信息 |
| 系统盘/样本盘/输出盘类型 | 未确认；应用与暂存盘位于 G: / C:，磁盘介质待确认 | Photoshop 系统信息与远程检查 |
| Photoshop 暂存盘 | 启动盘，500.0 GB，总可用约 305.1 GB；异步 I/O 已启用 | Photoshop 系统信息 |
| Photoshop 精确版本 | Photoshop 2025 `26.8.1 20250624.r.8 5e0d05d x64` | 帮助 > 系统信息 |
| UXP 运行时/Developer Tool 版本 | Photoshop 内含 UXP `uxp-8.3.0-uxp-b1d8c85`；UXP Developer Tools 已安装并完成账号/邮箱验证，可正常加载和调试插件 | Photoshop 系统信息与 UDT 截图 |
| 外部 Demo 参考标识 | 按需记录 | 仅用于追溯既有 Photoshop 操作，不作为正式性能基准 |

### 环境解释

该机器是 Photoshop 2025 的改版安装，作为探索性兼容环境记录。`26.8.1` 高于当前 UXP Manifest v5 探针的 `23.3.0` 最低门槛，且系统信息显示 UXP 运行时已加载；这只能证明宿主具备 UXP 组件，不能证明本项目插件可加载、权限可用或智能对象操作可执行。该结果不改变正式支持策略，也不能代表正版或其他改版 Photoshop。

## 操作确认

| 项目 | 状态 | 备注 |
| --- | --- | --- |
| 用户已确认可以加载验证插件 | 已加载并完成 probe | UXP Developer Tools 显示 `local.ecommerce.uxp-validation` 为 Loaded；面板显示 `Probe recorded` 且返回 `ok: true`，证明插件已进入宿主并成功执行受控 Photoshop API 调用 |
| Photoshop 开发人员模式 | 已发现并完成重启 | 用户截图显示复选框存在；后续 UXP Developer Tools 已能启动到账号检查页 |
| 实验模板是源 PSD 的明确副本 | 已验证 | fixture 固定为 `G-详情-03-validation-copy.psd`；`0.2.1` 运行后 SHA-256 保持 `B94E...A35`，evidence 记录 `source_template_saved: false` |
| 实验源图是源素材的明确副本或只读引用 | 已验证 | fixture 固定为 `7Y9A3760.jpg`；`0.2.1` 运行后 SHA-256 保持 `0472...BDB` |
| 临时与正式输出目录分离 | 已选择 fixture 独立 `output/` | `0.2.0` 换图前失败已证明可定位且未改写输入；一次代表性部分输出清理验证仍待完成 |
| 原始 PSD/素材运行前 SHA-256 已记录 | 已完成前后复核 | PSD `B94E...A35`；JPG `0472...BDB`；`0.2.1` 验收脚本返回输入哈希不变，`0.2.2` 失败前也通过同一输入哈希检查 |

## 首次实机顺序

1. 记录上述环境，不安装或升级 Photoshop。
2. 在 Photoshop 首选项中确认是否存在并启用开发者模式；若没有，记录为环境差异。
3. 通过 Adobe 官方 Creative Cloud 安装 UXP Developer Tool；若必须登录而无法登录，停止并记录，不使用随机第三方安装包。
4. 将仓库的 `uxp-validation-plugin` 整个目录复制到远程电脑，用 Developer Tool 加载。
5. 回到 Photoshop，确认 `UXP Validation` 面板出现，只点击 `Record Photoshop probe`。
6. 如需验证桌面握手，在远程电脑启动 `tools/uxp_validation_bridge.ps1` 后点击 `Handshake with desktop bridge`。
7. 保存插件输出和截图；在用户再次确认副本路径后，才进入图片框读取和渲染动作实现。

## 实测结果

已完成环境盘点。UXP Developer Tools 已安装并可用；清洁解压并加载 `0.1.1` 诊断版后，插件状态为 Loaded，有活动文档的只读 probe 返回 `Probe recorded`、`ok: true`。同机启动 PowerShell bridge 后，插件面板返回 `Bridge connected`，bridge 输出 `POST /handshake -> 200 OK` 和 `stopped after 1 accepted handshake(s)`；握手请求中记录插件版本 `0.1.1`。这证明该具体改版 PS2025 能加载 UXP、执行基础 Photoshop API 并访问同机桌面进程。此前 `Permission denied / Manifest entry not found` 已由清洁加载临时 `network.domains: "all"` 的诊断版排除；正式权限白名单仍需收敛复测。probe 输出的 Photoshop 版本字段为空，精确版本以系统信息 `26.8.1` 为准；后续智能对象操作、JPG/PSD 基础导出、完整 sRGB ICC 文件验证和 `0.2.3` JPG 肉眼复核均已通过。

随后远程面板成功读取旧的两页 RenderPlan 协议样例，显示 `ok: true`、`job_id: uxp-validation-sample-001`、`pages: 2`。这只证明 JSON 文件权限、协议解析和面板状态正常；旧版 `Execute gated plan` 未执行换图。

远程执行 `0.2.0` 单框副本测试后，面板显示 `Single-frame test stopped safely`，错误为 `Calibrated layer path 2 is not a smart object`，并返回 `partial_outputs_removed: true`、`source_template_saved: false`。该结果不是换图成功；它证明变更前类型检查和失败清理路径被触发。原因是 PSD 审计路径按底部到顶部编号，而 UXP DOM 图层集合按顶部到底部暴露，`0.2.0` 错把审计下标直接当作 UXP 下标。`0.2.1` 已按 ADR-003 修复并在同机完成后续成功复测。

`0.2.1` 随后在同一环境实机通过：面板显示 `Single-frame copy test completed`、`ok: true`；路径 `2` 正确转换到 UXP 下标 `1` 并命中 `back_main` 智能对象，图层数保持 `4 -> 4`，JPG/PSD/evidence 均生成。Photoshop modal 耗时 `2503 ms`，其中 PSD 保存 `231 ms`、JPG 保存 `86 ms`。收到的 JPG 与人工参考肉眼一致；收到的 PSD 可重新解析且 `back_main` 保留内嵌 `7Y9A3760.jpg` 智能对象。运行后 PSD 副本和源 JPG 哈希均保持不变；JPG 未嵌入 ICC profile，Photoshop 人工二次编辑仍待确认。

`0.2.2` 随后在同一环境执行显式 sRGB 复测。Photoshop 已生成 JPG、PSD 和 evidence，远程验收脚本也通过了输入哈希、输出存在性、evidence 版本、绑定和转换请求参数检查，但在读取实际 JPEG 时返回 `Generated JPG has no ICC_PROFILE APP2 segment.`。这表明该具体改版构建没有落实 `saveAs.jpg` 的 profile 嵌入请求；不能依据面板完成状态或 evidence 请求回显判定 ICC 已存在。

`0.2.3` 已在主开发机完成候选实现，并在同一改版 PS2025 `26.8.1` 远程执行。verifier 返回 `passed: true`，输入 PSD/JPG 哈希保持 `B94E7458...4A35` / `04720404...BDB`；输出 JPG 由 Photoshop 原生嵌入 `sRGB IEC61966-2.1`，`photoshop_embedded_before_postprocess: true`、`adapter_injected: false`、`embedding_verified: true`，实际 ICC 为 `3144` 字节，SHA-256 为 `2B3AA164...AF7E`。Node 语法、JPEG ICC 单元测试、Pillow `12.3.0`/LittleCMS `2.19` 固定 profile 复现、PowerShell 5.1 verifier、静态预检、合成输出闭环、最终打包和 ZIP 重解压复验也均已通过；最终插件 ZIP SHA-256 为 `A76883E842D5DBA02309755BD44D8109B8C09657231ACE04B362A884E20E9AA3`，fixture ZIP SHA-256 为 `204DAFC66594500EBCFAF16EB2B6C867D000FE86A282419CB7D37B3F575F12F7`。用户回传的 JPG/PSD/evidence 哈希与 verifier 完全一致，并确认最终画面肉眼无异常，TV-07 已通过。

后续同机 TV-11 `0.4.6` 已完成整款六页和 11 个图片框，TV-13 `0.4.7` 已在两页 JPG 生成后受控失败，删除两张部分输出并在同一插件会话继续完成正常六页。最终 verifier 返回 `passed: true`、`continued_run_passed: true`，失败目录零残留，evidence SHA-256 为 `11FB9555A0B273561794568771E510418EF0413D6291998735FA6C9690127F1D`；TV-12 与 TV-13 均已通过。
