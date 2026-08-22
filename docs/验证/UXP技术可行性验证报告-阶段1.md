# UXP 技术可行性验证报告（阶段 1：本地设计与预检）

## 结论

阶段 1 仅证明验证资产和安全边界已经具备，尚未证明 UXP 在 Photoshop 2022 实机上可安装、连接、置入、导出或达到速度门槛。生产执行通道保持未冻结，当前建议为“继续 UXP 实机验证”，不是“采用 UXP”。

## 已执行证据

| 日期 | 命令或检查 | 结果 | 证据 |
| --- | --- | --- | --- |
| 2026-08-22 | `git status --short --branch` | 无提交；PRD、治理文件和样本均未跟踪 | 工作区状态；`.gitignore` 已建立安全忽略规则 |
| 2026-08-22 | 解析 inspection-report 与 manifest-skeleton | 找到单图片框页 `G-详情-03` 和四图片框页 `G-详情-05-细节`；确认 `G-详情-01` 与 backup 哈希相同 | `需求样本/第一组/模板PSD/output/_inspection/*.json`（本地输入，不纳入基线） |
| 2026-08-22 | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/validate_uxp_prototype.ps1` | PASS：Manifest v5、PS 23.3.0 下限、权限、样例计划和 5 个绑定框均通过 | `tools/validate_uxp_prototype.ps1`、`uxp-validation-plugin/` |
| 2026-08-22 | bundled Node `--check uxp-validation-plugin/main.js` | PASS：插件 JavaScript 语法有效 | `uxp-validation-plugin/main.js` |
| 2026-08-22 | 启动 `tools/uxp_validation_bridge.ps1` 后向 `http://127.0.0.1:17842/handshake` 发送协议探针 | PASS：返回 200、`render-plan/v1` 和会话 ID；接受一次握手后退出 | `tools/uxp_validation_bridge.ps1`（PowerShell 到 PowerShell；UXP 内调用待实机） |
| 2026-08-22 | `git switch -c codex/uxp-validation-v1` | 初次受沙箱权限阻止；获得 Git 元数据写入批准后成功创建验证分支 | `git branch --show-current` |

## 当前验收矩阵状态

| ID | 阶段 1 状态 | 说明 |
| --- | --- | --- |
| TV-01 | 阻塞 | 缺少 Photoshop 实机和用户安装动作 |
| TV-02 | 进行中 | Manifest v5 和 `23.3.0` 候选已静态校验，运行时版本/API 尚未盘点 |
| TV-03 | 进行中 | 审计记录了源文件哈希；尚无副本运行前后对照 |
| TV-04 | 进行中 | 单框和四框绑定目标及结构证据已进入 RenderPlan，实机读取待测 |
| TV-05 至 TV-13 | 阻塞 | 需要 Photoshop 实机、模板副本和 JSX 同机基准 |

## 安全基线策略

样本目录、PSD/PSB、`output`、缓存、日志、UXP 打包文件和凭据已加入 `.gitignore`。首个提交白名单是 `AGENTS.md`、`README.md`、PRD、`docs/`、`uxp-validation-plugin/`、`tools/` 和 `.gitignore`，不包含 `需求样本/`。验证分支为 `codex/uxp-validation-v1`；提交前执行白名单暂存、`git diff --cached --check` 和凭据/大文件复核。

## 实机阻塞与下一动作

需要用户协助通过 UU 远程提供：Photoshop 精确版本和 Windows/硬件信息；确认可使用一个副本模板和一个副本源图；在 Photoshop 2022 中安装 UXP Developer Tool 并加载 `uxp-validation-plugin`。首个实机动作只做只读 probe 和握手，确认后才允许在副本上开启渲染适配器。
