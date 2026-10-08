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

public sealed partial class VolcArkAiAdapter
{
    private byte[] BuildReferenceLearningPayload(
        ReferenceLearningAiRequestV2 request,
        List<AiAnalysisCopy> images)
    {
        var providerFrames = request.Frames.Select((frame, index) => new
        {
            frame_id = $"f_{index + 1:D2}",
            page_id = frame.PageId,
            reference_asset_id = frame.ReferenceAssetId,
            page_context = frame.PageContext,
            frame_context = frame.FrameContext,
            layout_intent = frame.LayoutIntent
        }).ToArray();
        var content = new JsonArray();
        for (var index = 0; index < request.Assets.Count; index++)
        {
            // Keep the stable asset identity immediately adjacent to the
            // image it names. A trailing alias table after a run of images
            // lets a multimodal model shift one photo's semantics onto the
            // next frame even though the local frame/asset references close.
            content.Add(new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = $"REFERENCE_ASSET asset_id={request.Assets[index].AssetId} frame_ids="
                    + string.Join(",", providerFrames
                        .Where(frame => string.Equals(
                            frame.reference_asset_id,
                            request.Assets[index].AssetId,
                            StringComparison.Ordinal))
                        .Select(frame => frame.frame_id))
            });
            content.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = "data:" + images[index].MimeType + ";base64," + Convert.ToBase64String(images[index].Content.Span)
            });
        }

        var framePrompts = JsonSerializer.Serialize(providerFrames, JsonOptions);
        const string instruction = "每个 REFERENCE_ASSET 文本后紧邻的下一张图片就是该 asset_id，frame_ids 是只允许读取这张图片的框，不得将不同照片的内容互换。"
            + "结合每框已匹配的参考图，为未来不同款商品生成可复用的图片类别短语。selection_requirement 只保留未来候选图可重复判断的对象或商品部位、朝向、姿态、景别等必要区分，例如“人物正面端坐图”“人物背面全身图”“领口与尺码标细节图”“袖口工艺特写图”“面料纹理特写图”“成分信息图”；示例不是固定枚举。"
            + "禁止复述单张参考照片，禁止写衣服或商品颜色、背景、墙面、地面、家具、道具、饰品、光线、色调、人物身份等偶然属性。selection_requirement 必须是 2至40 个字符的单一类别短语，不得包含逗号、句号、分号、冒号或换行；判断理由只写入 positive_evidence 和 exclusion_evidence。"
            + "frame_context 和 layout_intent 仅是本地结构审计元数据，不能被改写进 selection_requirement；不能单独根据文件名、图层名、尺寸、位置或版式臆测语义。rankings 必须为空。必须为每个给定 frame_id 返回恰好一个 frame_results 元素，并逐字复制给定的短 frame_id（例如 f_01），不得改写、翻译、合并或新增 ID。";
        content.Add(new JsonObject
        {
            ["type"] = "input_text",
            ["text"] = "只返回符合 reference-learning-ai-result/v2 的 JSON，不输出 Markdown 或隐藏推理。operation="
                + request.Operation + "，request_id=" + request.RequestId
                + "，model_config_version=" + request.ModelConfigVersion + "。" + instruction
                + "图片框上下文：" + framePrompts
        });

        var root = new JsonObject
        {
            ["model"] = _options.ModelId,
            ["input"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = content
            }),
            ["thinking"] = new JsonObject { ["type"] = "disabled" },
            ["max_output_tokens"] = 4096,
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = "reference_learning_ai_result_v2",
                    ["strict"] = true,
                    ["schema"] = ReferenceLearningResultSchema(request.Operation)
                }
            }
        };
        return Encoding.UTF8.GetBytes(root.ToJsonString(JsonOptions));
    }

    private ReferenceLearningAiExecutionResult ParseReferenceLearningResponse(
        ReferenceLearningAiRequestV2 request,
        byte[] body,
        Stopwatch stopwatch)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(body); }
        catch (JsonException)
        {
            return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据无法校验。", stopwatch);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据无法校验。", stopwatch);
            }

            var status = root.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : null;
            var usage = ParseProviderUsage(root);
            if (status is "in_progress" or "queued" or "processing")
            {
                return ReferenceFailure(AiProviderOutcome.AiResultUnknown, "AI_PROVIDER_RESULT_PENDING", false, "供应商仍在处理，结果未知。", stopwatch, usage);
            }
            if (string.Equals(status, "incomplete", StringComparison.Ordinal))
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_OUTPUT_INCOMPLETE", false, "供应商未完整生成语义结果，当前响应不能使用；请人工检查后再决定是否建立新请求。", stopwatch, usage);
            }
            if (string.Equals(status, "failed", StringComparison.Ordinal))
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_REQUEST_FAILED", false, "供应商明确返回语义请求失败；请检查任务后再决定是否建立新请求。", stopwatch, usage);
            }
            if (status is "cancelled" or "canceled")
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_REQUEST_CANCELLED", false, "供应商明确返回语义请求已取消；请检查任务后再决定是否建立新请求。", stopwatch, usage);
            }
            if (status is not ("completed" or "complete"))
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_STATUS_INVALID", false, "供应商返回了无法确认的语义请求状态，已安全拒绝。", stopwatch, usage);
            }
            var outputText = ExtractOutputText(root);
            if (string.IsNullOrWhiteSpace(outputText))
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商未返回结构化语义结果。", stopwatch, usage);
            }

            JsonObject resultObject;
            try { resultObject = JsonNode.Parse(outputText)?.AsObject() ?? throw new JsonException(); }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商语义结果不是有效 JSON。", stopwatch, usage);
            }

            usage ??= ParseResultUsage(resultObject);
            if (!resultObject.ContainsKey("usage"))
            {
                resultObject["usage"] = new JsonObject
                {
                    ["usage_event_id"] = "usage_" + request.RequestId,
                    ["input_units"] = usage?.InputTokens ?? 0,
                    ["output_units"] = usage?.OutputTokens ?? 0,
                    ["request_count"] = 1,
                    ["provider_cost_amount"] = null,
                    ["provider_cost_currency"] = null
                };
            }
            var serialized = resultObject.ToJsonString(JsonOptions);
            if (!_contractValidator.Validate(ContractDocumentKind.ReferenceLearningAi, serialized).IsValid)
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商语义结果不符合 v2 合同。", stopwatch, usage);
            }

            ReferenceLearningAiResultV2 result;
            try { result = JsonSerializer.Deserialize<ReferenceLearningAiResultV2>(serialized, JsonOptions) ?? throw new JsonException(); }
            catch (JsonException)
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商语义结果无法读取。", stopwatch, usage);
            }
            result = result with
            {
                FrameResults = result.FrameResults
                    .Select(frame => TryMapReferenceFrameAlias(request, frame))
                    .ToArray()
            };
            if (!string.Equals(result.RequestId, request.RequestId, StringComparison.Ordinal)
                || !string.Equals(result.Operation, request.Operation, StringComparison.Ordinal)
                || !string.Equals(result.ModelConfigVersion, request.ModelConfigVersion, StringComparison.Ordinal)
                || result.FrameResults.Select(item => item.FrameId).Distinct(StringComparer.Ordinal).Count() != result.FrameResults.Count
                || result.FrameResults.Any(item => request.Frames.All(frame => !string.Equals(frame.FrameId, item.FrameId, StringComparison.Ordinal))))
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_REFERENCE_MISMATCH", false, "供应商语义结果引用了当前请求之外的数据。", stopwatch, usage);
            }
            if (request.Operation == ReferenceLearningAiOperation.GenerateSelectionRequirements
                && result.FrameResults.Count != request.Frames.Count)
            {
                return ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "语义生成结果缺少图片框或包含不允许的候选排名。", stopwatch, usage);
            }
            stopwatch.Stop();
            return ReferenceLearningAiExecutionResult.Completed(result, usage, stopwatch.Elapsed);
        }
    }

    private static ReferenceLearningFrameResultV2 TryMapReferenceFrameAlias(
        ReferenceLearningAiRequestV2 request,
        ReferenceLearningFrameResultV2 frame)
    {
        if (request.Frames.Any(candidate =>
                string.Equals(candidate.FrameId, frame.FrameId, StringComparison.Ordinal)))
        {
            return frame;
        }

        if (!frame.FrameId.StartsWith("f_", StringComparison.Ordinal)
            || !int.TryParse(frame.FrameId.AsSpan(2), out var ordinal)
            || ordinal < 1
            || ordinal > request.Frames.Count)
        {
            return frame;
        }

        // The provider sees short deterministic aliases because long random
        // frame IDs are easy for a vision model to mistype in a large result.
        // Mapping is positional and remains closed over this request; unknown,
        // duplicate, missing, or out-of-range IDs still fail the checks below.
        return frame with { FrameId = request.Frames[ordinal - 1].FrameId };
    }

    private static void ValidateReferenceCopy(
        ReferenceLearningAiRequestV2 request,
        AiAssetInputV1 asset,
        AiAnalysisCopy copy)
    {
        if (copy.Content.IsEmpty
            || !string.Equals(copy.Reference, asset.AnalysisCopyReference, StringComparison.Ordinal)
            || !IsSupportedMime(copy.MimeType)
            || copy.PixelWidth <= 0 || copy.PixelHeight <= 0)
            throw new InvalidDataException("AI_ANALYSIS_COPY_METADATA_INVALID");
        var digest = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(copy.Content.Span)).ToLowerInvariant();
        if (!string.Equals(copy.ContentDigest, digest, StringComparison.Ordinal)
            || copy.ByteLength != copy.Content.Length
            || !string.Equals(asset.AnalysisCopyDigest, digest, StringComparison.Ordinal)
            || asset.AnalysisCopyLength != copy.Content.Length
            || !string.Equals(asset.AnalysisCopyMimeType, copy.MimeType, StringComparison.Ordinal)
            || !string.Equals(asset.AnalysisCopyLeaseId, copy.LeaseId, StringComparison.Ordinal)
            || !string.Equals(asset.AnalysisCopyCompressionProfile, request.CompressionProfileVersion, StringComparison.Ordinal)
            || !string.Equals(copy.CompressionProfileVersion, request.CompressionProfileVersion, StringComparison.Ordinal))
            throw new InvalidDataException("AI_ANALYSIS_COPY_IDENTITY_MISMATCH");
    }

    private static JsonObject ReferenceLearningResultSchema(string operation) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("schema_version", "document_kind", "operation", "request_id", "model_config_version", "frame_results", "warnings", "usage"),
        ["properties"] = new JsonObject
        {
            ["schema_version"] = new JsonObject { ["const"] = ReferenceLearningAiProtocol.ResultSchemaVersion },
            ["document_kind"] = new JsonObject { ["const"] = "result" },
            ["operation"] = new JsonObject { ["const"] = operation },
            ["request_id"] = IdSchema(),
            ["model_config_version"] = StringSchema(1, 128),
            ["frame_results"] = ArraySchema(ReferenceFrameResultSchema(), 200),
            ["warnings"] = ArraySchema(WarningSchema(), 200),
            ["usage"] = UsageSchema()
        }
    };

    private static JsonObject ReferenceFrameResultSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("frame_id", "selection_requirement", "positive_evidence", "exclusion_evidence", "confidence"),
        ["properties"] = new JsonObject
        {
            ["frame_id"] = IdSchema(),
            ["selection_requirement"] = ReferenceSelectionRequirementSchema(),
            ["positive_evidence"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 8, ["uniqueItems"] = true, ["items"] = StringSchema(1, 200) },
            ["exclusion_evidence"] = new JsonObject { ["type"] = "array", ["maxItems"] = 8, ["uniqueItems"] = true, ["items"] = StringSchema(1, 200) },
            ["confidence"] = UnitIntervalSchema()
        }
    };

    private static JsonObject ReferenceSelectionRequirementSchema() => new()
    {
        ["type"] = "string",
        ["description"] = "未来不同商品可复用的图片类别短语，只写对象或部位、朝向、姿态、景别等必要区分，不得复述参考照片的颜色、背景、家具、道具、饰品、光线或其他偶然细节。",
        ["minLength"] = ReferenceSelectionRequirementPolicy.MinLength,
        ["maxLength"] = ReferenceSelectionRequirementPolicy.MaxLength,
        ["pattern"] = "^[^,，.。;；:：\\r\\n]+$"
    };

    private static void ValidateReferenceLearningRequest(ReferenceLearningAiRequestV2 request, int maxAssets)
    {
        if (!string.Equals(request.SchemaVersion, ReferenceLearningAiProtocol.RequestSchemaVersion, StringComparison.Ordinal)
            || !string.Equals(request.DocumentKind, "request", StringComparison.Ordinal)
            || !ReferenceLearningAiOperation.IsSupported(request.Operation)
            || !string.Equals(
                request.PromptContractVersion,
                ReferenceLearningAiProtocol.SemanticPromptVersion,
                StringComparison.Ordinal)
            || !string.Equals(request.ResponseSchemaVersion, ReferenceLearningAiProtocol.ResultSchemaVersion, StringComparison.Ordinal)
            || !IsApprovedCompressionProfile(request.CompressionProfileVersion)
            || request.Assets.Count is 0 || request.Assets.Count > maxAssets
            || request.Frames.Count is 0 or > 200
            || request.Assets.Select(asset => asset.AssetId).Distinct(StringComparer.Ordinal).Count() != request.Assets.Count
            || request.Frames.Select(frame => frame.FrameId).Distinct(StringComparer.Ordinal).Count() != request.Frames.Count
            || request.Assets.Any(asset => asset.AnalysisCopyDigest is null
                || asset.AnalysisCopyLength is null or <= 0
                || asset.AnalysisCopyMimeType is null
                || asset.AnalysisCopyCompressionProfile is null
                || asset.AnalysisCopyLeaseId is null)
            || request.Assets.Any(asset => request.Frames.All(frame =>
                !string.Equals(frame.ReferenceAssetId, asset.AssetId, StringComparison.Ordinal)))
            || request.Frames.Any(frame => string.IsNullOrWhiteSpace(frame.ReferenceAssetId)
                || request.Assets.All(asset => !string.Equals(asset.AssetId, frame.ReferenceAssetId, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("TPL_LEARN_AI_REQUEST_INVALID");
        }
    }

    private static ReferenceLearningAiExecutionResult MapReferenceHttpFailure(HttpStatusCode statusCode, Stopwatch stopwatch) => statusCode switch
    {
        HttpStatusCode.BadRequest => ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_BAD_REQUEST", false, "语义分析请求被供应商拒绝。", stopwatch),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_AUTH_FAILED", false, "AI 凭据无效或已撤回。", stopwatch),
        (HttpStatusCode)429 => ReferenceFailure(AiProviderOutcome.RetryableError, "AI_PROVIDER_RATE_LIMITED", true, "供应商暂时限流，请稍后重试。", stopwatch),
        >= HttpStatusCode.InternalServerError and <= (HttpStatusCode)599 => ReferenceFailure(AiProviderOutcome.RetryableError, "AI_PROVIDER_TEMPORARY_FAILURE", true, "供应商暂时不可用，请稍后重试。", stopwatch),
        _ => ReferenceFailure(AiProviderOutcome.TerminalError, "AI_PROVIDER_HTTP_ERROR", false, "供应商请求失败，请检查连接和服务状态。", stopwatch)
    };

    private static ReferenceLearningAiExecutionResult ReferenceFailure(
        AiProviderOutcome outcome,
        string code,
        bool retryable,
        string userAction,
        Stopwatch stopwatch,
        AiProviderUsage? usage = null)
    {
        stopwatch.Stop();
        return ReferenceLearningAiExecutionResult.Failure(
            outcome,
            new ContractError(code, retryable, "provider", userAction),
            stopwatch.Elapsed,
            usage);
    }

    private string RequestTooLargeUserAction(int actualBytes) =>
        $"最终 UTF-8 JSON 请求体为 {actualBytes:N0} bytes，超过应用本地传输预算 {_options.MaxRequestBytes:N0} bytes（当前为 16 MiB，不是火山 API 硬限制）；本次未发送 AI 请求。请减少本款照片后重新建立批次。";

    private static string? ReadSafeProviderReference(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = id.GetString();
        return value is not null
            && value.Length <= 256
            && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or ':' or '-')
            ? value
            : null;
    }

}
