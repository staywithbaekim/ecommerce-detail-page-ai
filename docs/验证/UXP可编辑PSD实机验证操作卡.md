# UXP 0.2.3 可编辑 PSD 实机验证操作卡

## 本轮只验证什么

验证 `0.2.3` 已通过的输出 PSD 是否能由美工继续人工编辑：替换一个智能对象、修改一个普通文字层，保存、关闭并重新打开后，两处修改和图层类型都仍然存在。

本轮不重新测试 JPG、不安装新版插件、不打开 bridge，也不运行 UXP Developer Tool。自动让 UXP 再改一次 PSD 不能代替真实美工工作流，因此本轮必须在 Photoshop 中人工完成两处编辑。

所有操作只针对包内自动创建的 `work/G-detail-03-TV08-working.psd`。禁止编辑 `baseline` 中的金标产物，也不要使用此前桌面上的 `0.2.3` 原件。

## 已固定的测试输入

- 金标输出 PSD：`0.2.3` 的 `G-详情-03-validation-output.psd`，SHA-256 `DD997E7F47B8792744C4EBF57261A5109B50D195BEDFCC8B89FE836DCE7F6949`。
- 替换图片：正面图 `7Y9A3750.jpg`，SHA-256 `C04DDDD4C7802277D9996F347BA6FD4285987147941EED5A69638E0443696D57`。
- 智能对象：顶层 `back_main`。
- 普通文字层：`边框-无需修改` 组内的 `上身舒适更柔软`。
- 修改后的文字内容：`TV08 EDIT OK`，字母之间保留空格。

脚本已经确认金标 PSD 内有 `16` 个真实文字层，`back_main` 是智能对象；无需另找模板。

## 远程操作

1. 把 `uxp-validation-tv08-editability-0.2.3.1.zip` 传到 Photoshop 电脑并“全部解压缩”。不要直接在压缩包预览窗口内运行。
2. 双击解压目录中的 `START-TV08.bat`。窗口显示 `fixture_ready: true` 后，会打开 `work` 文件夹。
3. 用 Photoshop 打开 `work/G-detail-03-TV08-working.psd`。不要打开 `baseline` 目录中的文件。
4. 在图层面板选中顶层智能对象 `back_main`，执行“图层 -> 智能对象 -> 替换内容”，选择包内 `source/7Y9A3750.jpg`。也可以右键 `back_main` 后选择“替换内容”。画面应从背面图变为明显的正面图。
5. 展开 `边框-无需修改` 组，找到带 `T` 图标的 `上身舒适更柔软`。双击 `T` 图标，把画布中的实际文字内容改成 `TV08 EDIT OK`。不能只修改图层名称，也不能新建另一个文字层。
6. 按 `Ctrl+S` 保存，关闭这个 PSD 标签页，再从 `work` 文件夹重新打开同一个 PSD。
7. 重新打开后确认：画布仍是正面图；`TV08 EDIT OK` 仍可见；`back_main` 仍带智能对象图标且可以双击打开；修改后的文字仍带 `T` 图标。
8. 截一张同时包含画布和图层面板的截图，然后关闭 Photoshop 中的 PSD。
9. 双击 `VERIFY-TV08.bat`。自动检查通过后，`work` 中会出现 `tv08-editability-evidence.json`。

若第 5 步出现缺少字体提示，先记录截图。只要文字仍可编辑、能保存并重开，它属于模板字体环境问题，不直接判定 UXP 执行通道失败。

## 需要回传

- `work/G-detail-03-TV08-working.psd`
- `work/tv08-editability-evidence.json`
- 第 8 步关闭重开后的画布与图层面板截图

自动脚本会确认：金标和源图哈希未变；工作 PSD 确实改变；正面 JPG 的原始 payload 恰好完整内嵌一次；`TV08 EDIT OK` 进入 PSD 文字数据；排除 JPG payload 内偶然出现的同名字节后，`TySh`、`SoLd`、`PlLd` 结构记录数量保持不变。

脚本通过只表示产物已具备复核条件。TV-08 只有在回传 PSD 可再次解析、截图确认关闭重开后的画面和图层类型都正确之后，才能正式标记为通过。

## 失败处理

- `START-TV08.bat` 提示 working 文件已存在：不要覆盖或删除证据，重新解压一份干净的 ZIP 再做。
- 找不到 `back_main`、文字层没有 `T` 图标、保存失败或重开丢失修改：立即停止并保留截图和文件。
- `VERIFY-TV08.bat` 报错：保留整个解压目录，不要继续修改 baseline；把报错截图和 `work` 文件夹回传排查。
