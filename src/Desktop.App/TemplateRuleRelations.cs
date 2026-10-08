using Desktop.Application.Templates;

namespace Desktop.App;

// Read-only projection of the same rule snapshot used by confirmation and production.
// There is deliberately no independently editable graph/purpose data.
public sealed record TemplateRuleRelationFrame(string PageId, string FrameId, string Label, string Description, string Color);
public sealed record TemplateRuleRelationNode(string PageId, string Label, string Usage, string Order, IReadOnlyList<TemplateRuleRelationFrame> Frames);
public sealed record TemplateRuleRelationGroup(string Condition, string Connection, IReadOnlyList<TemplateRuleRelationNode> Nodes);

public static class TemplateRuleRelations
{
    public static IReadOnlyList<TemplateRuleRelationGroup> Build(TemplateRuleDraftView? draft, IEnumerable<TemplateRulePageViewModel> pages)
    {
        var byId = pages.ToDictionary(page => page.PageId, StringComparer.Ordinal);
        if (draft?.Rules is not { } rules)
            return [new("每款商品", "按顺序组合 · 沿用已保存的页面规则",
                byId.Values.Where(page => page.Enabled).Select((page, index) => Node(page, null, index)).ToArray())];
        return rules.Branches.Select(branch => new TemplateRuleRelationGroup(Condition(branch.Condition),
            rules.Branches.Length > 1 ? "条件互斥；命中后按顺序组合" : "命中条件后按顺序组合",
            branch.Recipes.Select((recipe, index) => byId.TryGetValue(recipe.PageId, out var page)
                ? Node(page, recipe, index) : new TemplateRuleRelationNode(recipe.PageId, "失效模板引用", "请返回修改这次模板使用", Order(index), [])).ToArray())).ToArray();
    }

    public static string Purpose(TemplateRulePageViewModel page, IReadOnlyList<TemplateRuleRelationGroup> groups)
    {
        var uses = groups.SelectMany(group => group.Nodes.Where(node => node.PageId == page.PageId)
            .Select(node => $"{group.Condition}：{node.Usage}（{node.Order}）")).ToArray();
        return uses.Length == 0 ? "当前没有使用此页；点击设置用途" : string.Join("\n", uses);
    }

    private static TemplateRuleRelationNode Node(TemplateRulePageViewModel page, TemplatePageRecipeView? recipe, int index) =>
        new(page.PageId, page.DisplayName, page.IsSizeTable ? "每款一次 · 使用本款尺码素材" : Usage(page, recipe), Order(index),
            page.Frames.Select(frame => new TemplateRuleRelationFrame(page.PageId, frame.FrameId, frame.Label, frame.Description, Color(recipe, frame.FrameId))).ToArray());
    private static string Order(int index) => $"第 {index + 1} 步";
    private static string Usage(TemplateRulePageViewModel page, TemplatePageRecipeView? recipe)
    {
        if (recipe is null) return page.GenerationMode switch
        {
            TemplateGenerationModeOption.OncePerVariant => "每个颜色使用一次",
            TemplateGenerationModeOption.VariantCapacityCombination => $"按 {page.VariantCapacity} 色组合使用（原页面规则）",
            _ => "使用一次"
        };
        if (recipe.Repeat == TemplateRuleRepeatOption.Once) return "使用一次";
        var slots = recipe.ColorSlots.Count(slot => slot.Source == TemplateRuleColorSourceOption.Allocated);
        var range = recipe.ColorRange == TemplateRuleColorRangeOption.Secondary ? "副色" : "颜色";
        return $"每 {slots} 个{range}重复一次" + (recipe.ExpectedInstanceCount is { } count ? $"；须恰好 {count} 次" : "；次数由本款颜色决定");
    }
    private static string Condition(TemplateRuleColorCountView condition)
    {
        var count = condition.Minimum == 1 && condition.Maximum is null ? "有商品颜色"
            : condition.Maximum == condition.Minimum ? $"{condition.Minimum} 个颜色"
            : condition.Maximum is { } max ? $"{condition.Minimum}–{max} 个颜色" : $"至少 {condition.Minimum} 个颜色";
        return count + (condition.Parity switch { TemplateRuleParityOption.Odd => "，且为奇数", TemplateRuleParityOption.Even => "，且为偶数", _ => "" });
    }
    private static string Color(TemplatePageRecipeView? recipe, string frameId)
    {
        if (recipe is null) return "沿用此框已保存的选图约束";
        var binding = recipe.FrameBindings.SingleOrDefault(item => item.FrameId == frameId);
        var slot = recipe.ColorSlots.SingleOrDefault(item => item.SlotId == binding?.SlotId);
        if (slot is null) return "尚未指定颜色关系 · 请补充";
        var text = slot.Source switch
        {
            TemplateRuleColorSourceOption.Primary => "主色",
            TemplateRuleColorSourceOption.SingleSecondary => "唯一副色",
            _ => $"本次第 {recipe.ColorSlots.Where(item => item.Source == TemplateRuleColorSourceOption.Allocated).ToList().IndexOf(slot) + 1} 个颜色"
        };
        return text + (recipe.FrameBindings.Count(item => item.SlotId == slot.SlotId) > 1 ? " · 相同标记的框保持同色" : "");
    }
}
