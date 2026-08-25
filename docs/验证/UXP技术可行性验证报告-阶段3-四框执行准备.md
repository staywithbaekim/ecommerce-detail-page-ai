# UXP 技术可行性验证报告：阶段 3 四框执行准备

## 结论

UXP 四框执行器已从 `0.3.0` 迭代到 `0.3.5`，并在改版 Photoshop 2025 `26.8.1` 完成真实四框智能对象替换、结构审计和 PSD/JPG/evidence 输出。回传 PSD 独立深检和 JPG 人工视觉检查均已通过。

TV-06 已通过。插件执行、原始输出 verifier、回传 PSD 独立深检、JPG 视觉验收和 Photoshop 重开截图均已完成。

## 验证范围

- 模板副本：`G-详情-05-validation-copy.psd`，`790x1115`，8 位 RGB。
- 模板 SHA-256：`69BF44A64E47CB8D6817FA8589888163B14AB08A19A0BCFCEE02352C3392F5EF`。
- 只验证四个已校准的可见剪贴智能对象，不把其他智能对象当作商品位。
- 只使用用户提供的四张批准素材，不生成、补全或重绘商品图片。
- 输出一次可编辑 PSD、一次明确 sRGB JPG 和一个结构化 evidence。
- 不验证整款、多页队列、AI、尺码表、受控失败或批量性能。

## 四框绑定

| 目标路径 | 窗口基底 | 旧栈路径 | 审计名 | 批准素材 | 语义 |
| --- | --- | --- | --- | --- | --- |
| `3` | `1` | `2` | `care_label` | `7Y9A3762.jpg` | 水洗标/吊牌细节 |
| `7` | `5` | `6` | `collar` | `7Y9A3751.jpg` | 领口细节 |
| `11` | `9` | `10` | `\tsleeve` | `7Y9A3755.jpg` | 袖口细节 |
| `15` | `13` | `14` | `\tfront` | `7Y9A3750.jpg` | 完整正面商品 |

图层名只用于绑定后的交叉审计。正式定位依据为显式 `psd_bottom_to_top` 图层路径、智能对象类型、可见性、剪贴结构、窗口基底、旧替换栈和注册框。

四张源图与人工参考逐格复核后，中心焦点和最小 cover 能保留参考结果中的正面主体、袖口、领口和水洗标关键内容；最终仍以远程输出与人工参考肉眼对照为准。

## 执行器变化

- 保留已验证的 `single_frame_copy`，新增严格门控的 `multi_frame_copy`。
- 四个目标在第一张图替换前全部预检；任一目标路径、类型、可见性、剪贴、窗口、旧栈或唯一 ID 异常时安全中止。
- 四个替换位使用各自源文件 session token，依次执行智能对象内容替换和确定性 cover 几何。
- 递归记录执行前后结构快照；目标智能对象只允许几何变化，所有图层的身份、名称、类型、可见性、剪贴、蒙版标志、透明度和混合模式必须保持，非目标图层 bounds 也必须保持。
- 结构审计通过后只保存一次 PSD，再沿用 TV-07 已通过的 sRGB 转换、JPG 导出、ICC 读回/必要时补写和最终复核路径。
- 任一步失败时关闭未保存工作文档并清理本轮 PSD、JPG 和 evidence 输出；输入模板副本和源图不保存。

## 本地验证

| 检查 | 结果 |
| --- | --- |
| bundled Node `--check`：`main.js`、`jpeg-icc.js` | PASS |
| `tools/test_uxp_render_plan.js` | PASS：单框/四框正例、生成图禁用、缺框、错路径、错剪贴、错素材、路径顺序和结构变化负例 |
| `tools/test_jpeg_icc.js` | PASS |
| Windows PowerShell 5.1 `tools/validate_uxp_prototype.ps1` | PASS：Manifest `0.3.0`，协议样例 2 页/5 框，单框计划和四框计划 |
| `tools/tv06/verify_fixture.ps1` | PASS：模板副本和四张素材哈希、四框绑定、窗口/旧栈、输出策略；Windows PowerShell 5.1 的显式目录和脚本目录回退两条调用均通过 |
| `tools/package_uxp_tv06.ps1` | PASS：生成单 ZIP，并从 ZIP 重解压后分别通过显式目录和无参数 fixture 校验 |
| `git diff --check` | PASS |

本地电脑没有 Photoshop，因此上述检查不声称真实 UXP API 或视觉结果已通过。

## 远程包

- 修正版文件：`uxp-validation-tv06-multi-frame-0.3.0.1.zip`
- SHA-256：`1193BF69A7A7AC2BC01866D6B190A854F65F2867B66CBA4583FBEBDE151205F3`
- 大小：`126292349` 字节。
- 包含：插件 `0.3.0`、模板明确副本、四张批准源图、人工参考副本、四框 RenderPlan、输入/输出校验脚本和操作卡。
- 插件目录自身不包含 PSD、JPG、PNG 或客户凭据；用户样本仅位于受控 fixture 目录。

首次远程双击 `0.3.0` 包的输入检查 BAT 时，Windows PowerShell 5.1 在参数绑定阶段未建立 `$PSScriptRoot`，导致 `FixtureRoot` 为空并在任何 Photoshop 操作前安全失败。显式传入 fixture 目录后远程输入检查返回 `passed: true`，模板副本和四张源图哈希均与批准值一致。修正版把脚本目录回退移到参数绑定之后，两个 BAT 同时显式传入 `%SCRIPT_DIR%.`，并在打包和 ZIP 回环中新增无参数 Windows PowerShell 5.1 回归检查。插件代码、RenderPlan 和受控输入没有变化；当前已传输的 `0.3.0` 包可在显式检查通过后继续本轮实机，后续复现使用 `0.3.0.1`。

`0.3.0` 首次 Photoshop 四框执行进入统一预检后安全中止，面板状态为 `RenderPlan test stopped safely`，错误消息为 `The resolved smart object does not match the calibrated audit name.`。失败发生在第一张素材替换前，工作 PSD 未保存并已关闭。Photoshop `executeAsModal` 把插件自定义的 `BINDING_AUDIT_MISMATCH` 包装为 `UNEXPECTED_ERROR`，导致原本的 frame id、期望名、实际名和路径解析证据没有显示在面板中，因此目前不能判断是路径错误还是宿主名称规范化。

诊断修正版插件 `0.3.1` 在模态回调内部序列化校验错误，并在回到插件边界后恢复原始错误码和详情；没有改四框映射、替换算法、RenderPlan 或受控输入。插件小包 `uxp-validation-tv06-plugin-0.3.1.zip` 为 `24337` 字节，SHA-256 `55908E755B042D587AE01B21287E0A07C3892372DB5FEFEF491DE99BA6C793A1`。Node 语法、错误保真正例、RenderPlan 正反测试、JPEG ICC、Windows PowerShell 5.1 静态预检、无媒体文件检查及 ZIP 重解压复验均通过。TV-06 仍保持“进行中”，等待 `0.3.1` 实机返回具体绑定证据。

`0.3.1` 远程复测再次安全中止。回传面板证据明确显示 `mutated_frame_ids_before_failure: []`、`failed_after_partial_mutation: false`、`partial_outputs_removed: true`、`output_cleanup.confirmed: true`；没有任何 frame 被替换，也没有部分变更残留。错误变为 `The 文档 with an id of undefined does not exist.`，原因是模态回调 `finally` 中的文档关闭异常覆盖了已序列化的原始预检错误。

`0.3.2` 改为在模态外部独立保存原始错误；后续文档关闭或模态边界异常只附加为 `modal_boundary_error`，不能替换最初的错误码、frame、期望名、实际名和路径证据。插件小包 `uxp-validation-tv06-plugin-0.3.2.zip` 为 `24537` 字节，SHA-256 `331D65EFBAAF469C058B5CA2E882702669F28B04512C22EBF8B21974EFBF5167`。四框映射、替换算法、RenderPlan 和 fixture 仍未改变；Node 语法、双错误保真单测、协议、ICC、静态预检、禁带媒体检查和 ZIP 重解压复验通过，等待实机取回原始绑定证据。

`0.3.2` 远程复测成功取回原始证据：`BINDING_AUDIT_MISMATCH` 发生在 `G-详情-05.frame-11`。路径 `11` 按 `psd_bottom_to_top` 正确解析为 UXP index `6`、集合长度 `18`、图层 ID `539`、类型 `smartObject`；校准审计名为带前导 Tab 的 `\tsleeve`，宿主实际返回 `sleeve`。这证明路径和结构命中正确，差异来自 UXP 对图层名首尾空白的规范化。失败仍发生在任何替换前，零个 frame 修改且输出清理确认通过。

根因修正版 `0.3.3` 只允许以下审计名等价：完全相同，或移除首尾空格/Tab 后主体完全相同且非空。名称主体不同仍返回 `BINDING_AUDIT_MISMATCH`；路径、智能对象类型、可见性、剪贴、窗口基底、旧替换栈和目标唯一性仍全部硬校验，名称继续只作交叉审计。匹配模式会写入 frame evidence。小包 `uxp-validation-tv06-plugin-0.3.3.zip` 为 `24838` 字节，SHA-256 `83A176BA9817C656343B041A6B70B96E7C953BA2E37098C2ADF32BA075FD1963`；精确匹配、边界空白等价、主体不等和空白名正反测试，以及原有协议、ICC、静态预检、禁带媒体检查和 ZIP 重解压复验均通过。

`0.3.3` 远程执行越过审计名检查后，主错误与文档关闭错误均为 `The 文档 with an id of undefined does not exist.`。`mutated_frame_ids_before_failure: []` 只说明替换函数尚未成功返回，不能证明 Action Manager 没有开始第一框操作；输出清理仍确认通过，输入副本未保存。该证据表明缓存的 Photoshop Document/Layer DOM 引用可能在 `placedLayerReplaceContents` 后失效，连续多框执行不应长期持有这些引用。

`0.3.4` 在每一框替换前重新按已校准路径解析目标并复核 ID，替换返回后从 `app.documents` 按唯一受控模板副本名重新获取 Document，再解析同一路径、复核 ID 并执行几何调整；关闭时也重新获取打开的受控副本，不调用可能失效的旧对象。失败证据分为 `attempted_frame_ids`、`mutated_frame_ids`（替换调用已返回）和 `completed_frame_ids`（几何已完成），并记录 `execution_stage` 与 `active_frame_id`。小包 `uxp-validation-tv06-plugin-0.3.4.zip` 为 `25509` 字节，SHA-256 `E7EE8A6CFC45B87A970A77DEC58796B522B2ACA610B602A698F7411B94CC4F75`；文档唯一性正反测试、既有协议/审计名/双错误/ICC/静态预检、禁带媒体检查和 ZIP 重解压复验均通过。

`0.3.4` 远程复测在 `open_template_copy` 阶段安全失败：面板显示 `WORKING_DOCUMENT_NOT_UNIQUE`，期望 `G-详情-05-validation-copy.psd`，`match_count: 0`、`open_document_names: []`，四个 frame 均未 attempted，输出清理确认通过；截图中 Photoshop 已出现模板画布。该结果说明宿主在同一 `executeAsModal` 中可能先返回 `app.open()` 文档、但尚未刷新 `app.documents`，不能把它判为 PSD 打不开；后续确认原版 Photoshop 也可能出现同类行为，因此不把它归因于改版环境。

`0.3.5` 以 `app.open()` 返回的 Document 为打开阶段权威；每次替换后优先从 `app.activeDocument` 重新获取受控文档，`app.documents` 只作唯一性校验和后备；失败关闭也保留 open-return 文档作为回退。新增“集合为空但 open 返回有效”“活动文档重新获取”“错误名称”和“重复文档拒绝”回归测试。插件小包 `uxp-validation-tv06-plugin-0.3.5.zip` 为 `25959` 字节，SHA-256 `5EC2C63B59B6C971CA4B9535A878204E1F5C94DEFB976305BF7ADD2179AC4E60`；本地 Node/RenderPlan/ICC/静态预检和 ZIP 回环通过，等待远程复测。

远程清洁重启 Photoshop 并确认没有遗留 `G-详情-05-validation-copy.psd` 后，`0.3.5` 返回 `Multi-frame copy test completed`、`ok: true`，已完成四框执行和 PSD/JPG/evidence 输出。此前 `0.3.5` 的空返回发生在 `0.3.4` 漏关验证副本后再次打开同一路径的条件下，不再作为首次打开失败证据。与当前 evidence 匹配的校验更新包 `uxp-validation-tv06-verifier-0.3.5.zip` 为 `5138` 字节，SHA-256 `76E5A13FB93E696606E5AFEF32F5C1DB80A504C23CE44609FAF7E189B07B0D2F`，只含校验脚本和说明；等待远程自动校验、JPG 人工参考对照和输出 PSD 重开证据。

## `0.3.5` 回传产物检查

- 回传 evidence SHA-256 为 `FD9CF79750B1EC1B5FC407A282ECA93B292BC38D2BC281DB6EFB9720ED4045E3`，输出 PSD SHA-256 为 `193CABA71A7603F0D313D149E8966EE071C8D62BB405D21371E1D502063DEE6E`。
- evidence 记录插件 `0.3.5` 在 `790x1115` 画布完成四个 frame；四个目标均进入 attempted、mutated 和 completed，执行前后均为 46 层，结构审计通过且输入模板未保存。Photoshop modal 为 `12756 ms`，包含色彩转换和文件输出的总耗时为 `33103 ms`。
- `tools/tv06/verify_returned_psd.py` 对回传 PSD 的独立深检返回 `passed: true`：PSD 可重新解析，保持 8 位 RGB、46 个递归图层和 15 个文字层；路径 `3/7/11/15` 的四个目标仍为可编辑智能对象，内嵌 payload 哈希分别匹配 `7Y9A3762/3751/3755/3750.jpg`；42 个非目标图层不变，37 个可解码像素层一致。结构化结果见 `docs/验证/TV06回传PSD深检结果-0.3.5.json`。
- 用户确认最终 JPG “肉眼没问题”；主开发机对照人工参考复核后，同样确认四个图片位的内容、裁切、圆角、文字和固定装饰正常，无白边、黑块、旧商品或关键细节错位。
- 微信回传 JPG SHA-256 为 `F49234A0656238EBD909B57CB0A4261766848CDA655E62F5900B7D910CA51FF3`。它已被微信重新编码并移除 ICC，只能作为视觉证据，不能替代对远程原始 JPG 的色彩配置和哈希校验。原始 evidence 报告 Photoshop 原生嵌入 `sRGB IEC61966-2.1`、ICC 长度 `3144` 字节、`embedding_verified: true`；最终仍须由更新后的远程 verifier 直接读取原文件确认。

远程更新 verifier 已对 fixture 原始文件返回 `passed: true`。输入模板和四张源图哈希保持批准值；输出 PSD/JPG/evidence SHA-256 分别为 `193CABA71A7603F0D313D149E8966EE071C8D62BB405D21371E1D502063DEE6E`、`07741D43190746A590D293D4D34B1574B3E305CCE2A6E08338C8886DDEA1567E`、`FD9CF79750B1EC1B5FC407A282ECA93B292BC38D2BC281DB6EFB9720ED4045E3`。校验器确认画布 `790x1115`、PSD 为 8 位 RGB、原始 JPG 含 ICC、`fixed_structure_unchanged: true`、`frame_count: 4`。

最终 `G-详情-05-validation-output.psd` 已在 Photoshop 中重新打开。截图顶部明确显示输出文件名，画布与已验收 JPG 一致；图层面板中的 `front`、`sleeve`、`collar` 等目标保留智能对象图标。截图 SHA-256 为 `8EE1EA28D7512429D5878D904865DD6618FEA8B1C42B40C4E1CDE0F4A3357C91`。

至此 TV-06 的自动验证、结构验证、视觉验证和宿主重开验证全部通过，状态更新为“通过”。

远程复现步骤见 `docs/验证/UXP四框复杂页实机验证操作卡.md`。该操作卡继续作为 TV-06 的可重复验证记录，本轮无需再次执行。

## 后续范围

TV-06 不再有待实机证明项。整款页面、代表性失败隔离和绝对批量性能分别由 TV-11、TV-13 和 TV-14 继续验证，不以本页四框结果替代。
