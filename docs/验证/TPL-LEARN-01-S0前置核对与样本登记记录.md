# TPL-LEARN-01 S0 前置核对与样本登记记录

## 1. 结论

2026-09-05，`TPL-LEARN-01` 的 LEARN-S0 通过。用户已明确：`新建文件夹.zip` 是该款制作时使用的源照片集合，`奶奶新模板.zip` 是人工填好的 PSD 模板集合。两份原始 ZIP、其中的 PSD、JPG 和照片均只读；检查只使用 Git 忽略目录 `tmp/tpl-learn-s0-user/` 下的隔离副本，没有把媒体、依赖、预览或检查输出加入 Git。

上游 `TPL-01`、`AI-01`、`psd-inspection/v1`、模板修订持久化、`FrameBinding`、`LayoutIntent` 和 renderer-neutral `RenderPlan` 生产者均在基线存在。LEARN-S0 通过只表示真实样本与上游合同已具备，不表示 WPF 人工复核、完整恢复矩阵或 LEARN-S7 已通过。

## 2. 输入登记

为避免在仓库中持久化用户外部目录信息，本记录只登记归档文件名、摘要和 Git 忽略副本的仓库相对位置。

| 角色 | 归档文件 | 长度 | SHA-256 | 隔离副本 |
| --- | --- | ---: | --- | --- |
| 该款源照片 | `新建文件夹.zip` | 51,740,515 | `64993e1230d74dc3c06f05e7b58d4fcb237345e0d61d167b07705f2339776fed` | `tmp/tpl-learn-s0-user/source-archive/新建文件夹/` |
| 人工填好模板 | `奶奶新模板.zip` | 440,185,511 | `4febd3beaca81088af0dc43f4ea28d9f5b1b1829abf2df5640f3ff6d234fa98e` | `tmp/tpl-learn-s0-user/template-archive/奶奶新模板/` |

源照片归档共 11 张图片：

| 文件 | 长度 | SHA-256 |
| --- | ---: | --- |
| `036A7091__9Y2A3650.png` | 8,158,157 | `d562abe0b578293c312b58279c3327691790f5e64f28955b0ef2f98fc53d5d2e` |
| `036A7092__036A7320_9Y2A1616.png` | 8,221,418 | `527751a15a0e3f09fa44d6aed4d9c764978831a35d1a8639bbb6f6bcdf93ab01` |
| `036A7092__036A7320_9Y2A3598.png` | 8,157,012 | `a969d7b91ff27bb0bb72e7164703222564c44004543e6631f0e2b4b4e39871c9` |
| `036A7166__036A7320_9Y2A3558.png` | 7,435,918 | `8a0418634223e1623a5b482cdb4d3f994bdc843a3b500f8ae0c7b68a21cd16a5` |
| `036A7166__036A7320_9Y2A3941.png` | 7,785,772 | `9e1301b89123aefb38c2b31b3fc08ebd5fd252c6bbdbeb29af3233e3a1d7e682` |
| `414deb3ab25f0d01bf68a9d6a758f6e9.png` | 2,915 | `f0b4c5a0a85a71643749a14149d4a03ce3c1aeed4c68b008289e6795f0ae9995` |
| `a732592fe1beb1c75e426cb227bc9ddd.png` | 2,956 | `2bfa4f1164ad8973d90b091f05905fc8e74cc08926d62893e902bd40e4c59c3d` |
| `I36A8876.JPG` | 2,393,954 | `ca0ba5b8f828ad5029aad35e82bf8425edd4bb31e265c5c97f64caddbeb9e835` |
| `I36A8878.JPG` | 2,522,839 | `fb9656b08fa5d2bda83f6ae69922ac6f628415671fcf507348939862c2810e24` |
| `I36A8881.JPG` | 3,500,459 | `7bcd568c0bdd9313b71a0d0f8131379dfddd1e23201122b0f4fc314e4671b549` |
| `I36A8887.JPG` | 3,855,385 | `d397c230d3d96add70475dcc3e0cee19693ce0a455579605c14adae16c57f40b` |

## 3. PSD 只读结构检查

使用仓库现有 `src/PsdInspection.Worker/worker.py` 和临时目录中的 `psd-tools 1.18.0` 对 6 份 PSD 副本执行 `psd-inspection/v1`。所有 PSD 均成功解析；40 个智能对象全部为嵌入式 `data`，未发现外链智能对象。

| PSD | 长度 | 画布 | 层 | 智能对象 | 原始可见智能对象 | 结构候选 | 源照片精确命中 | 结构签名 |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | --- |
| `G-详情-01.psd` | 52,379,519 | 790×1327 | 38 | 4 | 3 | 1 | 1 | `0272731cc2598066fb40828ec1ac1e075bac8a0a258e51dd55074e3dee4a2860` |
| `G-详情-03.psd` | 42,363,495 | 790×1252 | 43 | 3 | 2 | 1 | 1 | `a9e909eb2caa799075d533ab26c7b8f5ac0d1fb62c6a2aeb6cf4f8cc7841333d` |
| `G-详情-04-面料.psd` | 50,405,960 | 790×1182 | 293 | 4 | 3 | 1 | 1 | `7e54bb31c54259bbd04b78ff331382a1b7581ea32b1b2393bbc49c3661285a3f` |
| `G-详情-05-细节.psd` | 79,024,801 | 790×1115 | 50 | 12 | 12 | 4 | 4 | `b7a5bc6dce0174c7e083c7f1997877a7d8367ac04e44808505541e732643703f` |
| `H-模特图-01.psd` | 137,734,782 | 790×2425 | 22 | 8 | 8 | 2 | 2 | `42c8ccb166acf986fe80b5b0a59ce2b086dd25c4626613c01867e2cbcea3cc2e` |
| `H-模特图-02.psd` | 148,519,826 | 790×2546 | 15 | 9 | 8 | 2 | 2 | `e3069d4aa2ee588f1b841a1165e0d087592c764d590f9e8357cb37154747ec81` |

40 个嵌入智能对象中，按嵌入内容 SHA-256 与源照片逐字节精确比对，共 12 个命中；其中 36 个是原始可见智能对象。按连续重叠替换栈折叠后只保留 11 个结构候选，全部有对应源照片命中；`G-详情-05-细节.psd` 路径 `4` 的 239×252 成分图也确实填入图片框，不能仅按尺寸判为固定。其余 25 个可见对象属于被栈顶覆盖的旧替换层，4 个对象隐藏，均不登记为商品图片框。`H-模特图-02.psd` 中另有 1 个命中智能对象不可见，因此不进入结构候选集合。

## 4. 安全与可复现性

- 原始 ZIP、PSD、照片未被写回；只读取长度和 SHA-256，并在隔离副本上解析。
- 临时 Python 依赖仅位于 `tmp/tpl-learn-s0-user/pydeps/`，包括 `psd-tools 1.18.0`、`Pillow 12.3.0`、`numpy 2.2.6`、`attrs 26.1.0` 和 `typing_extensions 4.16.0`。
- 外链智能对象不会被 worker 解引用；只有嵌入式 `data` 内容参与 hash。
- 工作树变更清单不含 ZIP、PSD、JPG、PNG、依赖目录、缓存或生成预览。
- 当前候选实现增加了版本化 `reference-learning/v1`、只读照片 fingerprint、嵌入内容匹配、人工复核状态、会话原子保存、规则投影和参考素材隔离门；这些仍需后续 WPF 入口和完整故障矩阵验收。

## 5. 代码候选验证

执行结果：

- `dotnet build EcommerceDetailPage.Desktop.slnx --no-restore -p:NuGetAudit=false`：0 警告、0 错误。
- 学习服务、提交隔离和 RenderPlan 聚焦通过（含替换栈折叠、成分图人工复核和投影证据）。
- PSD worker 客户端、照片 fingerprint 和 JSON session 聚焦：13/13 通过。
- `dotnet test EcommerceDetailPage.Desktop.slnx --no-build --no-restore -p:NuGetAudit=false --filter 'FullyQualifiedName!~SizeTableGoldSampleTests'`：Domain 49、Contracts 48、Application 159、Infrastructure 213、Architecture 5、WPF UI 15，共 489 通过、2 个显式 opt-in 测试跳过（真实 PSD 样本与外部 AI 探测）、0 失败。
- 一次中间完整复跑中，既有 `RefinementRequestMustRemainClosedOverItsFirstLayerParent` 出现 `AI_SCHEDULER_STATE_STALE_WRITE`；该用例立即聚焦复跑 1/1 通过，随后相同完整命令复跑为上述 471/1/0。该非本轮路径的偶发结果已如实登记，不据此扩展当前任务。
- 无过滤完整 solution 运行：上述 471 项通过、1 项跳过；另 2 个 `SizeTableGoldSampleTests` 因当前 worktree 未携带 Git 忽略的 `size-real-a-*.jpg`/`size-real-b-*.jpg` 返回动态跳过并由 runner 记为失败。本轮未伪造、复制或改写这两张无关媒体，因此无条件完整回归不记为全绿。
- `dotnet format EcommerceDetailPage.Desktop.slnx --no-restore --verify-no-changes --verbosity minimal` 与 `git diff --check`：通过（Git 仅报告既有 LF/CRLF 转换提示）。
- 两份原始 ZIP 在验证后长度和 SHA-256 与登记值一致；`git check-ignore -v` 确认 PSD、照片和临时依赖均由 `**/tmp/` 忽略。

## 6. 阶段状态

| 阶段 | 状态 | 说明 |
| --- | --- | --- |
| LEARN-S0 | 通过 | 用户输入角色、真实样本、安全策略和上游生产者已核对 |
| LEARN-S1 | 代码候选与真实只读检查已完成 | 6/6 PSD 可解析，嵌入内容 hash 已提取；尚未做正式打包 worker 实机链路 |
| LEARN-S2 | 代码候选与真实 hash 比对已完成 | 原始可见智能对象 36 个折叠为 11 个结构候选，全部进入人工复核；25 个旧替换层和 4 个隐藏对象不进入图片框候选 |
| LEARN-S3 | 离线候选 | 本地结构与几何候选已实现；AI 消歧未启用，不影响 fail-closed |
| LEARN-S4 | 未完成 | 尚无正式 WPF 复核入口和产品走查 |
| LEARN-S5 | 离线候选 | 会话 Schema/原子保存、规则投影、模板草稿提交编排和目标款显式选图门已实现；完整 SQLite 集成仍待验收 |
| LEARN-S6 | 部分候选 | 已覆盖部分取消、损坏会话、未完成复核和提交失败；完整恢复矩阵未完成 |
| LEARN-S7 | 未完成 | 尚无完整逐框人工确认、重启恢复、目标款运行和 reviewed diff 证据 |

下一步是在 WPF 人工复核入口中逐框确认 11 个候选（包括成分/水洗唛图位）；若内容应固定，由用户明确拒绝并保留模板内容。只有用户确认后的候选才可进入新模板修订。正式 Photoshop 输出仍由 `RENDER-01` 验收。
