# UXP 技术可行性验证计划

## 目标与边界

本轮只验证 Photoshop 执行通道的最小纵向能力，不建设完整桌面应用。UXP 插件只接收结构化 `RenderPlan`，不承载 AI、选图规则、批次队列、本地数据库或自然语言到 Photoshop 指令的转换。所有 Photoshop 操作必须针对模板副本和临时输出目录进行。

通过条件沿用 `docs/任务/UXP技术可行性验证.md`、`docs/架构决策/ADR-001-Photoshop执行通道验证策略.md` 和 `docs/验收矩阵.md`：TV-01 至 TV-13 必须有可复现证据；视觉结果不得有旧商品残留或固定图层误改；Photoshop 阶段中位耗时不超过同条件 JSX 基准的 1.2 倍。

## 样本选择

样本审计文件：`需求样本/第一组/模板PSD/output/_inspection/inspection-report.json` 和 `manifest-skeleton.json`。

| 用途 | 模板副本 | 选择理由 | 绑定目标 |
| --- | --- | --- | --- |
| 单图片框纵向冒烟 | `G-详情-03.psd` | 790×1252；一个候选商品位；剪贴结构 | 层路径 `2` |
| 复杂页面纵向验证 | `G-详情-05-细节.psd` | 790×1115；四个候选商品位；多个裁切和层级 | 层路径 `3`、`7`、`11`、`15` |
| 重复模板控制 | `G-详情-01.psd` 与 `G-详情-01-backup.psd` | 审计哈希相同，验证不重复执行 | 不执行替换 |

审计显示存在隐藏智能对象和候选堆叠，因此不能按“所有智能对象都替换”。`RenderPlan` 必须同时携带层路径、结构证据和源图 SHA-256。

## RenderPlan 计时边界

每次基准记录单调时钟时间戳，分段如下：

1. `prepare`: 打开模板副本、确认输出目录和清理上一次临时文件；不计 AI 和桌面端扫描。
2. `photoshop_cold_start`: Photoshop 未运行时，从启动到插件握手完成。
3. `photoshop_render`: 从提交页面级计划到 JPG 写入完成；包含置入、裁切、变换、图层恢复和导出。
4. `photoshop_hot_render`: Photoshop 保持打开时重复执行同一计划。
5. `save_editable_psd`: 可选 PSD 保存和文件落盘。
6. `verify`: 尺寸、色彩配置、输出命名、哈希和图层结构检查。

JSX 基准必须在同一机器、同一副本、同一输出选项、同一缓存状态下重复三次，报告冷启动、热运行中位数和异常值。UXP 只比较 Photoshop 阶段，不把 AI、缩略图和桌面 UI 时间混入。

## 输出核对清单

- 原始 PSD、源照片和人工参考结果运行前后 SHA-256 不变。
- 输出目录只包含预期 JPG，以及勾选时的可编辑 PSD；不接受旧文件冒充成功结果。
- JPG 尺寸、sRGB、质量和命名符合 `RenderPlan.output`。
- 输出视觉与人工参考对比：主体、裁切、位置、页面尺寸和固定装饰一致；无错款、旧商品残留或白边。
- 可编辑 PSD 中智能对象、文字、蒙版和必要图层效果仍可人工修改。
- 受控失败必须返回 `job_id`、`page_id`、`frame_id`、错误码和清理动作，不污染正式输出。

## 实机前置条件

Photoshop 2022 在另一台电脑。实机动作需要用户通过 UU 远程完成或明确协助。首次连接只做只读盘点：Windows 版本、CPU、内存、GPU、磁盘类型、暂存盘、Photoshop 精确版本、UXP Developer Tool 版本和插件安装路径。若 Photoshop 低于 23.3，记录环境差距并停止安装，不自行升级。

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
