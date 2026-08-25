<!-- package: tv13 -->
# UXP 0.4.7 TV-13 失败隔离实机验证操作卡

本轮只证明一次代表性失败：前两页 JPG 已完整生成后，测试计划在第三页尚未开始时主动返回固定错误 `TV13_CONTROLLED_FAILURE`。插件必须删除前两张 JPG，保持输入不变，并在同一插件会话中继续完成正常六页任务。

## 开始前

1. 解压小型更新器包 `uxp-validation-tv13-update-0.4.7.1.zip`，关闭旧的 CHECK/VERIFY 窗口，双击 `APPLY-RECOVERY-0.4.7.1.bat`，选择直接包含完整 `fixture` 的现有整款包目录。目录名可以是 `0.4.6` 或 `0.4.7`，更新器不会移动或重命名大 fixture。
2. 记录更新器输出的 `LOAD_MANIFEST`，再双击同一目录下 `fixture/CHECK-TV13-INPUTS.bat`；必须显示 `passed: true` 和 `failure_output_clean: true`。
3. 打开 Photoshop 并最大化，再打开 UXP Developer Tool；删除旧插件记录，加载 `LOAD_MANIFEST` 指向的 `manifest.json`。
4. Photoshop 面板标题必须是 `UXP Validation Probe 0.4.7`。不要重启 Photoshop 或重新 Load 插件，直到本轮两次执行都完成。

## 第一次：受控失败

1. 点击 `Load RenderPlan JSON`，选择 `fixture/tv13-controlled-failure-render-plan.json`。
2. 点击 `Execute loaded RenderPlan`。
3. 依次选择 `fixture/template`、`fixture/source`、`fixture/tv13-failure-output` 三个文件夹。
4. 等待面板显示 `TV-13 controlled failure cleaned safely`。
5. 面板 JSON 应显示错误码 `TV13_CONTROLLED_FAILURE`、已完成页面 `G-详情-01` 和 `G-详情-03`、`partial_outputs_removed: true`、`residual_outputs: []`。这是预期结果，不要重试该失败计划。

## 第二次：继续正常运行

1. 不关闭 Photoshop，不重新 Load 插件。
2. 点击 `Load RenderPlan JSON`，选择 `fixture/whole-product-a6015-render-plan.json`。
3. 点击 `Execute loaded RenderPlan`。
4. 依次选择 `fixture/template`、`fixture/source`、`fixture/tv13-recovery-output` 三个文件夹。
5. 等待面板显示 `Whole-product copy test completed`。

## 最终验收

双击 `fixture/VERIFY-TV13.bat`。只有显示 `passed: true`、`failure_output_clean: true`、`continued_run_passed: true`、`jpg_count: 6`、`prior_failure_code: TV13_CONTROLLED_FAILURE`，才算通过。回传完整窗口截图即可，不需要再次肉眼比较六张图。
