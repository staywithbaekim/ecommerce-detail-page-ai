# AI-01 S2 WIC 分析副本重编码验证记录

## 范围与结论

本轮只验证 Windows 首发环境的应用自有分析副本重编码，不调用火山方舟、不产生账单、不进入 S4。实现使用 .NET WindowsDesktop framework 自带 WPF/WIC，没有新增 NuGet 图像包；Domain/Application/Contracts 和 renderer-neutral `RenderPlan` 不引用 WIC/WPF。

`wic-bounded/max1600/jpeg-q82/png/v1-candidate` 是候选配置，不是生产冻结值：最长边 1600、JPEG quality 82、源文件 64 MiB、输出 8 MiB、源像素 100M。仍需逐次审批后的最少量正式 adapter probe 才能冻结或修订它，并为批量、并发、重试和费用门提供证据。

## 编码与安全策略

| 输入 | 输出 | 规则 |
| --- | --- | --- |
| JPEG | JPEG | EXIF orientation 1–8 归一；quality 82 candidate；不复制 EXIF/comment |
| PNG | PNG | 保留非不透明 alpha；不复制文本/路径元数据 |
| TIFF | JPEG | 只接受单帧；按 JPEG candidate 输出；不复制 TIFF 元数据 |

- 在实际像素展开前检查格式、帧数、宽高和总像素；最长边按高质量 WIC 缩放。
- compression profile 与最长边、JPEG quality、源/输出字节和像素门闭合；参数变化但沿用 `wic-bounded/max1600/jpeg-q82/png/v1-candidate` 时配置守卫直接拒绝，避免不同编码结果共享 cache fingerprint。
- 源长度和 INPUT-01 登记值闭合，编码前后重新计算 SHA-256；源文件以只读方式打开，应用从不写回、重命名或删除。
- 输出编码使用 8 MiB candidate 硬上限流，越界立即中断；写入前后用真实输出重新检查 MIME、尺寸、digest 和长度。
- 副本和 JSON sidecar 只写应用自有目录；源变化、取消、损坏内容、资源越界或原子 metadata 写入失败时清理本次应用文件，不触碰源。
- durable/shared lease sidecar 保持既有重试、首层到精细分析、暂停/重启和终态清理协议。

## 离线验证覆盖

- EXIF 1–8：用 120×80 非对称四象限 JPEG 核对每个方向的输出宽高和象限位置；1–4 为 120×80，5–8 为 80×120，镜像/旋转均不裁切。
- 格式：JPEG 缩放输出 JPEG；PNG 输出 PNG 且保留 alpha；TIFF 输出 JPEG；声明格式与实际内容不一致时拒绝。
- 元数据：输出为单帧，EXIF orientation 和 JPEG comment 均为空。
- 资源门：最长边 1600、源 byte limit、输出硬 byte limit、100M source pixel limit、登记尺寸不匹配均有正反例。
- 故障门：损坏输入、预取消、源在准备期间变化、metadata 写失败均不留下副本/sidecar；源字节和 hash 保持不变。
- 读取闭合：实际副本 digest/长度/MIME/尺寸与 sidecar 重新核对；篡改 MIME/尺寸时联网前拒绝。
- 兼容性：`Desktop.Infrastructure` 与对应测试改为 `net10.0-windows` 并引用 `Microsoft.WindowsDesktop.App.WPF`；solution 项目引用构建验证不允许警告或错误。

## 验证命令与结果

- `dotnet test tests/Desktop.Infrastructure.Tests/Desktop.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~AiAnalysisCopyAndResultStoreTests' --verbosity minimal`：25/25；包含 reviewed diff 新增的“参数漂移不得复用候选 profile”回归。
- `dotnet build EcommerceDetailPage.Desktop.slnx --no-restore --verbosity minimal`：0 警告、0 错误。
- `dotnet test EcommerceDetailPage.Desktop.slnx --no-build --no-restore --verbosity minimal`：294 通过、1 个 `RealProbeThroughFormalAdapter` 按设计跳过、0 失败。
- `dotnet format EcommerceDetailPage.Desktop.slnx --no-restore --verify-no-changes --verbosity minimal` 与 `git diff --check`：通过。
- reviewed diff 发现并修正“自定义编码参数仍复用默认 candidate 版本”的 cache fingerprint 风险；参数漂移现在必须提供新 profile version。opt-in 真实 probe 保持禁用，本轮没有联网或账单。

## 未关闭门

离线 WIC 证据只关闭“正式生产链存在真实重编码、方向/透明度/元数据/资源/回滚边界可复现”子项。候选 profile、单款请求上限、并发、重试、费用告警和硬停止尚未由真实用量冻结；S2/S3 和 AI-01 整体继续未通过。后续真实请求必须逐次取得用户审批，只使用官方演示图或明确批准的应用副本，不保存 Key、认证头、原始响应、response ID、媒体、本机路径或账单原文。
