# UXP 技术可行性验证报告（阶段 1：本地设计与预检）

## 结论

本报告只记录阶段 1 的证据边界：验证资产和安全边界已具备，指定改版 Photoshop 2025 `26.8.1` 可以加载插件、执行基础 Photoshop API、连接同机 bridge 并读取 RenderPlan。单框渲染及其后的实机结论统一以阶段 2 报告、验收矩阵和当前项目状态为准。生产执行通道仍未冻结。

## 已执行证据

| 日期 | 命令或检查 | 结果 | 证据 |
| --- | --- | --- | --- |
| 2026-08-22 | `git status --short --branch` | 无提交；PRD、治理文件和样本均未跟踪 | 工作区状态；`.gitignore` 已建立安全忽略规则 |
| 2026-08-22 | 解析 inspection-report 与 manifest-skeleton | 找到单图片框页 `G-详情-03` 和四图片框页 `G-详情-05-细节`；确认 `G-详情-01` 与 backup 哈希相同 | `需求样本/第一组/模板PSD/output/_inspection/*.json`（本地输入，不纳入基线） |
| 2026-08-22 | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/validate_uxp_prototype.ps1` | PASS：Manifest v5、PS 23.3.0 下限、权限、样例计划和 5 个绑定框均通过 | `tools/validate_uxp_prototype.ps1`、`uxp-validation-plugin/` |
| 2026-08-22 | bundled Node `--check uxp-validation-plugin/main.js` | PASS：插件 JavaScript 语法有效 | `uxp-validation-plugin/main.js` |
| 2026-08-22 | 启动 `tools/uxp_validation_bridge.ps1` 后向 `http://127.0.0.1:17842/handshake` 发送协议探针 | PASS：返回 200、`render-plan/v1` 和会话 ID；接受一次握手后退出 | `tools/uxp_validation_bridge.ps1`（PowerShell 到 PowerShell；UXP 内调用待实机） |
| 2026-08-22 | `git switch -c codex/uxp-validation-v1` | 初次受沙箱权限阻止；获得 Git 元数据写入批准后成功创建验证分支 | `git branch --show-current` |
| 2026-08-22 | 白名单暂存、`git diff --cached --check`、凭据/大文件复核和 root commit | PASS：首个基线提交完成，样本未被跟踪 | `4ad6be211030c9ad82bc7f5dbfc79caeb5e39c59` |
| 2026-08-23 | 用户提供 Photoshop 系统信息 | PASS：记录 PS `26.8.1`、Windows 11、约 32 GB 内存、RTX 4060 Ti；发现 UXP `8.3.0` | `docs/验证/Photoshop实机环境盘点.md`；仅为环境盘点，不是插件能力通过 |
| 2026-08-23 | 启动 Adobe UXP Developer Tools | BLOCKED：工具已安装并启动，但提示 Creative Cloud 未登录或邮箱未验证；不是 Photoshop API 或插件加载结果 | `docs/验证/Photoshop实机环境盘点.md`；需完成同一 Creative Cloud 账号的登录/邮箱验证后重启工具 |
| 2026-08-23 | 加载 `local.ecommerce.uxp-validation` 并点击 `Record Photoshop probe` | PARTIAL：插件状态为 Loaded，Photoshop 收到 API 调用；因没有活动文档返回“命令‘获取’当前不可用” | `docs/验证/Photoshop实机环境盘点.md`；打开空白文档后重试 |
| 2026-08-23 | 打开空白文档后再次点击 `Record Photoshop probe` | PASS（只读 probe）：面板显示 `Probe recorded`，返回 `ok: true`；版本字段为空但宿主已执行文档读取和证据写入 | `docs/验证/Photoshop实机环境盘点.md`；精确版本以用户系统信息为准 |
| 2026-08-23 | 启动 loopback bridge 并点击 `Handshake with desktop bridge` | FAIL（可定位）：UXP 返回 `BRIDGE_UNAVAILABLE` 和 `Permission denied / Manifest entry not found`，bridge 未收到请求；多次 Reload 后仍失败。已准备 `0.1.1` 全域网络权限诊断版并增加 Console 错误记录，待覆盖实测 | `docs/验证/Photoshop实机环境盘点.md` |
| 2026-08-23 | 清洁解压并加载 `0.1.1`，重新运行 probe 与 bridge 握手 | PASS：插件 Loaded；probe 返回 `ok: true`；面板显示 `Bridge connected`；PowerShell 返回 `POST /handshake -> 200 OK` 并在一次成功握手后正常退出 | `docs/验证/Photoshop实机环境盘点.md`；`uxp-validation-probe-0.1.1.zip` |
| 2026-08-23 | 面板加载旧两页 RenderPlan 样例 | PASS（仅协议）：`ok: true`、`pages: 2`；未调用渲染适配器 | 用户截图；`docs/验证/Photoshop实机环境盘点.md` |
| 2026-08-23 | `0.2.0` 单框执行器与交接包本地预检 | PASS（本地）：JS 语法、Manifest、单框计划、fixture 哈希和 ZIP 重解压通过；Photoshop 换图待实机 | `docs/验证/UXP技术可行性验证报告-阶段2-单框执行准备.md` |

## 阶段 1 验收快照

| ID | 阶段 1 状态 | 说明 |
| --- | --- | --- |
| TV-01 | 通过 | 改版 PS 2025 `26.8.1` 清洁环境已复现插件加载、启动和同机 bridge 握手 |
| TV-02 | 进行中 | Manifest v5、`23.3.0` 下限已静态校验，实机发现 UXP 8.3；只读 Photoshop API probe 通过，智能对象和导出 API 尚未验证 |
| TV-03 | 进行中 | 审计记录了源文件哈希；尚无副本运行前后对照 |
| TV-04 | 进行中 | 单框和四框绑定目标及结构证据已进入 RenderPlan，实机读取待测 |
| TV-05 至 TV-08、TV-11 至 TV-12、TV-14 | 待验证 | `0.2.0` 在替换前因路径顺序错误安全中止；需先完成单框能力，再扩展 PSD 可编辑性、复杂页、整款和绝对批量性能 |
| TV-13 | 进行中 | `0.2.0` 已返回结构化定位和清理标记；远程目录与运行后哈希仍待独立核对 |

## 安全基线策略

样本目录、PSD/PSB、`output`、缓存、日志、UXP 打包文件和凭据已加入 `.gitignore`。首个提交白名单是 `AGENTS.md`、`README.md`、PRD、`docs/`、`uxp-validation-plugin/`、`tools/` 和 `.gitignore`，不包含 `需求样本/`。验证分支为 `codex/uxp-validation-v1`；提交 `4ad6be211030c9ad82bc7f5dbfc79caeb5e39c59` 已通过暂存区空白、凭据和大文件复核。

## 阶段 1 交接

协议加载已通过；`0.2.0` 单框实机因跨工具图层顺序错误安全中止，`0.2.1` 已按 ADR-003 修复并进入阶段 2 实机验证。该改版环境的证据只代表此具体构建，不能外推到正版或其他改版版本。当前后续动作以 `docs/当前项目状态.md` 和活动任务书为准。
