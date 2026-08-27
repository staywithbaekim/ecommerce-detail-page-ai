# ADR-014：renderer-neutral RenderPlan v1

## 状态

已批准并冻结语义合同。机器可读合同位于 `docs/技术方案/schemas/render-plan-v1.schema.json`；跨语言实现和真实 UXP 回归仍需契约测试与 `RENDER-01` 验收。

## 日期

2026-08-27

## 背景

桌面端需要把模板规则、资源选择、裁切/变换、尺码表和输出意图交给 Photoshop 执行，同时保留未来 JSX 或独立渲染器的替换可能。历史验证计划包含 UXP 路径和本地文件，不能直接作为产品合同。

## 决策

1. `RenderPlan v1` 只表达渲染语义：计划身份、模板修订、逻辑资源、文档画布、校准目标、替换栈、布局、尺码表和输出意图。
2. 正式合同是独立 JSON Schema，不以 C# DTO、UXP 私有 JSON 或 Photoshop 命令列表作为唯一来源。Schema 版本固定为 `render-plan/v1`，未知顶层/嵌套字段拒绝。
3. 资源只登记逻辑 `resource_id`、用途、MIME 和内容 hash。允许的用途包括 `template_copy`、`high_resolution_render_source` 和 `deterministic_size_table`；`analysis_copy`、缩略图和生成式图片不得作为正式渲染资源。
4. 所有目标使用 `layer_path_order=psd_bottom_to_top`，路径段为从底到顶的 1-based 同级序号。UXP 的数组倒序转换只属于适配器；路径、类型、可见性、剪贴、蒙版、透明度、混合模式、ID 和结构签名在替换前校验。
5. 替换栈使用逐层 `superseded_layers`，禁止恢复模糊的 `superseded_paths`。目标名称替换后的变化属于执行 evidence，不改变目标身份合同。
6. 普通套图页的 `size_table` 必须为 `null`；动态尺码表必须引用本地确定性栅格器生成的无损资源，不能直接贴市场尺码图或 AI 生成图片。尺码单元格保留来源原文，包括 `-`、`\\` 和 `/`。
7. 输出只允许文件名和逻辑 artifact ID，不允许绝对路径、临时路径、session token 或任意 Photoshop 命令。路径、句柄和覆盖确认在 bridge 执行信封中绑定。
8. `plan_digest` 对稳定语义计算，不能包含机器路径、端口、token、运行时版本或 adapter 私有字段。页面重生成必须创建新的 `plan_id`/`idempotency_key`，历史计划不可变。
9. 兼容策略只允许显式迁移：同一主版本可增加可选字段并提供迁移器；破坏语义的变化建立 `render-plan/v2` 和新 ADR。拒绝未知版本，不静默猜测字段。

## Schema 与校验门

进入 Application/UXP 之前必须依次通过：JSON Schema、ID/摘要格式、资源引用闭合、目标结构证据、页类型与尺码互斥、输出文件名无路径逃逸、`plan_digest` 重算一致。任一失败返回结构化错误，不能开始 Photoshop 修改。

## 候选与取舍

| 方案 | 优点 | 风险 | 结论 |
| --- | --- | --- | --- |
| 独立 JSON Schema + 版本迁移 | 跨 WPF/UXP/未来渲染器，正反例可测试 | 需要维护版本和迁移器 | **采用** |
| C# DTO 作为合同 | 编写快 | UXP/未来渲染器被 .NET 类型绑死 | 不采用 |
| UXP 私有 JSON | 可直接执行 | AI/业务/Photoshop 细节泄漏，无法复用 | 不采用 |

## 影响

- `docs/技术方案/schemas/render-plan-v1.schema.json` 是提交前的规范合同；历史 `uxp-validation-plugin/render-plans/*.json` 仍是技术验证夹具，不自动宣称符合生产 v1。
- adapter 可使用内部 DTO，但必须在边界处验证/序列化为 v1，并通过正例、负例、迁移和跨语言测试。
- 执行 evidence 另存 `target_name_changes`、结构审计和 ICC 字段，不污染产品计划语义。

## 退出条件

若正式模板需要无法表达的语义，先新增兼容字段/版本和迁移测试；不得把 adapter 私有字段塞进计划或绕过 Schema。
