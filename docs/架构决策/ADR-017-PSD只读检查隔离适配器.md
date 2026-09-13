# ADR-017：PSD 只读检查隔离适配器

## 状态

已批准。TPL-S0 于 2026-08-27 通过，正式采用隔离的本地 `psd-tools` worker；2026-09-08 为 TPL-LEARN-01 增补最终合成像素贡献证据与完整 composite 运行依赖。

## 日期

2026-08-27

## 背景

TPL-01 必须在不占用 Photoshop 的默认校准路径中读取应用内部 PSD 副本的画布、色彩/位深、完整层树、可见性、剪贴、蒙版、智能对象、稳定层标识和结构签名。未知或损坏 PSD 不能拖垮 WPF 进程，也不能通过手写通用 PSD 解析器、放宽结构门或把所有智能对象自动注册为图片框来救场。

## 候选证据

| 候选 | 证据 | 结论 |
| --- | --- | --- |
| A：进程内 .NET | NuGet 上不存在 `PhotoshopFile 1.0.0`，联网恢复返回 `NU1101`。替代候选 `Aspose.PSD 26.8.0` 可读 G03/G05 目标 ID，但 G03 返回 `46` 条原始记录、递归展开 `63` 层，而批准结果为 `42` 层；G05 返回 `51` 条原始记录、递归展开 `65` 层，而批准结果为 `46` 层。未授权预览含评估水印，峰值工作集达到约 `496.7 MB`，并受商业 EULA 约束。 | 拒绝；结构树不一致、评估预览和许可/资源代价未满足“全部通过才采用”。不为其手写层树重建。 |
| B：隔离 worker | `psd-tools 1.18.0` 对 8 个批准 PSD 副本全部成功，G03 为 `42` 层、路径 `2`/ID `257`/`back_main`；G05 为 `46` 层，四个批准目标路径、ID、名称、剪贴基底和边界与 Photoshop/UXP 证据一致。损坏输入和未知协议稳定阻断。 | 采用。 |
| C：Photoshop/UXP | Photoshop 2025 `26.8.1` / UXP v5 已有真实只读结构和准确渲染证据。 | 保留为显式兼容性探针和准确预览对照，不作为默认本地校准实现。 |

## 决策

1. Infrastructure 通过 `IPsdInspectionPort` 启动按请求隔离的 `psd-inspection-worker`。worker 使用版本化 `psd-inspection/v1` JSON，经供应商无关 DTO 返回结果；WPF、Domain 和 Application 不引用 `psd-tools` 类型。
2. worker 只接受应用自有根目录中的明确 PSD 副本。应用负责路径门禁，worker 在读取前后比较 SHA-256；用户源 PSD、源照片和人工结果永远只读。
3. worker 返回完整层路径、稳定层 ID、类型、可见性、剪贴、透明度、混合模式、蒙版、智能对象和结构签名。每个剪贴层还必须返回基底路径、基底边界和基底蒙版状态；剪贴层找不到基底时返回结构阻断。
4. client 对进程超时、退出码、响应上限、协议/请求 ID、缺字段、非法数组、计数不一致和异常几何做封闭校验。损坏/不支持结构返回 `TPL_PSD_UNSUPPORTED_STRUCTURE`；进程问题使用稳定 `TPL_PSD_WORKER_*` 错误码，不向 UI 暴露库异常或绝对路径。
5. 本地校准使用 worker 合成图、注册框几何和 WPF 覆盖层。代表四框页的默认 cover 与 Photoshop 人工参考框内平均绝对差异为 `2.18%` 至 `2.80%`；`1.15` 缩放及 `(12,-12)` 偏移探针仍全覆盖，换算误差不超过 `0.5 px`。存在应用无法表达的用户蒙版、矢量蒙版或变换时阻断并要求兼容性检查，不猜坐标。
6. Photoshop adapter 继续只消费 renderer-neutral `RenderPlan`；AI、选图和产品规则不得进入 worker 或 Photoshop adapter。

## 许可证与发布

- 运行时锁定 `psd-tools 1.18.0`、`attrs 26.1.0`、`numpy 2.2.6`、`Pillow 12.3.0` 和 `typing_extensions 4.16.0`。直接依赖分别采用 MIT、MIT、BSD 系、MIT-CMU 和 PSF-2.0 许可；发布必须携带适用的第三方声明。
- PyInstaller `6.16.0` 只参与构建，采用允许分发非自由程序的 bootloader 例外；其构建依赖已锁定。
- Windows x64 onedir 制品为 `102` 个文件、`58,631,966` bytes，不依赖客户机 Python；本次 EXE SHA-256 为 `112D411992B95B6A988E641B9D2FB1643618C134A549B1EED56EC82F7F07C99A`。生成制品不进入 Git，正式安装打包由发布任务重建并生成第三方声明/SBOM。
- 8 个批准 PSD 以“一请求一进程”运行，总解析耗时 `7,251.404 ms`，单请求最大峰值工作集 `184,164,352` bytes；G05 经 .NET client 的峰值为 `158,187,520` bytes。该数据关闭 TPL-S0 技术门，不替代正式安装或完整性能矩阵。

## 影响与约束

- 发布面增加一个约 `55.9 MiB` 的隔离 worker 目录和协议兼容责任，但 PSD 崩溃、内存和解析依赖不进入 UI 进程。
- `requirements.lock` 与 `build-requirements.lock` 是可复现构建输入；升级 PSD 库、Python、协议或发布形态必须重新跑批准 PSD、损坏反例、资源和许可验证并更新 ADR。
- worker 只提供结构与合成证据，不决定图片框业务含义，不执行真实 AI、Photoshop 渲染、平台上传或模板规则。
- TPL-S0 已通过，可进入 TPL-S1；这不代表 TPL-01、UI-01、AI-01、RENDER-01 或 INSTALL-01 已完成。

## 2026-09-08 增补：最终合成像素贡献证据

TPL-LEARN-01 不能仅凭 `visible`、图层边界或“智能对象”类型判断图片框。隔离 worker 现对每个智能对象返回 `PsdLayerPixelEvidence`：自身非透明像素数，以及从完整页面合成中移除该层后真正发生变化的像素数和边界。Application 只有在嵌入内容与参考源照片 SHA-256 精确闭合、图层有效可见、自身有像素且对最终页面有非零贡献时，才允许登记人工学习候选；同一明确剪贴窗口中的连续重叠层只保留最终有效商品层。图层名称只可生成用户可修改的用途提示，不参与是否登记图片框的判定。

最终页面重合成会触发 PSD 的渐变、矢量、蒙版等效果，因此运行时由最小 `psd-tools` 集合扩展为锁定的 `psd-tools[composite]` 闭包。`requirements.lock` 新增 `aggdraw 1.3.19`、`scipy 1.15.3`、`scikit-image 0.25.2` 及其锁定运行依赖 `ImageIO 2.37.4`、`lazy-loader 0.5`、`networkx 3.4.2`、`packaging 26.3`、`tifffile 2025.5.10`；许可证以 MIT/Python-MIT、BSD 系及 Apache-2.0 OR BSD-2-Clause 为主，正式发布仍必须生成完整第三方声明和 SBOM。只安装原最小集合会在真实 PSD 渐变叠加处缺少 SciPy，该构建不得发布，也不得误判为 PSD 损坏。

fix16 候选 onedir worker 为 `1,193` 个文件、`166,051,888` bytes，EXE SHA-256 为 `7C0E7E6B8AF86EBE00474452616EB475E5B45070FBE0E9ABFD55945FB35686A3`。打包 worker 对 6 份真实 PSD 完整运行通过，总墙钟时间 `240,674.753 ms`，单页最慢 `56,399.096 ms`，最大峰值工作集 `581,738,496` bytes；默认单请求超时由 30 秒调整为 120 秒。该数据只关闭本地像素证据与打包可运行性，不替代正式安装、长期性能矩阵或 Photoshop/RENDER-01 验收。
