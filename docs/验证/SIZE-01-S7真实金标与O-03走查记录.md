# SIZE-01 SIZE-S7 真实金标与 O-03 走查记录

## 1. 当前结论

2026-09-02 已完成两张真实市场尺码图的正式逐格回归，以及动态尺码页 O-03 编辑弹层的自动化 WPF 窗口走查候选。

- 真实金标：2/2 通过；逐格结构与人工核对一致，原文 `/`、小数和整数均保留；来源文件 SHA-256 与 SIZE-S0 收口值一致。
- O-03 窗口候选：1/1 通过；弹层在 1440×900 与 1100×760 下边界闭合，来源参考/结构化表格/模板样式预览均可见；合法 `\\` 可编辑，真正空值禁用保存。
- 本记录不把自动化窗口测试写成产品负责人视觉签字；125% DPI 实机确认、中文 IME/键盘/UIA 的动态弹层专项走查，以及真实模板 JPG/PSD 联合验收仍开放。

因此 `SIZE-01` **仍未整体通过**。

## 2. 真实金标逐格回归

执行测试：

```text
dotnet test tests/Desktop.Application.Tests/Desktop.Application.Tests.csproj --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~SizeTableGoldSampleTests" --verbosity minimal
结果：2 通过、0 失败、0 跳过。
```

测试读取 Git 忽略目录中的真实文件，先以 SHA-256 校验字节未变，再把人工逐格核对结果送入正式 `SizeTableService`。测试不上传、复制、改写或提交图片。

### 样本 A

文件：`需求样本/SIZE-01金标/真实来源/size-real-a-577fa4d19f919a7a.jpg`

- 长度：98,392 bytes
- SHA-256：`577fa4d19f919a7a7bbd64f07daa1102a34642c470e0967364f44543271a9e2b`
- 列：`尺码 | 衣长 | 胸围 | 肩宽 | 袖长`
- 行：
  - `L | 61 | 102 | 40 | /`
  - `XL | 62 | 107 | 41 | /`
  - `2XL | 63 | 112 | 42 | /`
  - `3XL | 64 | 117 | 43 | /`
  - `4XL | 65 | 122 | 44 | /`
  - `5XL | 66 | 127 | 45 | /`

### 样本 B

文件：`需求样本/SIZE-01金标/真实来源/size-real-b-9231ad299120bba6.jpg`

- 长度：239,126 bytes
- SHA-256：`9231ad299120bba65af9391ba8a59097b7ff169197810a38971621df886d9fb0`
- 列：`尺码 | 胸围 | 肩宽 | 袖长 | 衣长 | 袖口`
- 行：
  - `L | 99 | 39.5 | 51.5 | 64 | 28`
  - `XL | 103 | 40.5 | 52.5 | 65 | 29`
  - `2XL | 107 | 41.5 | 53.5 | 66 | 30`
  - `3XL | 111 | 42.5 | 54.5 | 67 | 31`
  - `4XL | 115 | 43.5 | 55.5 | 68 | 32`
  - `5XL | 119 | 44.5 | 56 | 68.5 | 33`

实现证据见 `tests/Desktop.Application.Tests/SizeTableGoldSampleTests.cs`。这项测试验证的是“真实来源 hash + 已核对结构进入正式领域/应用合同”；它不是 OCR 准确率或真实 provider 请求证据。

## 3. O-03 WPF 窗口走查候选

执行测试：

```text
dotnet test tests/Desktop.App.UiTests/Desktop.App.UiTests.csproj --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~DynamicSizeTableEditorRendersSourceGridAndTemplatePreviewAtWideAndNarrowSizes" --verbosity minimal
结果：1 通过、0 失败、0 跳过；实际耗时约 11 秒（测试主体约 7 秒）。
```

覆盖内容：

- 从动态尺码页结果详情打开“编辑尺码数据”；
- 来源只读预览、结构化表格、模板样式预览和保存按钮均存在且可见；
- 第一张真实金标的 `/` 原样呈现；改为 `\\` 后保存按钮可用；改回真正空字符串后保存按钮禁用并显示不能保存；
- 弹层在 1440×900 和 1100×760 下均未越出窗口边界；
- 当前测试环境实际 DPI 由 WPF `VisualTreeHelper.GetDpi` 读回，未通过修改系统缩放设置制造 125% 环境。

既有 Shell 自动化还覆盖当前环境的键盘焦点、中文输入和 UIA；本轮以 `DESKTOP_EXPECTED_DPI_PERCENT=100` 复跑 `ShellSupportsWideNarrowUiaFocusAndChineseText`，结果 1/1 通过。动态尺码弹层在 125% DPI 下的实机专项和产品负责人视觉确认尚未完成，不能把本候选写成完整 S7 通过。

## 4. 未关闭的 S7 门

1. 产品负责人对 O-03 实际弹层的视觉确认，包含 1440×900、1100×760、100%/125% DPI、中文 IME、键盘和 UIA；
2. 以真实已校准模板和 Photoshop/UXP v5 执行真实动态尺码页，确认 JPG/PSD 原子输出和失败不覆盖旧输出；
3. 完成 reviewed diff、上游文件安全审查和最终验收矩阵同步。

在上述证据齐全前，保持验收矩阵中的 `SIZE-01` 为“进行中”。

## 5. 本轮完整回归

```text
dotnet build EcommerceDetailPage.Desktop.slnx --no-restore -p:NuGetAudit=false --verbosity minimal
结果：0 警告、0 错误。

dotnet test EcommerceDetailPage.Desktop.slnx --no-restore -p:NuGetAudit=false --verbosity minimal
结果：456 通过、1 跳过、0 失败；跳过项为默认关闭的 opt-in RealProbeThroughFormalAdapter，本轮未联网。

dotnet format EcommerceDetailPage.Desktop.slnx --no-restore --verify-no-changes --verbosity minimal
git diff --check
结果：均通过。

## 6. 2026-09-03 外部联合验收条件复核

只读检查发现一份真实尺码表模板 PSD 候选：

```text
C:\Users\79122\Documents\ChatGPT\全新实现\需求样本\第一组\模板PSD\F-尺码表-【L-5XL】.psd
bytes: 4,151,145
SHA-256: 6CFF7EB29E1929E2294338AA6E05CF4B0520A45DF63A8718350F632ADEB81E05
```

本机当前 `Photoshop` 进程不存在，常见 `Program Files`/Adobe 安装目录和 `Photoshop*.exe` 命令均未发现可执行文件。因此本轮没有打开或改写 PSD，没有生成 JPG/PSD 输出，也没有执行 Photoshop/UXP 联合验收。该外部条件是 SIZE-01 完成门的剩余阻塞，不以自动化测试或模板文件存在替代。
```
