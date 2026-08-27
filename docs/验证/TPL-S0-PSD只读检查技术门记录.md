# TPL-S0 PSD 只读检查技术门记录

## 结论

2026-08-27，TPL-S0 **通过**。正式 adapter 采用隔离的本地 `psd-tools 1.18.0` worker；进程内 .NET 候选因结构一致性、评估预览、资源和许可条件未全部满足而拒绝；Photoshop/UXP 只保留为显式兼容性探针。决策见 `ADR-017`。

## 环境与输入安全

- Windows 11 x64 开发机，`.NET SDK 10.0.400`，Python `3.10.11`，PyInstaller `6.16.0`。
- 起点为 `217ec0b docs: approve TPL-01 implementation brief`。
- 验证仅复制 `需求样本/第一组/模板PSD` 的 8 个批准 PSD、4 张批准源照片和 1 张人工参考到工作区 `tmp/tpl-s0-validation`。所有源文件复制前后 SHA-256 不变；损坏注入只截断内部副本。
- `tmp`、PSD/JPG、预览、JSON 运行结果、虚拟环境和 PyInstaller 制品均被 Git 忽略，不作为提交内容。

## 候选比较

| 候选 | 结构/预览 | 许可与资源 | 结果 |
| --- | --- | --- | --- |
| A `PhotoshopFile 1.0.0` | NuGet 联网查询确认包不存在，返回 `NU1101` | 无可验证制品 | 不采用 |
| A 替代 `Aspose.PSD 26.8.0` | 能命中 G05 路径 `3/7/11/15` 的 ID `541/540/539/538`；但 G03 展开为 `63` 层而批准结果为 `42`，G05 展开为 `65` 层而批准结果为 `46`；未授权预览带评估水印 | 商业 EULA；整批峰值约 `496.7 MB` | 不采用；不手写层树重建 |
| B `psd-tools 1.18.0` worker | 8 页全部成功；G03 `42` 层、路径 `2`/ID `257`；G05 `46` 层且四个目标结构、剪贴基底和边界全部一致 | MIT；单请求最大峰值 `184,164,352` bytes；onedir `58,631,966` bytes | 采用 |
| C Photoshop 2025 / UXP v5 | 既有真实结构、智能对象替换和准确 JPG/PSD 证据 | 占用 Photoshop | 仅作兼容性与准确预览对照 |

## 正式 worker 结构证据

| 页面/目标 | worker 结果 | 批准对照 |
| --- | --- | --- |
| G03 | `42` 层；路径 `2`；ID `257`；`back_main`；签名 `27217074B5CEC992916C87510E016027B2A4C15EADF42CCE594ABB7E67F373DF` | 一致 |
| G05 care label | 路径 `3`；ID `541`；剪贴基底路径 `1`；边界 `[406,698,766,985]` | 一致 |
| G05 collar | 路径 `7`；ID `540`；剪贴基底路径 `5`；边界 `[23,693,387,980]` | 一致 |
| G05 sleeve | 路径 `11`；ID `539`；剪贴基底路径 `9`；边界 `[443,276,765,510]` | 一致 |
| G05 front | 路径 `15`；ID `538`；剪贴基底路径 `13`；边界 `[23,188,422,672]` | 一致 |

G05 结构签名保持 `F794AC3B7F27CBC68A474A20314582457D6BA641158D00081D4F91AD07D37368`。8 个 PSD 的一请求一进程解析总耗时为 `7,251.404 ms`；每个副本前后 SHA-256 均相同。打包 worker 与 .NET client 真实端到端返回 `OK=True`、`LAYERS=46`，四个目标全部映射成功。

## 本地校准预览门

`tools/tpl/validate_local_calibration_preview.py` 使用打包 worker 的合成图/剪贴基底证据、批准 RenderPlan 和明确复制的 4 张源图构造 WPF 等价 cover 覆盖层，并与批准 Photoshop 人工参考比较：

- 四个 worker 基底边界逐项等于 RenderPlan `registered_frame`，基底均无用户蒙版或矢量蒙版；本轮不猜蒙版。
- 默认 center cover 的框内平均绝对差异分别为 `2.5573%`、`2.1769%`、`2.7950%`、`2.2114%`。
- `scale=1.15`、`offset=(12,-12)` 探针四框全部覆盖，无空白边，焦点/目标点换算误差为 `0` 至 `0.5 px`。
- PSD 副本 SHA-256 前后均为 `69BF44A64E47CB8D6817FA8589888163B14AB08A19A0BCFCEE02352C3392F5EF`；人工参考副本 SHA-256 为 `1377264FB8A2A8AF9F525413BE57A8D0B2FDDE01F8263662B4F3CFFA58F232F1`。
- 本地预览用于校准意图和几何，不冒充 Photoshop 准确预览；遇到无法表达的蒙版/变换必须阻断或要求兼容性检查。

## 负例、隔离与发布证据

- 截断 PSD：`TPL_PSD_UNSUPPORTED_STRUCTURE`，不可重试。
- 未知协议：`TPL_PSD_PROTOCOL_UNSUPPORTED`；非法 JSON：`TPL_PSD_REQUEST_INVALID`。
- .NET client 的应用根外路径、缺少内部副本、worker 缺失、畸形 `bbox`、超时和响应超限共 6 项聚焦测试全部通过；异常不再越过 adapter 成为未处理 CLR 弹框。
- PyInstaller onedir：`102` 个文件、`58,631,966` bytes，EXE SHA-256 `112D411992B95B6A988E641B9D2FB1643618C134A549B1EED56EC82F7F07C99A`，不依赖客户机 Python。
- 沙箱内 PyInstaller 两次因用户级 Python 目录读取权限失败，按停止规则不再换参数；经用户人工批准在沙箱外执行同一重建命令后成功。该事件不是 PSD 技术失败，也未访问或修改用户源文件。

## 实际验证

```powershell
dotnet restore EcommerceDetailPage.Desktop.slnx --force-evaluate
dotnet build EcommerceDetailPage.Desktop.slnx --no-restore
dotnet test EcommerceDetailPage.Desktop.slnx --no-build --no-restore
python -m compileall -q src/PsdInspection.Worker/worker.py tools/tpl/validate_local_calibration_preview.py
pyinstaller --noconfirm --clean --distpath tmp/tpl-s0-validation/publish/worker --workpath tmp/tpl-s0-validation/pyinstaller-work src/PsdInspection.Worker/psd-inspection-worker.spec
python tools/tpl/validate_local_calibration_preview.py <批准副本参数>
```

NuGet restore 与漏洞索引联网检查经用户人工批准后成功，11 个项目全部恢复，无 `NU1301`、`NU1900` 或漏洞告警。solution 构建 `0` 警告、`0` 错误；共 `50` 项测试通过（Domain 16、Application 3、Contracts 8、Infrastructure 15、Architecture 5、WPF UI 3）。
