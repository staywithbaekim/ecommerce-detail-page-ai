using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Ai;
using Desktop.Application.Diagnostics;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;

namespace Desktop.Infrastructure.Ai;

internal static class VolcArkAiPayloadBuilder
{
    internal static byte[] BuildProviderPayload(AiAnalysisRequestV1 request, List<AiAnalysisCopy> images, string modelId, JsonSerializerOptions jsonOptions)
    {
        var content = new JsonArray();
        for (var index = 0; index < request.Assets.Count; index++)
        {
            var copy = images[index];
            content.Add(new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = JsonSerializer.Serialize(new
                {
                    image_alias = AiAnalysisResultReferenceNormalizer.AssetAlias(index)
                }, jsonOptions)
            });
            content.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = "data:" + copy.MimeType + ";base64," + Convert.ToBase64String(copy.Content.Span)
            });
        }

        var requirementSummary = JsonSerializer.Serialize(
            request.Requirements.Select((requirement, index) => new
            {
                requirement_id = AiAnalysisResultReferenceNormalizer.RequirementAlias(index),
                selection_requirement = requirement.SelectionRequirement,
                layout_intent = requirement.LayoutIntent,
                required = requirement.Required
            }),
            jsonOptions);
        content.Add(new JsonObject
        {
            ["type"] = "input_text",
            ["text"] = "请仅返回符合 ai-analysis-result/v1 的 JSON 对象。每个 requirement_matches 元素只能包含 requirement_id、asset_id、rank、confidence、evidence_codes 五个字段，不要添加 matched 或其他字段。request_id="
                + AiAnalysisResultReferenceNormalizer.RequestAlias
                + "，model_config_version="
                + AiAnalysisResultReferenceNormalizer.ModelConfigVersionAlias
                + "，analysis_kind=" + request.AnalysisKind
                + "。analysis_kind=product_joint_understanding 时把全部图片视为同一目标款候选整组，按每个开放式 selection_requirement 独立排序；同一 asset_id 可以出现在多个图片框的排名中。每个 requirement_matches 的 evidence_codes 必须且只能包含一个匹配类别：精确符合用 exact，不影响用途的安全相近替代用 safe_similar，存在实质差异用 material_difference，错款、错变体、无关素材或不安全候选用 unsafe。不要根据文件名推断语义；没有安全候选时不得编造匹配。analysis_kind=selected_asset_geometry 时只分析请求中的已确认单图并返回其主体几何；analysis_kind=size_table_extraction 时只从清晰可读的尺码来源提取原文行列，缺失、低清或冲突时返回 warning 且 size_table_candidate=null，禁止猜值"
                + "。每个 image_alias 标签只对应紧随其后的那一张图片。结果中的 request_id、model_config_version、requirement_id、coverage_requirement_ids、missing_requirement_ids、asset_id、asset_ids 和 source_asset_id 必须原样使用本请求给出的短别名；不得猜测、改写或输出源系统真实标识"
                + "。除 requirement_matches 外，请把真实商品配色分成 variant_groups：只按商品本身颜色分组，不要按背景色、光线或后期色调分组；dimension 固定填 color，value 填你观察到的颜色名称。每组返回 coverage_requirement_ids、missing_requirement_ids、is_complete、evidence_codes；完整性按所需视角/姿态/部位是否覆盖判断，不能按照片数量判断。只有一个颜色完整时将其 is_primary_suggestion=true；多个颜色完整时只能建议一个主色并填写 primary_suggestion_evidence，后续生成会保持该主色一致；所有颜色都不完整时建议覆盖最多的一组并明确缺项。variant_role 用 primary 或 secondary 表达相对角色，不能把红、蓝、黄等示例颜色写死为产品规则。"
                + "。匹配时优先满足商品视角、朝向、姿势和部位等核心用途。真人模特/假模特默认只是次级呈现形式，互为可接受候选；即使选图描述含“真人模特”，也不得仅因假模特形式判为 material_difference 或 unsafe，除非用户明确要求“必须真人模特/不要假模特”等排他条件。只要求“正面图”时，真人模特正面和假模特正面都符合候选。对同一款/颜色内的候选按整体符合度排序，返回最合适候选；exact/safe_similar 的低置信度表示建议检查，不表示没有可用图。只有错款、错颜色、无关内容或明确违反用户排他要求才判 unsafe；只有明确要求的关键属性确实不符才判 material_difference。不要输出 Markdown 或隐藏推理："
                + requirementSummary
        });

        var root = new JsonObject
        {
            ["model"] = modelId,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = content
                }
            },
            ["thinking"] = new JsonObject { ["type"] = "disabled" },
            ["max_output_tokens"] = AiAnalysisProductionPolicy.MaxOutputTokensPerRequestV2,
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = "ai_analysis_result",
                    ["strict"] = true,
                    ["schema"] = ResultSchema(request)
                }
            }
        };

        return Encoding.UTF8.GetBytes(root.ToJsonString(jsonOptions));
    }

    internal static JsonObject ResultSchema(AiAnalysisRequestV1 request)
    {
        var requirementAliases = request.Requirements
            .Select((_, index) => AiAnalysisResultReferenceNormalizer.RequirementAlias(index))
            .ToArray();
        var assetAliases = request.Assets
            .Select((_, index) => AiAnalysisResultReferenceNormalizer.AssetAlias(index))
            .ToArray();

        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("schema_version", "document_kind", "request_id", "model_config_version", "requirement_matches", "variant_groups", "subject_geometry", "size_table_candidate", "warnings", "usage"),
            ["properties"] = new JsonObject
            {
                ["schema_version"] = new JsonObject { ["const"] = "ai-analysis-result/v1" },
                ["document_kind"] = new JsonObject { ["const"] = "result" },
                ["request_id"] = new JsonObject { ["const"] = AiAnalysisResultReferenceNormalizer.RequestAlias },
                ["provider_request_ref"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                ["model_config_version"] = new JsonObject { ["const"] = AiAnalysisResultReferenceNormalizer.ModelConfigVersionAlias },
                ["requirement_matches"] = ArraySchema(MatchSchema(requirementAliases, assetAliases), 40000),
                ["variant_groups"] = ArraySchema(VariantSchema(assetAliases, requirementAliases), 200),
                ["subject_geometry"] = ArraySchema(GeometrySchema(assetAliases), 20000),
                ["size_table_candidate"] = new JsonObject { ["anyOf"] = new JsonArray(SizeTableSchema(assetAliases), new JsonObject { ["type"] = "null" }) },
                ["warnings"] = ArraySchema(WarningSchema(), 200),
                ["usage"] = UsageSchema()
            }
        };
    }

    internal static JsonObject StringSchema(int minLength, int maxLength) => new()
    {
        ["type"] = "string",
        ["minLength"] = minLength,
        ["maxLength"] = maxLength
    };

    internal static JsonObject IdSchema() => new()
    {
        ["type"] = "string",
        ["minLength"] = 1,
        ["maxLength"] = 128,
        ["pattern"] = "^[A-Za-z0-9_-]+$"
    };

    internal static JsonObject ArraySchema(JsonObject itemSchema, int maxItems) => new()
    {
        ["type"] = "array",
        ["maxItems"] = maxItems,
        ["items"] = itemSchema
    };

    internal static JsonObject MatchSchema(
        IReadOnlyList<string> requirementAliases,
        IReadOnlyList<string> assetAliases) =>
        new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("requirement_id", "asset_id", "rank", "confidence", "evidence_codes"),
            ["properties"] = new JsonObject
            {
                ["requirement_id"] = AllowedReferenceSchema(requirementAliases),
                ["asset_id"] = AllowedReferenceSchema(assetAliases),
                ["rank"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 20000 },
                ["confidence"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
                ["evidence_codes"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 32, ["uniqueItems"] = true, ["items"] = IdSchema() }
            }
        };

    internal static JsonObject VariantSchema(
        IReadOnlyList<string> assetAliases,
        IReadOnlyList<string>? requirementAliases = null) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("variant_id", "dimension", "value", "asset_ids", "is_primary_suggestion"),
        ["properties"] = new JsonObject
        {
            ["variant_id"] = IdSchema(),
            ["dimension"] = StringSchema(1, 200),
            ["value"] = StringSchema(1, 200),
            ["asset_ids"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 20000, ["uniqueItems"] = true, ["items"] = AllowedReferenceSchema(assetAliases) },
            ["is_primary_suggestion"] = new JsonObject { ["type"] = "boolean" },
            ["variant_role"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("primary", "secondary") }, new JsonObject { ["type"] = "null" }) },
            ["coverage_requirement_ids"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "array", ["uniqueItems"] = true, ["maxItems"] = 20000, ["items"] = AllowedReferenceSchema(requirementAliases ?? []) }, new JsonObject { ["type"] = "null" }) },
            ["missing_requirement_ids"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "array", ["uniqueItems"] = true, ["maxItems"] = 20000, ["items"] = AllowedReferenceSchema(requirementAliases ?? []) }, new JsonObject { ["type"] = "null" }) },
            ["is_complete"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "boolean" }, new JsonObject { ["type"] = "null" }) },
            ["evidence_codes"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 32, ["uniqueItems"] = true, ["items"] = IdSchema() }, new JsonObject { ["type"] = "null" }) },
            ["primary_suggestion_evidence"] = new JsonObject { ["anyOf"] = new JsonArray(StringSchema(1, 500), new JsonObject { ["type"] = "null" }) }
        }
    };

    internal static JsonObject GeometrySchema(IReadOnlyList<string> assetAliases) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("asset_id", "left", "top", "right", "bottom", "confidence"),
        ["properties"] = new JsonObject
        {
            ["asset_id"] = AllowedReferenceSchema(assetAliases),
            ["left"] = UnitIntervalSchema(),
            ["top"] = UnitIntervalSchema(),
            ["right"] = UnitIntervalSchema(),
            ["bottom"] = UnitIntervalSchema(),
            ["confidence"] = UnitIntervalSchema()
        }
    };

    private static JsonObject UnitIntervalSchema() => new() { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 };

    internal static JsonObject SizeTableSchema(IReadOnlyList<string> assetAliases) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("source_asset_id", "confidence", "columns", "rows"),
        ["properties"] = new JsonObject
        {
            ["source_asset_id"] = AllowedReferenceSchema(assetAliases),
            ["confidence"] = UnitIntervalSchema(),
            ["columns"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 50, ["items"] = StringSchema(1, 200) },
            ["rows"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 200, ["items"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 50, ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = 500 } } }
        }
    };

    internal static JsonObject WarningSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("code", "scope", "message_summary"),
        ["properties"] = new JsonObject
        {
            ["code"] = IdSchema(),
            ["scope"] = new JsonObject { ["enum"] = new JsonArray("request", "product", "requirement", "asset", "size_table") },
            ["message_summary"] = StringSchema(1, 500)
        }
    };

    internal static JsonObject UsageSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("usage_event_id", "input_units", "output_units", "request_count"),
        ["properties"] = new JsonObject
        {
            ["usage_event_id"] = IdSchema(),
            ["input_units"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["output_units"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["request_count"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
            ["provider_cost_amount"] = new JsonObject { ["type"] = new JsonArray("number", "null"), ["minimum"] = 0 },
            ["provider_cost_currency"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["pattern"] = "^[A-Z]{3}$" }
        }
    };

    internal static JsonObject AllowedReferenceSchema(IEnumerable<string> aliases)
    {
        var values = new JsonArray();
        foreach (var alias in aliases)
        {
            values.Add(alias);
        }

        return new JsonObject
        {
            ["type"] = "string",
            ["enum"] = values
        };
    }

}
