# V1 D4：Photoshop环境检测与诊断实机验证

日期：2026-09-24。结论：**只读环境检测与历史数字版本诊断快照限定门通过；不是正式宿主选择、认证连接或完整逐机能力验收。**

## 实现与边界

- 按[V1执行计划D4](../任务/V1交付执行计划-20260922.md)、PRD§15.2、SET-01、交互§6.11及ADR-011，新增窄`IPhotoshopEnvironmentProbe`，读取Photoshop进程路径、HKCU/HKLM两注册表视图的App Paths/Adobe登记及有限Adobe安装目录；最多128候选，不全盘搜索。支持手动选择本地Photoshop.exe补充检测。
- 只读取文件版本资源，不启动/操控/连接PS、不读取文档/凭据/业务DB。拒绝网络、设备、ADS、重解析和非Photoshop程序路径；来源失败隔离，结果截断/不完整明确提示。单飞、取消、失败保留旧快照、关闭/离页取消；快照只有进程内生命周期，列表选择只看详情。
- 文件原始版本显示文本与数字四段版本分列；26.8.1只标版本候选，Windows客户端/x64/版本门也不替代客户机验收。运行进程不等于连接，已配对不等于实际构建/完整能力通过；未接入的正式宿主选择、独立连接测试和逐机探针明确说明。
- 诊断增加`photoshop_environment`历史快照，仅包含检测时间、OS候选布尔、数字版本、运行布尔和固定来源枚举，最多16条；无路径/版本自由文本。导出只读`Latest`，不触发检测；认证宿主完整构建仍用`photoshop_version=null/not_collected`。没有SQLite迁移或Bridge/RenderPlan改动。

## 自动化与审查

- 最终solution build：0警告/0错误。首次发现CA1826/CA1859及测试CA1861，随后修复；第二次测试编译CS8752、xUnit2031/2012也已修复。
- Infrastructure过滤`PhotoshopEnvironmentProbeTests|DiagnosticPackageExporterTests|DesktopServicesTests`：46通过、1跳过、0失败。覆盖非C盘、来源合并、顺序/上限、非法路径、部分失败、取消/单飞/迟到、旧快照不被失败覆盖、真实错误exe元数据拒绝、DI以及诊断白名单和既有导出边界。
- 跳过项是符号链接创建：本机无该权限，xUnit旧runner又把dynamic skip作为失败。明确改为静态Skip，不改系统权限、不计为通过；实际junction拒绝另有下述正式UI证据，不能将其冒充符号链接单元测试已绿。
- UI过滤`PhotoshopEnvironmentViewModelTests|DiagnosticExportViewModelTests|WpfShellTests.Settings|PhotoshopPairingViewModelTests|SettingsPageStateTests`：27/27。覆盖无自动检测、取消文件选择、错误脱敏、历史状态、单飞/关闭/迟到、选择只看详情、1440×900与1100×760布局/UIA。
- Architecture `DependencyRulesTests`：26/26。后续仅修Infrastructure测试，没有无变化重跑已绿UI/架构。差异复核及全局/新增文件空白检查通过，其他既有修改保留。
- 两个Sol/max子任务分别负责诊断白名单集成和检测测试；主线程集成、统一构建并独占UI，无并行PS或凭据操作。两子任务已关闭。

## 正式入口实机证据

入口是正式应用代码的`src/Desktop.App/bin/Debug/net10.0-windows/win-x64/Desktop.App.exe`，**不是交付安装包**。证据根`tmp/v1-d4-photoshop-environment-20260924`，所有证据新建，不覆盖D3。

1. 冷启动进入Photoshop分区为“尚未检测”“尚无版本快照”“尚未检查设备配对”，没有自动检测或连接。见`cold-start-uia.txt`。
2. 主动重新检测找到2个程序，2026-09-24 23:03:25快照：
   - G盘Photoshop2025：文件显示`26.8 (20250624.r.8 5e0d05d)`，数字`26.8.1.8`，来源运行进程/安装登记，标候选且不宣称可生产。
   - C盘CC2019：文件显示`20.0 (20191122.r.92 2019/11/22: 1208122)`，数字`20.0.8.28474`，来源登记/Adobe目录，未观察到对应运行进程。切换详情明确“不在当前首发支持版本范围；不是普通连接失败”，没有启动CC2019。
   - 本机Windows数字版本`10.0.26200.0`/X64，与Windows11候选文案分列。真实文件元数据及exe hash独立留存在`before-detection.json`、`second-installation.json`；并非把配置中的批准构建当检测值。
3. 程序选择对话框Escape取消：无新检测，23:03:25快照不变，见`cancelled-picker-uia.txt`。手动提供自有junction路径被拒，旧2条快照和时间不变。先用错误exe副本验证失败反馈，再用与真实PS exe SHA256完全相同的隔离副本和自有junction验证拒绝，见`valid-junction-fixture.json`、`valid-reparse-rejected-uia.txt`。未执行任何副本；junction目标均在本次tmp根内，不指向用户源目录。
4. 通过原生系统菜单缩到最小窗口，实际可见截图1086×753（不混同1100×760外框），程序版本、路径、失败提示、检测/配对按钮和底部说明可读，无重叠；必要区域可滚动。见`minimum-window-uia.txt`。直接拖拽未改变尺寸，不将那张大窗截图冒充最小窗。
5. 在最小窗口主动导出新ZIP，170条技术记录、0省略、未截断，状态明确是历史元数据而非连接证明，见`export-success-uia.txt`。独立脚本检查固定3条目、manifest全部键和枚举、两数字版本/来源/运行状态、无安装路径/版本自由文本/媒体/业务DB/Key内容，未读取真实凭据。
6. ZIP为5122字节，SHA256 `bcc623df25074485dd9fe43b4d165aba88910d8ccf57bb629fd748f83c6597c4`。`after-export.json`、`after-close.json`、`after-restart.json`一致，源技术日志不变、自有导出临时文件0。曾因PowerShell自动把JSON时间转成DateTime导致字符串比较误报进程变化，改用UTC ticks后核实PS PID/启动时间/exe hash全不变；没有修改产品或重启PS。
7. 正常退出并确认进程消失，再启动同一构建：重新进入Photoshop为未检测/无快照/未检查配对，没有复用旧快照伪装在线，见`restart-environment-uia.txt`。未再次扫描或导出。
8. `tmp/repair-r1-20260924/before-d4-ui.json`、`after-d4-export.json`、`after-d4-restart.json`通过：70保护文件、10业务表、旧迁移/v17备份不变，schema18、repair请求0、integrity=ok。无AI/渲染/Unknown重投。

构建指纹见`build-fingerprints.json`：App.dll `2B3657197105B7B2CA516B118DE8D43C4DE19DEAA09AFEFDD175567F57880BEF`，Infrastructure.dll `976EC79926A5DD171DA9DF5839DA94345CCE9A12A07AFF561B16C320C2951611`。

## 下一门

继续SET中独立、受时限控制的认证连接检查，先核查现有配对/Bridge生命周期与版本合同，不把一次连接/ready冒充完整逐机安装、渲染、ICC和恢复探针。正式宿主选择仍需与实际认证宿主绑定，不把当前下拉详情当成已经实现。存储/清理/迁移、R2～R6、完整UI/FLOW、10款性能和安装/试点仍开放。R1d等待有依据尺码更正；本轮没有提交、推送、发布或改变授权。
