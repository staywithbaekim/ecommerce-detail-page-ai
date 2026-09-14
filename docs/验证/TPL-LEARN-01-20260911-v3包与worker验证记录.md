# TPL-LEARN-01 v3 测试包与 PyInstaller worker 验证记录

**日期：** 2026-09-11  
**范围：** 复核 `reference-learning-semantic-prompt/v3` 后的最新源码，补齐 BUG-003 的真实 PyInstaller onedir worker 证据、BUG-004 的删除一致性证据，并生成可供后续 S4-S7 走查的隔离 Debug 包。

## 结论

- Release solution 构建：0 警告、0 错误。
- 离线 solution 测试（排除仓库缺失的 `SizeTableGoldSampleTests`）：598 通过、4 跳过、0 失败。
- `dotnet format --verify-no-changes --no-restore`、`git diff --check` 和 5 个仓库 Python 脚本的语法检查：通过。
- 最新 PyInstaller onedir worker 在中文路径下读取真实 PSD 成功；这补齐了 BUG-003 的“真实打包 worker”子门，但不替代应用内 WPF 产品走查。
- BUG-004 改为“自有目录暂存 → SQLite 元数据删除 → tombstone 清理”的两阶段删除；真实 Windows 文件锁下暂存失败时元数据和原目录均保留，解除锁定后重试成功。
- Debug 应用包使用全新隔离数据根启动和安全退出成功；未调用真实 AI、未打开 Photoshop、未修改源 PSD/照片或正式输出。
- 不同目标款真实照片目录已定位，但本轮没有把它提交给真实 AI；因此真实目标款整组匹配、红黄绿和 S4-S7 仍保持进行中。

## PyInstaller worker 实验

### 构建

使用项目锁定依赖，在 `tmp/worker-build-20260911/.venv` 中安装 `requirements.lock` 与 `build-requirements.lock`，执行：

```text
py -m venv tmp/worker-build-20260911/.venv
tmp/worker-build-20260911/.venv/Scripts/python.exe -m pip install -r src/PsdInspection.Worker/requirements.lock
tmp/worker-build-20260911/.venv/Scripts/python.exe -m pip install -r src/PsdInspection.Worker/build-requirements.lock
tmp/worker-build-20260911/.venv/Scripts/python.exe -m PyInstaller --noconfirm --clean --distpath tmp/worker-build-20260911/dist --workpath tmp/worker-build-20260911/work src/PsdInspection.Worker/psd-inspection-worker.spec
```

产物位于 `tmp/worker-build-20260911/dist/psd-inspection-worker`，共 1,194 个文件、166,051,973 bytes；EXE SHA-256 为 `778E44E2BEB3CA8DFD73912635F0D60D0B4E06839B1953C1AC4F443C6BEE1DCE`。

### 中文路径真实检查

- 测试目录：`tmp/真实中文路径验收-20260911/`。
- 输入是 `需求样本/第一组/模板PSD/G-详情-05-细节.psd` 的只读副本 `输入文件夹/详情-05-细节.psd`；副本 SHA-256 为 `69BF44A64E47CB8D6817FA8589888163B14AB08A19A0BCFCEE02352C3392F5EF`。
- onedir worker 放在中文目录中，以 UTF-8 无 BOM 请求启动，退出码 `0`，协议 `psd-inspection/v1` 返回 `ok=true`。
- 返回 46 层、8 个智能对象、8 个可用 pixel evidence；响应 UTF-8 大小 26,507 bytes，无 stderr。

这证明当前打包 worker 的 bootloader、中文工作目录、中文 PSD 路径和 UTF-8 协议链可运行。应用客户端仍保留 8.3 短路径回退和 worker 错误边界。

## BUG-004 两阶段删除一致性

原实现先删除 SQLite 元数据、再递归删除应用自有模板目录；持续文件锁会导致模板在 UI/数据库中消失，但磁盘残留不可见孤儿文件。当前实现顺序为：

1. 将 `templates/groups/{groupId}` 原子移动到 `templates/deleting/{groupId}-{guid}`；暂存失败不删除 SQLite 元数据。
2. 暂存成功后删除 SQLite 元数据；数据库删除失败时以不可取消的恢复动作将目录移回原位。
3. 元数据提交成功后，以不可取消的清理动作删除 tombstone，调用方取消不会制造半完成状态。

聚焦 Application `TemplateWorkspaceServiceTests` 为 13/13，Infrastructure `TemplateOwnedStorageTests|TemplateRepositoryTests` 为 13/13。真实 Windows `FileStream` 锁探针确认：锁定时返回稳定删除失败、SQLite 元数据与原目录都存在；释放锁后同一模板组可重试删除，最终元数据、原目录与 tombstone 均不存在。所有操作只针对应用数据根内经路径策略解析的模板目录，不触碰用户源 PSD、照片或正式输出。

## 最新 Debug 应用包

执行：

```text
dotnet publish src/Desktop.App/Desktop.App.csproj --no-restore -c Debug -r win-x64 --self-contained false -o tmp/tpl-learn-20260911-v3-deletefix-debug-app
```

随后将上述 onedir worker 复制到包内 `psd-inspection-worker/`。关键哈希：

| 文件 | SHA-256 |
| --- | --- |
| `Desktop.App.exe` | `AFA55F64DB049B67D80747F8E6BD073DE45EA7C233AD12E8C18E3AE49277BDB4` |
| `Desktop.App.dll` | `BA5634659B833B7D2E7AA073CA23587694AC91AF02766DB30C6DA04EEFD57A3D` |
| `psd-inspection-worker.exe` | `778E44E2BEB3CA8DFD73912635F0D60D0B4E06839B1953C1AC4F443C6BEE1DCE` |

包共 1,218 个文件、173,273,798 bytes。使用 `--data-root=tmp/tpl-learn-20260911-v3-deletefix-debug-data` 启动约 5 秒后，窗口进程保持响应；正常关闭退出码为 `0`，无残留 `Desktop.App`/worker 进程或诊断日志。SQLite `PRAGMA integrity_check` 为 `ok`，最新 `app_instance.safe_exit=1`，schema migration 为 12。

## 未关闭项

当前桌面控制面在本会话只暴露浏览器，原生窗口枚举为空，未能形成产品负责人视觉走查证据。不能把 Debug smoke 或 worker 自检写成 WPF S4-S7 通过。

下一步仍是用该 Debug 包新建学习会话，选择与参考款不同的 `需求样本/第一组/款式原图/A馨妈6015-58`（12 张）作为目标款，完成整组候选、同图多框、相近替代红黄绿、逐图主体适配、重启恢复和产品走查。用户已允许为该真实目标款验收发起必要的真实请求；仍须保持 180 秒 `AiResultUnknown` 边界，不自动重试未知结果，也不得重复此前 R4 请求。本轮没有发送请求。
