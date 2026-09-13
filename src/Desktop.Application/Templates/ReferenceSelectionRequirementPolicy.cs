using System.Buffers;

namespace Desktop.Application.Templates;

/// <summary>
/// Keeps learned selection requirements reusable across products. The AI may
/// explain its decision in the evidence fields, but the rule consumed by a
/// future batch must remain a short visual-category phrase rather than a
/// description of one reference photograph.
/// </summary>
public static class ReferenceSelectionRequirementPolicy
{
    public const int MinLength = 2;
    public const int MaxLength = 40;
    public const string UnconfirmedLabel = "待确认图片类别";

    private static readonly SearchValues<char> NarrativePunctuation = SearchValues.Create(
    [
        ',', '，', '.', '。', ';', '；', ':', '：', '、', '\r', '\n',
        '(', ')', '（', '）', '[', ']', '【', '】', '{', '}', '/', '\\',
        '-', '—', '–', '“', '”', '"', '\'', '！', '!'
    ]);

    private static readonly string[] IncidentalPhotoAttributes =
    [
        "背景", "墙面", "墙壁", "地板", "地面", "长凳", "凳子", "桌子", "椅子", "沙发", "花瓶",
        "道具", "项链", "耳坠", "耳环", "手镯", "手链", "戒指", "光线", "灯光", "采光", "色调", "氛围",
        "酒红", "深蓝", "浅蓝", "大红", "红色", "蓝色", "白色", "黑色", "绿色", "黄色", "紫色", "粉色",
        "灰色", "米色", "棕色", "咖色", "橙色", "金色", "银色"
    ];

    // These words describe one photograph or a camera composition. They are
    // useful as evidence, but cannot be used as a reusable future-batch rule.
    private static readonly string[] NarrativeTerms =
    [
        "商品展示图（", "商品图片（", "请确认", "保持同一视角", "保持同一构图", "沿用参考款",
        "构图", "景别", "画面", "呈现", "展示位", "人物从", "从头", "从后脑", "至大腿", "至脚",
        "身着", "搭配", "背景", "墙面", "墙壁", "地板", "地面", "光线", "坐在", "站在",
        "双手", "一只手", "手持", "摆放", "佩戴", "区域", "完整展示", "特征"
    ];

    private static readonly string[] GenericPlaceholders =
    [
        "商品展示图", "商品图片", "商品图", "模特上身展示图", "待确认", "请选择目标款"
    ];

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var phrase = value.Trim();
        return phrase.Length is >= MinLength and <= MaxLength
            && phrase.AsSpan().IndexOfAny(NarrativePunctuation) < 0
            && !string.Equals(phrase, UnconfirmedLabel, StringComparison.Ordinal)
            && !GenericPlaceholders.Any(placeholder =>
                phrase.Contains(placeholder, StringComparison.Ordinal))
            && !NarrativeTerms.Any(term =>
                phrase.Contains(term, StringComparison.Ordinal))
            && !IncidentalPhotoAttributes.Any(attribute =>
                phrase.Contains(attribute, StringComparison.Ordinal));
    }

    /// <summary>
    /// Converts a provider's occasional one-photo sentence into the smallest
    /// stable visual category that can be used by a later target batch. This
    /// is a projection, not a second vision pass: if no stable category cue
    /// exists, the result remains unconfirmed and is rejected by IsValid.
    /// </summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var phrase = value.Trim();
        if (IsValid(phrase))
        {
            normalized = phrase;
            return true;
        }

        if (ContainsAny(phrase, "成分", "水洗", "比例", "含量", "纤维", "文字信息", "规格", "尺码表", "尺码信息"))
        {
            normalized = phrase.Contains("尺码", StringComparison.Ordinal)
                ? "尺码信息图"
                : "成分信息图";
            return true;
        }

        if (ContainsAny(phrase, "领口", "领部", "领型", "领边", "尺码标", "织唛", "纽扣"))
        {
            normalized = phrase.Contains("尺码标", StringComparison.Ordinal)
                ? "领口与尺码标细节图"
                : "领口细节图";
            return true;
        }

        if (ContainsAny(phrase, "袖口", "袖子末端", "袖部", "翻折"))
        {
            normalized = "袖口工艺特写图";
            return true;
        }

        if (ContainsAny(phrase, "面料", "布料", "面布", "纹理", "材质", "提花", "褶皱", "垂坠"))
        {
            normalized = "面料纹理特写图";
            return true;
        }

        if (phrase.Contains("口袋", StringComparison.Ordinal))
        {
            normalized = "口袋细节图";
            return true;
        }

        if (ContainsAny(phrase, "背面", "背身", "后身", "背对"))
        {
            normalized = ContainsAny(phrase, "全身", "脚踝", "脚部", "完整背面")
                ? "人物背面全身图"
                : "人物背面图";
            return true;
        }

        if (ContainsAny(phrase, "侧面", "侧身", "侧对"))
        {
            normalized = ContainsAny(phrase, "坐", "端坐")
                ? "人物侧面端坐图"
                : "人物侧面图";
            return true;
        }

        if (ContainsAny(phrase, "坐姿", "坐在", "端坐", "落座"))
        {
            normalized = phrase.Contains("正面", StringComparison.Ordinal)
                ? "人物正面端坐图"
                : "人物端坐图";
            return true;
        }

        if (ContainsAny(phrase, "站姿", "站立", "站在"))
        {
            normalized = phrase.Contains("正面", StringComparison.Ordinal)
                ? "人物正面站立图"
                : "人物站立图";
            return true;
        }

        if (phrase.Contains("正面", StringComparison.Ordinal))
        {
            normalized = "人物正面图";
            return true;
        }

        return false;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.Ordinal));
}
