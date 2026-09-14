# TPL-LEARN-01 验收测试计划

**版本：** V2 - 修复后验收  
**日期：** 2026-09-11  
**状态：** 🟢 所有修复已完成，准备验收

> **维护说明（2026-09-13）：** 本页保留的是 2026-09-11 的历史验收计划和操作步骤；“所有修复已完成”仅指当时列出的代码修复子门，不是 `TPL-LEARN-01` 或 LEARN-S4 至 S7 的整体通过结论。当前状态以任务书第 8.12 节、`docs/任务/TPL-LEARN-01-当前状态.md`、`docs/验证/TPL-LEARN-01-S7真实样本逐框与恢复隔离记录.md` 和 `docs/验收矩阵.md` 为准。

---

## 一、修复完成总结

### ✅ 已修复的问题（4/4）

| Bug ID | 问题描述 | 状态 | 修复方案 |
|--------|---------|------|---------|
| BUG-001 | AI生成规则质量差 | ✅完成 | 动态布局意图推断 + 个性化上下文 |
| BUG-002 | PSD重复检测阻塞 | ✅完成 | 自动重用草稿内重复 |
| BUG-003 | 中文路径不支持 | ✅完成 | Windows短路径转换 |
| BUG-004 | 删除功能不工作 | ✅完成 | 重试机制 + 错误处理 |

### 修改的代码文件

**核心逻辑（5个文件）：**
1. `src/Desktop.Application/Templates/ReferenceLearningService.cs`
2. `src/Desktop.Application/Templates/ReferenceLearningAiService.cs`
3. `src/Desktop.Application/Templates/TemplatePageImportService.cs`
4. `src/Desktop.Application/Templates/TemplateWorkspaceService.cs`
5. `src/Desktop.Infrastructure/Psd/PsdInspectionWorkerClient.cs`

**基础设施（3个文件）：**
6. `src/Desktop.Application/Templates/TemplateImportContracts.cs`
7. `src/Desktop.Infrastructure/Templates/TemplateOwnedStorage.cs`
8. `src/Desktop.App/MainWindowViewModel.Templates.cs`

**测试（3个新增）：**
9. `tests/Desktop.Application.Tests/ReferenceLearningLayoutIntentTests.cs`
10. `tests/Desktop.Application.Tests/TemplateDuplicateHandlingTests.cs`
11. `tests/Desktop.Application.Tests/TemplateDeleteTests.cs`
12. `tests/Desktop.Infrastructure.Tests/PsdInspectionWorkerClientTests.cs`

---

## 二、验收测试环境

### 测试数据
- **参考PSD**：6个文件，位于 `C:/Users/79122/Documents/test-20260911/psd-input/psds/`
- **参考照片**：12张，位于 `C:/Users/79122/Documents/test-20260911/psd-input/photos/`
- **目标款**：简唏7707-75文件夹，24张照片

### 测试环境
- **应用路径**：`C:/Users/79122/Documents/test-20260911/app/`（英文路径）
- **中文路径测试**：将应用复制到含中文的路径进行测试
- **API Key**：火山方舟已配置

---

## 三、完整验收测试流程

### 测试场景1：完整学习流程（核心场景）

#### 步骤1：启动应用并配置
1. 启动 `Desktop.App.exe`
2. 进入设置，确认API Key已配置

#### 步骤2：新建模板组（测试BUG-002修复）
1. 点击"模板" → "新建模板组"
2. 选择"从人工结果学习"
3. 点击"选择 PSD"
4. 选择 `psd-input/psds/` 文件夹，导入6个PSD
5. **验证点**：✅ 不应出现"内容相同的PSD"重复检测提示
6. 选择参考照片文件夹：`psd-input/photos/`

#### 步骤3：查看AI生成的规则（测试BUG-001修复）
1. 等待PSD检查完成（约2-4分钟）
2. 进入"页面校准"界面
3. 逐个查看每个图片框的规则描述
4. **验证点**：
   - ✅ 不同的图片框应该有不同的规则描述
   - ✅ 规则应该具体明确，如"领口特写图"、"面料质地图"等
   - ❌ 不应该都是"商品展示图（与参考款保持同一视角）"
5. 截图保存规则示例

#### 步骤4：保存模板并测试目标款匹配
1. 确认规则后保存模板组
2. 返回工作台，新建批次
3. 选择刚创建的模板组
4. 选择简唏7707-75文件夹作为目标款
5. 扫描并分析
6. **验证点**：
   - ✅ AI能正确从24张照片中挑选匹配的图片
   - ✅ 领口框匹配到领口照片，面料框匹配到面料照片
   - ✅ 匹配准确率显著提升

---

### 测试场景2：删除模板功能（测试BUG-004修复）

#### 步骤1：删除草稿模板
1. 进入"模板"页面
2. 选择一个草稿状态的模板
3. 点击"删除"
4. **验证点**：
   - ✅ 删除成功，模板从列表消失
   - ❌ 不应该静默失败

#### 步骤2：删除已提交的模板
1. 选择一个已提交但没有批次使用的模板
2. 点击"删除"
3. **验证点**：
   - ✅ 删除成功

#### 步骤3：删除被批次引用的模板
1. 创建一个批次使用某个模板
2. 尝试删除该模板
3. **验证点**：
   - ✅ 应该提示"无法删除，有X个批次正在使用"
   - ✅ 错误信息清晰友好

---

### 测试场景3：中文路径支持（测试BUG-003修复）

#### 步骤1：准备中文路径环境
1. 关闭应用
2. 复制应用到中文路径：`C:/Users/79122/Documents/测试文件夹/桌面应用/`
3. 启动应用

#### 步骤2：执行学习流程
1. 重复测试场景1的步骤
2. **验证点**：
   - ✅ PSD检查worker能正常启动
   - ✅ 不应该出现"Failed to load Python DLL"错误
   - ✅ 整个流程完整可用

---

### 测试场景4：重复PSD处理（测试BUG-002修复细节）

#### 步骤1：重新导入相同的PSD
1. 在现有草稿中，点击"添加 PSD"
2. 重新选择相同的PSD文件
3. **验证点**：
   - ✅ 系统自动重用草稿内已有的副本
   - ✅ 不需要用户手动选择"保留/复用"

#### 步骤2：跨草稿重复测试
1. 新建另一个模板组
2. 导入相同的PSD
3. **验证点**：
   - ✅ 能正常导入（因为是不同草稿）
   - ✅ 不会混淆资源

---

## 四、验收标准

### 必须通过（P0）
- [ ] **BUG-001**: AI生成的规则明确且具有区分度
- [ ] **BUG-002**: 重复PSD处理流畅，不阻塞用户
- [ ] **BUG-003**: 中文路径下应用完全可用
- [ ] **BUG-004**: 删除模板功能正常工作

### 质量指标
- [ ] 完整学习流程可以一次性完成，无需多次重试
- [ ] AI匹配准确率达到80%以上
- [ ] 所有错误提示清晰友好，用户能理解下一步操作

### 回归测试
- [ ] 旧版本创建的模板仍然可用
- [ ] 其他核心功能（批次管理、输出）不受影响

---

## 五、验收结果记录

### 测试场景1：完整学习流程
**执行日期：** ___________  
**执行人：** ___________  
**结果：** [ ] 通过 [ ] 失败  
**问题记录：**

---

### 测试场景2：删除模板功能
**执行日期：** ___________  
**执行人：** ___________  
**结果：** [ ] 通过 [ ] 失败  
**问题记录：**

---

### 测试场景3：中文路径支持
**执行日期：** ___________  
**执行人：** ___________  
**结果：** [ ] 通过 [ ] 失败  
**问题记录：**

---

### 测试场景4：重复PSD处理
**执行日期：** ___________  
**执行人：** ___________  
**结果：** [ ] 通过 [ ] 失败  
**问题记录：**

---

## 六、最终验收结论

**总体评估：** [ ] 通过验收 [ ] 需要返工  
**签字：** ___________  
**日期：** ___________  

**备注：**

## 七、2026-09-13 实际执行覆盖（增量记录）

使用最新包 `tmp/tpl-learn-20260913-transport-fix-debug-app/Desktop.App.exe`，以模板组“测试”和目标款“简唏7707-75”创建批次 `batch-9f7b115defbb4563a26471089f479098`。实际输入为 24 张 PNG/JPG，模板快照为 6 页、11 个图片框；唯一 `ai-analysis/v1` 请求上传完成并返回 HTTP 200，结果为 `exact=9`、`safe_similar=2`，带 `no_full_back_person` 警告，未重试。输出隔离目录保持为空，未点击“开始生成”。

WPF 中仅完成一次 `page_1 · 图片框 1` 的“照片 03 → 改用这张”人工锁图；该框显示“人工选用 · 已锁定”，其余 10 个框仍为 AI 推荐。正常关闭并重启后，SQLite `integrity_check=ok`，锁定 revision、`ManualReview` session/ledger 和单次 usage 保持，日志未出现第二次请求。

本次不能替代完整验收：Computer Use 控制面仍返回 `Trusted RPC service is not configured: sky`，因此没有重启后的截图级视觉证据；全部 11 框逐框语义确认、不同目标款绿/黄/红准确性（含同图多框和主体适配）、取消/中途退出恢复、RenderPlan 参考零引用和 reviewed diff 仍未完成。故本计划的总体验收结论不得填写为“通过”，也不得交接 `RENDER-01`。
