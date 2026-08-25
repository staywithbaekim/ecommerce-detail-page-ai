# UXP 0.3.0 四框复杂页实机验证操作卡

## 本轮只验证什么

本轮只验证 `G-详情-05-细节.psd` 的四个已校准商品位：同一页面连续替换四个智能对象后，剪贴关系、窗口基底、旧替换栈、固定文字、装饰和层级保持不变，并一次性输出可编辑 PSD 与 sRGB JPG。

这不是整款生成、批量性能、AI 选图或尺码表验证。插件只使用包内用户素材副本，禁止生成图片，禁止保存输入 PSD 副本。

## 只使用这个包

- 插件压缩包：`uxp-validation-tv06-plugin-0.3.5.zip`
- 插件 SHA-256：`5EC2C63B59B6C971CA4B9535A878204E1F5C94DEFB976305BF7ADD2179AC4E60`
- 解压后根目录：`uxp-validation-tv06-multi-frame-0.3.0.1/`
- 插件目录：`uxp-validation-plugin/`
- 测试目录：`fixture/`

不要把新文件覆盖进旧的 `0.2.x` 目录。完整解压到一个新目录后再操作。

若旧版返回审计名不一致或 document id undefined，使用当前独立插件包复测，并继续选择原 fixture 的 RenderPlan、模板、source 和 output。当前修正版为 `uxp-validation-tv06-plugin-0.3.5.zip`；它兼容 UXP 对审计名首尾空白的规范化，打开阶段使用 `app.open()` 返回文档，替换后优先从 `app.activeDocument` 重新获取受控 Document/Layer，不依赖立即刷新的 `app.documents`，不包含或修改 PSD、素材与 RenderPlan。

## 开始前

1. 双击测试目录中的 `CHECK-TV06-INPUTS.bat`，窗口必须显示 `"passed": true`。
2. 保存 Photoshop 中正在处理的其他工作；关闭已经打开的 `G-详情-05-validation-copy.psd`。
3. 打开 Photoshop 2025 和 UXP Developer Tool。
4. 在 UXP Developer Tool 中删除旧验证探针记录，点击 Add Plugin，选择本包 `uxp-validation-plugin/manifest.json`，再点击 Load。
5. Photoshop 面板标题必须是 `UXP Validation Probe 0.3.5`，按钮必须是 `Execute loaded RenderPlan`。

本轮不需要启动 PowerShell bridge，也不需要点击 Handshake。

## Photoshop 操作顺序

1. 点击 `Load RenderPlan JSON`。
2. 选择 `fixture/multi-frame-g05-render-plan.json`。
3. 面板应显示 `RenderPlan loaded`、`multi_frame_copy` 和四个 frame id。
4. 点击 `Execute loaded RenderPlan`。
5. 第一次选择 `fixture/template/G-详情-05-validation-copy.psd`。
6. 第二次选择整个 `fixture/source` 文件夹，不要逐张选图。
7. 第三次选择 `fixture/output` 文件夹。
8. 等待面板显示 `Multi-frame copy test completed`。执行期间不要点击 Photoshop 画布或关闭文档。

任一步选择错误或面板报错都应停止，不要连续反复点击；保留面板内容和 UXP Developer Tool 日志截图。若 `0.3.5` 仍在 `open_template_copy` 阶段返回 `WORKING_DOCUMENT_NOT_UNIQUE`，不要继续重试，回传完整 JSON、APP LOGS、插件 Console，以及当时 Photoshop 是否已显示模板画布；主开发机将据此决定是否启动只读 open-probe。

## 自动验收

成功后，`fixture/output/` 必须出现：

- `G-详情-05-validation-output.jpg`
- `G-详情-05-validation-output.psd`
- `G-详情-05-validation-evidence.json`

先解压 `uxp-validation-tv06-verifier-0.3.5.zip`，把其中的 `verify_fixture.ps1` 和 `VERIFY-TV06.bat` 复制到现有 `fixture/` 根目录并覆盖两个旧脚本；不得覆盖 `template`、`source`、`reference`、`output` 或 RenderPlan。再双击 `fixture/VERIFY-TV06.bat`。只有窗口显示 `"passed": true`、`fixed_structure_unchanged: true`、`frame_count: 4` 才完成自动验收。

## 肉眼检查

打开输出 JPG，并和 `fixture/reference/G-详情-05-manual-reference.jpg` 对照：

- 左上是 `7Y9A3750.jpg` 的完整正面商品；
- 右上是 `7Y9A3755.jpg` 的袖口细节；
- 左下是 `7Y9A3751.jpg` 的领口细节；
- 右下是 `7Y9A3762.jpg` 的水洗标/吊牌细节；
- 四个窗口都填满，没有白边、黑块或旧商品露出；
- 顶部标题、四组说明文字、边框、圆角和整体层级没有错位或消失；
- 视觉效果接近人工参考，肉眼没有明显错误。

再打开输出 PSD，确认可以正常打开，图层面板仍能看到四个目标智能对象和原有固定内容。不要在这个 PSD 中继续编辑或保存。

## 回传

回传以下四项：

1. 输出 JPG；
2. 输出 PSD；
3. evidence JSON；
4. 一张输出 PSD 重新打开后的 Photoshop 截图，包含画布和图层面板。

输入检查、插件面板成功或自动脚本通过，都不能替代最后的肉眼检查。
