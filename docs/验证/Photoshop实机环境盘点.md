# Photoshop 实机环境盘点

本记录只在用户通过 UU 远程协助或明确确认后填写。首次连接只读，不安装、升级或修改 Photoshop。若 Photoshop 精确版本低于 23.3，记录差距后停止 UXP 安装验证。

## 机器信息

| 项目 | 实测值 | 证据方式 |
| --- | --- | --- |
| 记录时间（Asia/Shanghai） | 待填写 | 系统时间截图或命令输出 |
| Windows 版本与构建号 | 待填写 | `winver` 或系统信息 |
| CPU | 待填写 | 任务管理器或系统信息 |
| 内存 | 待填写 | 任务管理器或系统信息 |
| GPU 与驱动 | 待填写 | Photoshop 系统信息 |
| 系统盘/样本盘/输出盘类型 | 待填写 | 磁盘型号和 SSD/HDD 类型 |
| Photoshop 暂存盘 | 待填写 | Photoshop 首选项 |
| Photoshop 精确版本 | 待填写 | 帮助 > 系统信息 |
| UXP 运行时/Developer Tool 版本 | 待填写 | Developer Tool 或插件 probe |
| JSX Demo 标识 | 待填写 | 压缩包哈希和版本 |

## 操作确认

| 项目 | 状态 | 备注 |
| --- | --- | --- |
| 用户已确认可以加载验证插件 | 待确认 | 不等同于正式安装方案 |
| 实验模板是源 PSD 的明确副本 | 待确认 | 记录副本绝对路径 |
| 实验源图是源素材的明确副本或只读引用 | 待确认 | 记录路径与 SHA-256 |
| 临时与正式输出目录分离 | 待确认 | 正式输出必须先写临时名再原子发布 |
| 原始 PSD/素材运行前 SHA-256 已记录 | 待确认 | 运行后再次计算 |

## 首次实机顺序

1. 记录上述环境，不做安装或升级。
2. 运行 `tools/validate_uxp_prototype.ps1`。
3. 启动 `tools/uxp_validation_bridge.ps1`，只监听 loopback。
4. 通过 UXP Developer Tool 加载 `uxp-validation-plugin`。
5. 打开一个无修改的测试副本，点击 `Record Photoshop probe`。
6. 点击 `Handshake with desktop bridge`，保存双方输出。
7. 在用户再次确认副本路径后，才进入图片框读取和渲染动作实现。

## 实测结果

待实机填写。每个失败必须记录插件版本、Photoshop 精确版本、页面、图片框、操作、错误码和是否产生临时输出。
