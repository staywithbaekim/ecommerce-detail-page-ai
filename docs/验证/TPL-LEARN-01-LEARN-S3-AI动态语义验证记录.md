# TPL-LEARN-01 LEARN-S3 AI 动态语义验证记录

## 结论

`LEARN-S3` 的版本化 AI 动态语义实现与真实参考组调用子门通过：应用在本地证据闭合后，以一个参考组一次请求生成开放式 `selection_requirement`，沿用 AI-01 的授权、凭据、WIC 分析副本、供应商适配、费用和未知结果边界；v2 结果、缓存和用户覆盖均 fail closed。`TPL-LEARN-01` 仍为进行中，因为仓库没有不同目标款真实照片夹具，不能关闭真实目标整组挑图、红黄绿准确性、主体适配和 S4-S7 重验。

## 实现证据

- 合同：`src/Desktop.Contracts/Ai/ReferenceLearningAiV2Contracts.cs` 与 `docs/技术方案/schemas/reference-learning-ai-v2.schema.json`。
- 应用：`ReferenceLearningAiService` 实现整组动态语义、目标整组排序、参考 hash 隔离、版本化缓存、红黄绿门槛、同图多框、逐框独立几何和持久化用户覆盖。
- 基础设施：`VolcArkAiAdapter` 使用严格供应商 JSON Schema；只读取 WIC 有界分析副本，不发送源路径或原始文件；旧提示词合同在读取凭据和 HTTP 前被拒绝。
- WPF：全部页面先完成本地分析，再调用一次批量语义服务；单页重新分析才调用单页接口。AI 文档持久化后，本地会话按持久化 digest 接管。
- 缓存：键包含操作、内容 hash、参考/目标 digest、模型、提示词、结果 Schema、压缩配置和帧上下文；读取时校验提示词版本、帧/素材闭包以及重复排名和几何。

## 真实 AI 受控证据

2026-09-08 使用 Windows Credential Manager 目标 `AI-01-VolcArk-Test`，对已批准参考照片目录 `需求样本/第一组/款式原图/A馨妈6015-58` 的 12 张照片执行一次受控 v2 请求：

- 结果：`Completed`；严格 v2 结果校验通过；
- 动态要求长度：244 字符；正向证据 5 项；置信度 `0.98`；
- 用量：输入 17,087 tokens，输出 696 tokens；耗时约 48 秒；
- 未持久化响应正文、供应商请求 ID、凭据或媒体；运行目录 `tmp/tpl-learn-s3-real-probe` 已清理并确认不存在；
- 该调用没有目标款素材，不能作为不同目标款匹配成功证据，也不应仅为补记录重复调用。

## 自动化验证

- `dotnet build EcommerceDetailPage.Desktop.slnx --no-restore -p:NuGetAudit=false`：0 警告、0 错误。
- v2 合同聚焦：3/3 通过；Application 参考学习聚焦：37/37 通过；Infrastructure AI/会话/提交聚焦：11 通过、1 个显式真实环境探测默认跳过；WPF 参考学习聚焦：3/3 通过。
- 完整 solution：539 通过、3 个显式环境探测跳过；另有 2 个既知失败，仅因仓库缺少 SIZE-01 外部金标 `size-real-a-577fa4d19f919a7a.jpg` 与 `size-real-b-9231ad299120bba6.jpg`，未补造或下载。
- `dotnet format EcommerceDetailPage.Desktop.slnx --verify-no-changes --no-restore`、`git diff --check`、变更文本凭据模式扫描、变更/未跟踪媒体与生成物扫描均通过；未发现凭据或可提交的 PSD/JPG/PNG/数据库/ZIP/EXE/DLL。
- 新 WPF 测试目录：`tmp/tpl-learn-s3-local-app-20260908`，共 1,217 个文件、173,137,353 bytes；`Desktop.App.dll` SHA-256 为 `F606319B622FBC1871B53C7C34F7C7F29C31F4184EEE55CBC741ED1AB8782089`，复用的已验证 worker SHA-256 为 `7C0E7E6B8AF86EBE00474452616EB475E5B45070FBE0E9ABFD55945FB35686A3`。使用显式隔离 `--data-root` 和完整既有数据副本启动，进程保持响应；正常关闭退出码为 `0`，最新实例记录 `safe_exit=1`，SQLite `integrity_check=ok`，未新增启动诊断行或 Windows `.NET Runtime` 崩溃事件。

## 2026-09-08 WPF 启动失败修复复核

产品负责人首次启动上一测试包时出现“应用启动失败”。只读检查确认默认 SQLite `integrity_check=ok`、v1-v12 migration checksum 正常；完整数据根仅复制到 `tmp/tpl-learn-s3-existing-data-repro` 复现，未修改默认用户数据。加入启动阶段脱敏诊断后取得原始异常：`main_window_creation` 阶段无法解析 `ReferenceLearningAiService` 所需的 `IAiUploadAdmissionPort`。根因是 LEARN-S3 新依赖已实现于 `SqliteDesktopStore`，但桌面组合根漏登记接口映射。

修复将 `IAiUploadAdmissionPort` 映射到同一 durable `SqliteDesktopStore`，并使共享注册导致的 `SqliteWriteQueue.DisposeAsync` 重复释放幂等；后者此前会在正常关闭时对已关闭连接再次执行 WAL checkpoint，Windows 事件日志中表现为 `ExecuteNonQuery can only be called when the connection is open`。启动 catch 现在记录固定阶段、异常类型、HResult 和经既有 `RedactingFileLogger` 脱敏的堆栈；若日志自身不可写，界面明确保留阶段且不会用第二个异常覆盖原始原因。

新增 DI 映射、重复释放和启动诊断脱敏 3 项聚焦回归均通过；完整 solution 更新为 539 通过、3 跳过，仍只有上述 2 个 SIZE-01 外部金标缺失失败。发布包在完整既有数据副本上启动/关闭证据见上方包记录。本修复只恢复 LEARN-S3 测试入口和诊断/退出安全，不改变 `TPL-LEARN-01` 仍缺不同目标款真实夹具的验收状态。

## 未关闭项与下一步

1. 准备至少一个与参考款不同的、经产品负责人批准的目标款照片文件夹；记录源 hash，使用整组匹配验证同图多框、相近替代红黄绿、逐图主体几何和参考款零引用。
2. 用新测试包重验动态规则文字、用户修改/确认/缓存失效、退出重启恢复，即 `LEARN-S4` 至 `LEARN-S7` 受影响子门。
3. 完成上述真实证据和 reviewed diff 前，不交接 `RENDER-01`，不宣称 `TPL-LEARN-01` 完成。
