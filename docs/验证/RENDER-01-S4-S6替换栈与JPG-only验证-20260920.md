# RENDER-01 S4/S6 替换栈与 JPG-only 持续验证

日期：2026-09-20。沿用同一工作树、正式 adapter/owner/broker/worker/SQLite，保留全部未提交改动。**仅关闭本文列出的限定实现子门，RENDER-S1 至 S7 和 SIZE 联验未整体关闭。**

## 范围与实现

- 按已校准 `superseded_layers` 逐个核对路径、ID、smart object 类型及有效可见性后隐藏旧商品层。执行审计和独立 PSD 验证仅允许这些指定层的可见性改变，其几何、嵌入内容、像素、蒙版、效果仍受保护。冻结 Schema 的 `expected_visible=true` 保持不变；不接受已经隐藏的旧层。
- JPG-only 计划显式声明 `output/verification-psd-v1`。适配器仍保存临时 PSD 并认证回传为 `verification_psd`，独立 worker 检查固定内容和目标嵌入/几何后才允许提交 JPG。正式 PSD 与验证 PSD 不能互换，旧计划不静默升级。
- 正式 manifest 的 `files` 只列实际发布文件；`media.files` 同时保留 JPG 与验证 PSD 的长度/hash。验证 PSD 不进入正式目录；受控 staging 暂存保留用于证据，本次未增加自动清理或删除任务数据。
- Verified 记录的 manifest 摘要绑定独立媒体验证结果；恢复核对该摘要、数据库身份和全部正式文件，不重跑 Photoshop，也不把验证 PSD 纳入正式交付文件集合。
- 原探针在接收任何配对材料前解析本地 page input；输入错误仅返回 `PROBE_PAGE_INPUT_INVALID`，不输出原始异常。未创建替代探针。

## 真实环境与源保护

正式插件 `local.ecommerce.detail-renderer` 0.1.0；Photoshop `26.8.1 (20250624.r.8 5e0d05d)`；UXP `uxp-8.3.0-uxp-b1d8c85`。网络仍为单条 `ws://localhost`，listener 仅绑定 127.0.0.1，认证/Host/Origin 未放宽。每个会话使用新内存一次性码和限当前用户管道，秘密未进入参数、文件或日志。

真实模板 `G-详情-01-validation-copy.psd` 与照片 `7Y9A3750.jpg` 的历史校准事实来自既有 TV 计划；没有运行或复制旧插件的宽权限实现。每次运行重新复制到独立受控输入目录，正式输出不覆盖。

| 源 | 长度 | 前后 SHA-256 |
| --- | ---: | --- |
| 模板 PSD | 39365416 | `164e4af1cf934f6a6d1543d912b792bbdef7caf6dd42520b157900d060eb62f4` |
| 源 JPG | 13106829 | `c04dddd4c7802277d9996f347ba6fd4285987147941eed5a69638e0443696d57` |

画布 790×1327；目标 ID247 / plan path `[4]`，指定隐藏旧层 ID232 / `[3]`；隐藏固定对象 ID184 保持原样。源解码尺寸 4480×6720；显式 placement 为 left=-47.333333、top=0、width=884.666667、height=1327。不是对旧 layout 的自动猜测或生产迁移。

## 运行结果

两次成功运行均为 `LISTENING → PAIRED → PLAN_ACCEPTED → MEDIA_VERIFIED → PAGE_COMMITTED → PROBE_FINISHED`，exit 0；connected/host_ready/media_validated/published=true、completed、event_seq=4。

| 项目 | 替换栈 JPG+PSD | JPG-only |
| --- | --- | --- |
| run | `47cb87b8590a44f4ab2e57cc3a0ae333` | `df8f3a76998c46a7919e15f0b5b40387` |
| plan digest（sha256） | `1626a70b5208409a9bb0ff6d20274eeba23cde36ad5609f61b54f051f366c835` | `f2d9141a7bb3731f9ca34a28d934cf27fbd9af459a552990af3eb9e5f341e584` |
| 认证心跳 | 388 | 390 |
| 实际独立结构检查 | 37 层、14 文字层 | 37 层、14 文字层 |
| JPG 长度 | 786793 | 786793 |
| JPG SHA-256 | `32ce69063f97c006dd48e48325aedacfed9aa67791881dfcf14a31b00bd5cf95` | `f1a68870b47f9028c0933421920373b4bdf11370341d14248505d585879f5403` |
| PSD 类型/长度 | 正式 editable_psd / 47606398 | 内部 verification_psd / 47606398 |
| PSD SHA-256 | `42f6fc38e61bb2e59d484a90c28200448e7aa5cadcd1b664d775aa813fcb810f` | `942bdc11963b1192b0bcc57783b26cd97fdc5f204f3b00bedba9ef3d9ac09134` |
| manifest SHA-256 | `91070f25576a6e45bb973db2b5ff599800c9d04d30c56fcd072383f9c348993a` | `a8aac65d30acc46e421e396f26a363fe8f506b108eb3336361118c23b79f81f7` |

两张 JPG 均通过解码与标准 sRGB ICC 检查：3144 字节，ICC SHA `2b3aa1645779a9e634744faf9b01e9102b0c9b88fd6deced7934df86b949af7e`。JPG-only 正式目录只有 `01-detail.jpg`、`manifest.json`、`committed.marker`。

两次新进程恢复均 `COMMIT_RECOVERED output_count=1 rendered=false`、exit 0。各自 `commit-probe.json` 均确认 event_seq=4、`temporary_resources_released=true`。JPG-only execution 为 `execution_b8b623b9d3c042a88f8f75b168dbb61d`。

证据分别在忽略目录 `tmp/render01-stack-20260920-<run>/`、`tmp/render01-jpg-20260920-<run>/`，包含 page input、probe/media/commit/recovery、PluginData 的限定 host/resource/render/commit 诊断和实际输出。替换栈成功输入是 **`page-input-mapped.json`**，同目录旧 `page-input.json` 不适用。

先前 `378401580f894dc4a574d8ea7774bfb0` 两次本地输入失败保留：原 worker `timing_ms` 和 `bbox` 未映射为 C# `timing`/`bounds`；发生在 Photoshop 提交前，不追认为执行失败或成功。已显式映射 timing、layer/clipping-base bounds 和 pixel contribution bounds，保留全部内容证据。

## 聚焦验证

- JPG-only 实现后 solution build 0 警告/0 错误；Contracts adapter 23/23、Application output/template/builder/factory 53/53；正式插件构建成功，Node 112/112；探针 build 0/0。
- 补充 JPG-only 故障与恢复用例后，solution build 仍 0/0；Infrastructure broker/reservation/commit-store **88/88**。包含验证 PSD 缺失、未 seal、整 hash 错误、独立 worker 拒绝、与正式 PSD 类型互换拒绝；JPG-only 在 Verified、目录移动、数据库完成三个中断点恢复，以及无日志、文件/marker/manifest 损坏和混入非目标文件的拒绝。
- 两种输出模式的真实新进程恢复和源 hash 重查均通过。此前阶段的其他测试计数是历史证据，不冒充本次全量回归。
- 最终 diff/禁止产物检查随本轮后续改动继续执行；不凭测试计数宣布实机故障矩阵或产品门通过。

## 仍开放

生产 owner 注册、受保护配对分发/轮换、多页编排、生产 placement 与旧冻结校准兼容、动态尺码目标/旧表替换/放置合同、页面重生成/覆盖确认、暂停/断线/PS重启/外置盘等实机故障矩阵、输出副本真实编辑与产品视觉确认均未完成。生产 availability 守卫保持；不得将以上两次普通页成功等同 RENDER 或 SIZE 验收。
