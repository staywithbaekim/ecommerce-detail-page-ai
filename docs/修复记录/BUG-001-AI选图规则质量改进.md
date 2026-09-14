# BUG-001 修复说明：AI生成的选图规则质量改进

## 修复日期
2026-09-11

## 问题描述
在学习阶段，AI为10个图片框生成的选图规则过于模糊，9个规则完全相同："商品展示图（与参考款保持同一视角，请确认）"。这导致用户无法区分不同图片框的具体用途。

## 根本原因
1. **硬编码的 frame_context**：所有图片框使用完全相同的上下文描述
2. **单一的 layout_intent**：所有框的布局意图都是 "subject_cover"
3. **缺少图层信息**：没有利用图层名称、位置、大小等上下文信息

## 解决方案

### 1. 新增布局意图推断逻辑
**文件**: `src/Desktop.Application/Templates/ReferenceLearningService.cs`

新增 `InferLayoutIntent` 方法，根据图片框特征推断布局意图：

- **detail_crop**: 小尺寸框（面积<15%）或包含细节关键词（领口、袖口、面料等）
- **hero_centered**: 大尺寸居中框（面积>50%，中心位置在画布30%-70%区域）
- **full_product**: 超大框架（面积>60%）
- **grid_top_left/top_right/bottom_left/bottom_right**: 网格布局中的定位框（面积15%-50%）
- **subject_cover**: 默认回退值

推断依据：
- 图片框面积占画布比例
- 图片框中心位置（相对画布）
- 图片框宽高比
- 图层名称和文件名关键词

### 2. 动态生成 frame_context
**文件**: `src/Desktop.Application/Templates/ReferenceLearningAiService.cs`

新增 `BuildFrameContext` 方法，为每个图片框生成独特的上下文描述：

```
图层名称：[实际图层名]；布局角色：[根据intent推断]；画幅：[横版/竖版/方形]；基于已匹配参考图的可见内容生成明确选图要求
```

示例输出：
- 小细节框：`图层名称：领口细节；布局角色：小尺寸特写/细节框；画幅：方形构图；基于已匹配参考图的可见内容生成明确选图要求`
- 主图框：`图层名称：主图；布局角色：大尺寸居中主图框；画幅：方形构图；基于已匹配参考图的可见内容生成明确选图要求`

### 3. 修改的代码位置

#### ReferenceLearningService.cs (行 983-1012 → 983-1097)
- 修改 `DeriveLayout` 方法：调用 `InferLayoutIntent` 而非硬编码 "subject_cover"
- 新增 `InferLayoutIntent` 方法（约90行）

#### ReferenceLearningAiService.cs (行 175-181 & 471-473)
- 修改提示词构建：从硬编码 `frame_context` 改为调用 `BuildFrameContext(frame)`
- 新增 `BuildFrameContext` 方法（约40行）

## 预期效果

修复后，AI应该能够生成更具体的选图规则：

| 修复前 | 修复后 |
|--------|--------|
| 商品展示图（与参考款保持同一视角，请确认） | 真人模特正面站姿图 |
| 商品展示图（与参考款保持同一视角，请确认） | 假模特平铺正面图 |
| 商品展示图（与参考款保持同一视角，请确认） | 领口特写图 |
| 商品展示图（与参考款保持同一视角，请确认） | 面料质地细节图 |

## 验证方法

### 1. 单元测试
已创建 `ReferenceLearningLayoutIntentTests.cs`，包含以下测试用例：
- ✅ 小尺寸细节框应推断为 `detail_crop`
- ✅ 大尺寸居中框应推断为 `hero_centered`
- ✅ 超大框应推断为 `full_product`
- ✅ 网格左上框应推断为 `grid_top_left`
- ✅ 网格右下框应推断为 `grid_bottom_right`
- ✅ 多个框应有不同的意图

### 2. 真实场景验证
使用 TPL-LEARN-01 真实样本：
1. 打开有多个图片框的参考 PSD（如 10 框详情页）
2. 执行学习流程
3. 检查生成的 `SemanticLabel` 字段
4. 验证不同框的标签是否具有明确区分度

### 3. 验证检查点
- [ ] 细节框（领口、袖口、面料）生成细节描述
- [ ] 主图框生成完整展示描述
- [ ] 不同位置的框有不同的上下文提示
- [ ] AI 缓存键包含新的 layout_intent 信息

## 风险与注意事项

### 1. 缓存失效
**风险**: 已缓存的 AI 结果仍使用旧的 `subject_cover` intent

**缓解措施**: 
- 缓存键计算包含 `layout_intent` 字段
- 旧缓存自动失效，会重新调用 AI
- `ModelConfigVersion` 保持不变（因为是前端改进，非 AI 模型变更）

### 2. 向后兼容性
**影响**: 已保存的学习会话文档中的 `LayoutIntent` 值会更新

**兼容性**: 
- ✅ 域模型 `LayoutIntent.Intent` 是字符串字段，接受任意值
- ✅ 没有硬编码枚举检查
- ✅ 旧的 "subject_cover" 值仍然有效

### 3. AI 提示词影响
**风险**: 更详细的 `frame_context` 可能导致 AI 响应变化

**缓解措施**:
- 保持 AI 提示词结构不变，只增强输入上下文
- 语义验证逻辑保持不变
- 如果 AI 质量下降，可以回退（修改 `BuildFrameContext` 返回简化版本）

## 回滚计划

如果需要紧急回滚：

1. **最小回滚**：只修改 `BuildFrameContext` 返回固定字符串
```csharp
private static string BuildFrameContext(ReferenceFrameCandidate frame)
{
    return "基于已匹配参考图的可见内容生成可消歧要求；结构与资格已经由本地证据确认。";
}
```

2. **完全回滚**：还原 `DeriveLayout` 硬编码 "subject_cover"
```csharp
return LayoutIntent.Create(
    "subject_cover",  // 还原为固定值
    scale,
    left - safeLeft,
    top - safeTop,
    TemplateRect.Create(safeLeft, safeTop, safeRight, safeBottom));
```

## 后续改进建议

1. **增强关键词库**：扩展 `InferLayoutIntent` 中的关键词检测（如 "拉链"、"纽扣"、"标签" 等）
2. **机器学习优化**：收集真实用户修正数据，训练布局意图分类器
3. **可视化调试**：在 UI 显示推断的 `layout_intent`，帮助用户理解 AI 推理过程
4. **配置化规则**：将关键词和阈值提取到配置文件，支持运营人员调整

## 相关文档
- 任务书: `docs/任务/AI-01纵向实现任务书.md`
- 技术方案: `docs/技术方案/桌面端V1总体技术方案.md`
- 验证记录: `docs/验证/TPL-LEARN-01-S7真实样本逐框与恢复隔离记录.md`
