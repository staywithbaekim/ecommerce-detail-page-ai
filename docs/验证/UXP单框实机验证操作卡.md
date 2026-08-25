# UXP 0.2.3 单框 sRGB 实机验证操作卡

## 本轮目标

只复测 `G-详情-03` 顶层路径 `2` 的既有智能对象替换和 cover 排版，并验证 JPG 显式转换为 `sRGB IEC61966-2.1`、导出后实际存在完整有效的 sRGB ICC profile，同时继续保证可编辑 PSD 和输入副本不被改变。不要使用旧的两页示例计划执行渲染。

`0.2.0` 已因图层顺序解释错误安全中止；`0.2.1` 和 `0.2.2` 均已作为无 ICC 输出的历史证据保留，其中 `0.2.2` 已证明 Photoshop 收到嵌入请求后仍未写入 ICC。旧版本不得再次执行，旧输出不得删除或覆盖。本轮必须使用新解压的 `0.2.3` 插件包和 `0.2.3` 样本包。

## 输入与输出

- 插件包：`uxp-validation-probe-0.2.3.zip`
- 样本包：`uxp-validation-g03-fixture-0.2.3.zip`
- PSD 副本：`template/G-详情-03-validation-copy.psd`
- 背面源图：`source/7Y9A3760.jpg`
- 计划：`single-frame-g03-render-plan.json`
- 输出目录：`output/`

样本包只含用户提供文件的明确副本。原始模板和原始照片不参与 Photoshop 操作。

## 开始前

1. 将两个 ZIP 分别完整解压到新目录，不要覆盖旧版插件目录。
2. 关闭 Photoshop 中已经打开的 `G-详情-03-validation-copy.psd`；插件会自行打开和关闭它。
3. 在 UXP Developer Tools 中删除旧探针记录，重新 Add Plugin，选择新插件目录里的 `manifest.json`，再点击 Load。
4. Photoshop 面板标题必须显示 `UXP Validation Probe 0.2.3`，按钮必须显示 `Execute single-frame copy test`。没有 `0.2.3` 说明加载的还是旧版。

## 执行顺序

1. 点击 `Load RenderPlan JSON`，选择样本包根目录中的 `single-frame-g03-render-plan.json`。
2. 面板必须显示 `Single-frame RenderPlan loaded`，并出现 `G-详情-03.frame-2`。
3. 点击 `Execute single-frame copy test`。
4. 第一次选择 `template/G-详情-03-validation-copy.psd`。
5. 第二次选择 `source/7Y9A3760.jpg`。
6. 第三次选择样本包中的 `output` 文件夹。
7. 等待面板显示 `Single-frame copy test completed`。

任一选择错误都会停止；失败时不得继续反复点击，应保留面板错误和 UXP Developer Tools 日志。

## 成功证据

`output/` 中必须同时出现：

- `G-详情-03-validation-output.jpg`
- `G-详情-03-validation-output.psd`
- `G-详情-03-validation-evidence.json`

还必须完成以下检查：

- JPG 中是 `7Y9A3760.jpg` 的背面商品，不是正面图。
- 画面覆盖注册框，没有白边。
- 固定文字、边框和其他图层未被误改。
- evidence schema 为 `uxp-validation-evidence/v2`，`jpg_color_profile` 为 `sRGB IEC61966-2.1`。
- `embedding_requested` 和 `embedding_verified` 均为 `true`；`photoshop_embedded_before_postprocess` 与 `adapter_injected` 明确记录实际走过的路径，二者至少一个为 `true`。
- `document_profile_after.assigned` 为 `true`，配置名称包含 `sRGB`。只有在 Photoshop 色彩转换真实成立后，才允许执行缺失 ICC 的确定性补写。
- 如果 `adapter_injected` 为 `true`，profile 必须为 `588` 字节，SHA-256 必须为 `14FB97CEB79AB10B4A72C839A16A334CAC46E7991C6A7F46DE4E56136675920D`。
- 输出 PSD 可以打开，路径 `2` 仍是可编辑智能对象。
- 输入 PSD 副本和源图运行后 SHA-256 仍与样本包记录一致。
- 在样本包根目录运行 `verify_fixture.ps1 -RequireOutputs`，必须返回 `passed: true`、`jpg_color.embedding_verified: true`，以及实际 ICC 的字节数和 SHA-256。

面板显示成功但缺少任一产物、视觉不对或输入哈希改变，都不能记为通过。
