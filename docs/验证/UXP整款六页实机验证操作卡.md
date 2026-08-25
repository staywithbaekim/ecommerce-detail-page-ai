<!-- package: tv11 -->
# UXP 0.4.6 整款六页实机验证操作卡

`0.4.4` 已通过 session-token Action Manager 成功打开第一张 PSD。Photoshop 最大化后，执行进入图片框预检并准确返回 `G-详情-01` 路径 `2` 的剪贴状态不匹配；任何图片尚未替换，输入未保存，临时输出已清理。同哈希 PSD 的本地只读结构报告确认路径 `2` 是可见智能对象且真实 `clipping: false`。`0.4.5` 为每个下层替换栈图层增加独立结构合同，只为该路径记录 `false`，其余 13 个下层图层继续严格要求 `true`。实机运行后发现 Photoshop 会把已替换的目标智能对象名称改成源文件名；`0.4.6` 仅允许目标路径发生这一名称变化，目标 ID 和其余结构字段仍严格校验。

## 本轮只验证什么

本轮验证第一组真实款式 `A馨妈6015-58` 的 6 个非尺码页面、11 个已校准商品图片框能否一次生成。它代表日常默认流程：只输出 6 张 JPG，不输出 PSD。

动态尺码表属于后续 `SIZE-01`，不混入本轮。插件只使用包内 PSD 副本和用户提供的源照片，禁止生成图片，禁止保存输入 PSD 副本。任一页面失败时，本轮已产生的 JPG 必须全部清理。

## 开始前

1. 优先使用小型 `uxp-validation-tv11-update-0.4.6.zip`。关闭 Photoshop 和 UXP Developer Tool 后，解压增量包并双击 `APPLY-UPDATE-0.4.6.bat`，选择现有 `uxp-validation-tv11-whole-product-0.4.5` 内层文件夹；脚本会校验输入、备份少量旧配置并原地更新，不复制 420 MB fixture。
2. 只有现有 `0.4.5` 完整目录被删除或损坏时才需要重新取得 fixture；正常情况下不需要传大包。
3. 双击 `fixture/CHECK-TV11-INPUTS.bat`，窗口必须显示 `"passed": true`、`page_count: 6`、`frame_count: 11`。
4. 保存 Photoshop 中正在处理的其他工作，关闭所有名称含 `validation-copy.psd` 的旧验证文档。
5. 打开 Photoshop 2025 并保持窗口最大化，再打开 UXP Developer Tool。
6. 在 UXP Developer Tool 中删除旧验证探针记录，点击 Add Plugin，选择更新后目录中的 `uxp-validation-plugin/manifest.json`，再点击 Load。
7. Photoshop 面板标题必须是 `UXP Validation Probe 0.4.6`。

本轮不需要 PowerShell bridge，也不需要点击 Handshake。

## Photoshop 操作顺序

1. 点击 `Load RenderPlan JSON`。
2. 选择 `fixture/whole-product-a6015-render-plan.json`。
3. 确认面板显示 `RenderPlan loaded`、`whole_product_copy`、6 pages 和 11 frames。
4. 点击 `Execute loaded RenderPlan`。
5. 第一次选择整个 `fixture/template` 文件夹。
6. 第二次选择整个 `fixture/source` 文件夹。
7. 第三次选择整个 `fixture/output` 文件夹。
8. 等待面板显示 `Whole-product copy test completed`。执行期间不要操作 Photoshop、关闭文档或重复点击。

任一步选错或面板报错都立即停止，保留完整面板 JSON 和 UXP Developer Tool APP LOGS 截图，不要连续重试。

## 自动验收

成功后双击 `fixture/VERIFY-TV11.bat`。只有窗口显示：

- `"passed": true`
- `jpg_count: 6`
- `editable_psd_count: 0`
- `all_fixed_structures_unchanged: true`
- `page_count: 6`
- `frame_count: 11`

才完成自动验收。

## 肉眼检查

逐张打开 `fixture/output` 中的 6 张 JPG，并与 `fixture/reference` 中同名页面的 `manual-reference.jpg` 对照：

- 页面尺寸、文字、装饰、蒙版、边框和整体层级保持；
- 商品正反面、袖口、领口、面料和标签素材没有串图；
- 每个图片框填满，无白边、黑块、旧商品残留或明显错位；
- 裁切和主体位置接近人工结果，肉眼无明显差异。

前两项单页/四框测试已经证明基础替换能力；本轮重点是首次整款输出。剩余 6 个此前没有可编辑人工 PSD 金标的图片框，如果首次候选存在位置偏差，应记录具体页面和图片框后调整校准参数，不能把失败隐藏成通过。

## 回传

1. `VERIFY-TV11.bat` 完整成功窗口截图；
2. 6 张输出 JPG；
3. 整款 evidence JSON；
4. 一张 Photoshop 插件面板显示成功的截图；
5. 肉眼检查结论，以及存在偏差时的页面名和方向描述。
