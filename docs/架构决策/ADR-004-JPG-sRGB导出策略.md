# ADR-004：JPG sRGB 导出策略

## 状态

已采用为 UXP `0.2.3` 方案。`0.2.3` 已在改版 PS2025 `26.8.1` 通过远程文件结构、ICC、输入哈希和用户视觉验收，Photoshop 原生嵌入路径生效，TV-07 已通过；TV-13 仍等待一次代表性受控失败隔离证据。

## 日期

首次决策：2026-08-23

最近更新：2026-08-24

## 背景

UXP `0.2.1` 使用 `document.saveAs.jpg(..., { embedColorProfile: true })` 导出。真实产物为 8 位 RGB 且视觉正确，但没有 ICC profile，因此不满足 PRD 16.2 的明确 sRGB 输出要求。

`0.2.2` 在保存可编辑 PSD 后，对未保存的 Photoshop 工作文档调用 `Document.convertProfile("sRGB IEC61966-2.1", ...)`，再用 `embedColorProfile: true` 导出 JPG。该版本通过本地静态预检和打包复验，但在改版 Photoshop 2025 `26.8.1` 实机上生成的 JPG 仍没有 `ICC_PROFILE` APP2 段。远程验收脚本明确返回：

```text
Generated JPG has no ICC_PROFILE APP2 segment.
```

这次失败说明 `embedColorProfile: true` 只能表达嵌入请求，不能作为产物已经嵌入 ICC 的证据。它不证明 JPG 像素内容错误，也不否定 `Document.convertProfile` 本身；但在实际文件没有 ICC 时，其他软件无法从文件中确认应按 sRGB 解释颜色。

Adobe Photoshop UXP 官方文档显示 `Document.convertProfile` 从 Photoshop 23.0 起可用，早于本项目批准的 Photoshop 23.3+ 基线。`constants.Intent.RELATIVECOLORIMETRIC` 从 22.5 起可用。

## 决策

1. renderer-neutral `RenderPlan` 的 JPG 输出继续只声明产品语义：目标配置文件 `sRGB IEC61966-2.1`、相对色度转换、黑点补偿、抖动和要求嵌入配置文件。不得把 UXP 专用的 APP2 补写步骤写入 `RenderPlan`。
2. UXP 适配器先保存可编辑 PSD 副本，再只对当前未保存的 Photoshop 工作文档调用 `Document.convertProfile`。转换完成后必须读回文档颜色配置状态并确认已分配 sRGB，随后才允许导出 JPG。
3. JPG 导出仍向 Photoshop 请求 `embedColorProfile: true`。色彩转换后的工作文档不得保存回输入 PSD，并在 Photoshop 模态阶段结束前始终 `closeWithoutSaving()`。
4. Photoshop 模态阶段结束后，UXP 适配器以二进制读回 JPG 并重组全部 ICC APP2 分段：
   - JPG 没有 ICC 时，插入项目批准的固定 sRGB ICC；
   - JPG 已有完整、有效且可识别为 sRGB 的 ICC 时，保留 Photoshop 原有配置文件，不因字节哈希不同而替换；
   - JPG 的 ICC 不完整、损坏或不是 sRGB 时，明确失败，不静默覆盖或冒充正确结果。
5. ICC 补写只改变 JPEG 元数据段，不重新编码扫描数据或生成新像素。它只能发生在 Photoshop 已成功完成 sRGB 像素转换并通过文档配置状态检查之后；不得用“只打 sRGB 标签”替代失败的颜色转换。
6. 补写或保留后必须再次从磁盘读回 JPG，验证 ICC 分段顺序和完整性、ICC header、RGB 色彩空间、sRGB 身份以及 evidence 一致性。由适配器补写时还必须匹配批准 profile 的 `588` 字节和 SHA-256 `14FB97CEB79AB10B4A72C839A16A334CAC46E7991C6A7F46DE4E56136675920D`。
7. evidence 使用 `uxp-validation-evidence/v2`，并明确区分：
   - `embedding_requested`：向 Photoshop 请求嵌入；
   - `photoshop_embedded_before_postprocess`：Photoshop 原始 JPG 中是否实际存在 ICC；
   - `adapter_injected`：适配器是否补写；
   - `embedding_verified`：最终文件是否通过读回验证；
   - profile 描述、字节数、是否为批准 canonical profile、适用时的 SHA-256；
   - 色彩转换和 ICC 后处理各自耗时。
8. 转换、导出、ICC 读取、补写、写回或最终复核任一步失败时，本页失败并删除本轮创建的 JPG、PSD 和成功 evidence。本 ADR 只定义色彩路径的原子输出契约；TV-13 的代表性失败检查点由统一验证计划确定。
9. ICC 二进制读回、补写和复核属于 UXP 执行适配器总耗时，必须计入绝对批量性能的总机器时间并单独记录。

## 固定 Profile 来源与许可

`0.2.3` 候选实现使用的固定 ICC 不是从 Adobe Photoshop 或 Adobe ICC 文件复制。它由 Pillow `12.3.0` 的 `PIL.ImageCms.createProfile("sRGB")` 调用 LittleCMS `2.19` 生成，并将创建时间归一化以获得可复现字节。profile 描述为 `sRGB built-in`，内嵌版权文字为 `No copyright, use freely`。

可复现生成脚本为 `tools/generate_srgb_icc_profile.py`，第三方来源和许可证记录在 `uxp-validation-plugin/THIRD_PARTY_NOTICES.md`。Photoshop 转换目标名 `sRGB IEC61966-2.1` 与最终固定 profile 的描述字符串必须在 evidence 中分开表达，不能把名称差异伪装成同一字段。

## `0.2.3` 远程实机证据

2026-08-24，`0.2.3` 在改版 Photoshop 2025 `26.8.1` 上执行后，远程 verifier 返回 `passed: true`：

- 输入 PSD SHA-256：`B94E7458A33F645E322F30EFDE675CCB268BE5A72CA2FF4B7618F241EED14A35`；
- 输入源 JPG SHA-256：`04720404A97FBE6DFD6EB138946C129545F37020F81346D98AA0616668C0B0DB`；
- 输出 PSD SHA-256：`DD997E7F47B8792744C4EBF57261A5109B50D195BEDFCC8B89FE836DCE7F6949`；
- 输出 JPG SHA-256：`D18AD1D8389289E06D9F0A66E544DA15D85F181F9D49F2229BA410A9245180A4`；
- evidence SHA-256：`358EE8A940A6217BB7A8EF815D2246EEAC3BC3EEABE8E2151E3674E9603CFB0F`；
- 目标 profile：`sRGB IEC61966-2.1`；
- `photoshop_embedded_before_postprocess: true`；
- `adapter_injected: false`；
- `embedding_verified: true`；
- 实际 ICC：`3144` 字节，SHA-256 `2B3AA1645779A9E634744FAF9B01E9102B0C9B88FD6DECED7934DF86B949AF7E`。

这证明本次运行由 Photoshop 原生写入了完整有效的 sRGB ICC，适配器正确保留了非 canonical 但有效的原生 profile，没有替换为本地 `588` 字节 profile。用户回传 JPG、PSD 和 evidence 后，主开发机重算的三项哈希与 verifier 完全一致；用户确认最终 JPG 肉眼无问题，画面未见白边、旧商品、错向或固定内容异常，TV-07 已通过。`0.2.3` 的 fallback 补写路径在本次实机运行中没有触发。

## 影响

- 可编辑 PSD 在 JPG 色彩转换前保存，不会因交付 JPG 的 sRGB 要求改变 PSD 的颜色配置或图层可编辑性。
- `0.2.1` 和 `0.2.2` 的无 ICC JPG 保留为历史证据，不能用于 TV-07 通过结论。
- `0.2.2` 的失败是该具体改版 PS2025 `26.8.1` 构建上的实测兼容行为，不自动外推到其他 Photoshop 构建。
- `0.2.3` 已通过本地验证、远程文件结构、ICC、输入哈希和用户视觉验收，TV-07 已关闭。TV-13 按一次代表性受控失败检查结构化定位、输入安全和输出清理。
- 后处理仍位于 Photoshop 执行适配器内部，不引入 AI、业务规则或 UXP 专用 `RenderPlan` 语义，也不要求 Adobe 登录或云端 API。

## 官方与本地依据

- [Adobe Photoshop UXP Document.convertProfile](https://developer.adobe.com/photoshop/uxp/2022/ps-reference/classes/document#convertprofile)
- [Adobe Photoshop UXP constants.Intent](https://developer.adobe.com/photoshop/uxp/2022/ps-reference/modules/constants#intent)
- `tools/generate_srgb_icc_profile.py`
- `uxp-validation-plugin/THIRD_PARTY_NOTICES.md`
