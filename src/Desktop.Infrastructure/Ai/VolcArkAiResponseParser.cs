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

internal sealed class VolcArkAiResponseParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IJsonContractValidator _contractValidator;
    private readonly Action<AiResponseShapeDiagnostic>? _diagnosticSink;

    public VolcArkAiResponseParser(IJsonContractValidator contractValidator, Action<AiResponseShapeDiagnostic>? diagnosticSink)
    {
        _contractValidator = contractValidator ?? throw new ArgumentNullException(nameof(contractValidator));
        _diagnosticSink = diagnosticSink;
    }

    internal AiAnalysisExecutionResult Parse(
        AiAnalysisRequestV1 request,
        byte[] body,
        Stopwatch stopwatch)
    {
        var shape = new ResponseShapeCollector(body.Length);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            EmitDiagnostic(shape.Build("json_parse"));
            return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据无法校验，已进入待处理状态。", stopwatch);
        }

        using (document)
        {
            var root = document.RootElement;
            shape.SetRoot(root);
            if (root.ValueKind != JsonValueKind.Object)
            {
                EmitDiagnostic(shape.Build("root_type"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据无法校验，已进入待处理状态。", stopwatch);
            }

            var status = root.TryGetProperty("status", out var statusElement)
                && statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : null;
            shape.SetStatus(ClassifyStatus(status));
            shape.SetOutputShape(root);
            var usage = ParseProviderUsage(root);
            if (status is "in_progress" or "queued" or "processing")
            {
                EmitDiagnostic(shape.Build("status_pending"));
                return Failure(AiProviderOutcome.AiResultUnknown, "AI_PROVIDER_RESULT_PENDING", false, "供应商仍在处理，当前结果未知，请稍后查询。", stopwatch, usage);
            }

            if (string.Equals(status, "incomplete", StringComparison.Ordinal))
            {
                EmitDiagnostic(shape.Build("status_incomplete"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_OUTPUT_INCOMPLETE", false, "供应商未完整生成结构化结果，当前响应不能使用；请人工检查后再决定是否建立新请求。", stopwatch, usage);
            }

            if (string.Equals(status, "failed", StringComparison.Ordinal))
            {
                EmitDiagnostic(shape.Build("status_failed"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_REQUEST_FAILED", false, "供应商明确返回请求失败；请检查任务后再决定是否建立新请求。", stopwatch, usage);
            }

            if (status is "cancelled" or "canceled")
            {
                EmitDiagnostic(shape.Build("status_cancelled"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_REQUEST_CANCELLED", false, "供应商明确返回请求已取消；请检查任务后再决定是否建立新请求。", stopwatch, usage);
            }

            if (status is not ("completed" or "complete"))
            {
                EmitDiagnostic(shape.Build("status_invalid"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_STATUS_INVALID", false, "供应商返回了无法确认的请求状态，已安全拒绝。", stopwatch, usage);
            }

            var outputText = ExtractOutputText(root);
            if (string.IsNullOrWhiteSpace(outputText))
            {
                EmitDiagnostic(shape.Build("missing_output_text"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商未返回结构化分析结果。", stopwatch, usage);
            }

            JsonObject resultObject;
            try
            {
                resultObject = JsonNode.Parse(outputText)?.AsObject()
                    ?? throw new JsonException();
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                EmitDiagnostic(shape.Build("output_json_parse"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据不是有效 JSON。", stopwatch, usage);
            }

            var providerReference = ReadSafeProviderReference(root);
            if (!resultObject.ContainsKey("provider_request_ref"))
            {
                resultObject["provider_request_ref"] = providerReference;
            }

            usage ??= ParseResultUsage(resultObject);
            if (!resultObject.ContainsKey("usage") && usage is not null)
            {
                resultObject["usage"] = new JsonObject
                {
                    ["usage_event_id"] = "usage_" + request.RequestId,
                    ["input_units"] = usage.InputTokens,
                    ["output_units"] = usage.OutputTokens,
                    ["request_count"] = 1,
                    ["provider_cost_amount"] = null,
                    ["provider_cost_currency"] = null
                };
            }

            var serialized = resultObject.ToJsonString(JsonOptions);
            ContractValidationResult validation;
            string? firstSchemaFailurePath = null;
            string? firstSchemaFailureKeyword = null;
            if (_contractValidator is IJsonContractDiagnosticValidator diagnosticValidator)
            {
                var diagnostic = diagnosticValidator.ValidateWithDiagnostic(ContractDocumentKind.AiAnalysis, serialized);
                validation = diagnostic.Validation;
                firstSchemaFailurePath = diagnostic.FirstFailureInstancePath;
                firstSchemaFailureKeyword = diagnostic.FirstFailureKeyword;
            }
            else
            {
                validation = _contractValidator.Validate(ContractDocumentKind.AiAnalysis, serialized);
            }

            if (!validation.IsValid)
            {
                EmitDiagnostic(shape.Build("schema", firstSchemaFailurePath, firstSchemaFailureKeyword));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据不符合结构化合同。", stopwatch, usage);
            }

            AiAnalysisResultV1 result;
            try
            {
                result = JsonSerializer.Deserialize<AiAnalysisResultV1>(serialized, JsonOptions)
                    ?? throw new JsonException();
            }
            catch (JsonException)
            {
                EmitDiagnostic(shape.Build("result_deserialize"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据无法读取。", stopwatch, usage);
            }

            if (!(string.Equals(result.RequestId, request.RequestId, StringComparison.Ordinal)
                    || string.Equals(
                        result.RequestId,
                        AiAnalysisResultReferenceNormalizer.RequestAlias,
                        StringComparison.Ordinal))
                || !(string.Equals(result.ModelConfigVersion, request.ModelConfigVersion, StringComparison.Ordinal)
                    || string.Equals(
                        result.ModelConfigVersion,
                        AiAnalysisResultReferenceNormalizer.ModelConfigVersionAlias,
                        StringComparison.Ordinal)))
            {
                EmitDiagnostic(shape.Build("reference_mismatch"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_REFERENCE_MISMATCH", false, "供应商结果与当前分析请求不匹配。", stopwatch, usage);
            }

            try
            {
                result = AiAnalysisResultReferenceNormalizer.Normalize(request, result);
            }
            catch (InvalidDataException exception) when (AiAnalysisResultSemanticValidator.IsSemanticError(exception))
            {
                EmitDiagnostic(shape.Build("semantic"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的分析结果结构不一致，已安全拒绝。", stopwatch, usage);
            }
            catch (InvalidDataException)
            {
                EmitDiagnostic(shape.Build("reference_closure"));
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_REFERENCE_MISMATCH", false, "供应商结果引用了当前请求之外的图片或图片框。", stopwatch, usage);
            }

            EmitDiagnostic(shape.Build("none"));
            stopwatch.Stop();
            return AiAnalysisExecutionResult.Completed(result, usage, stopwatch.Elapsed);
        }
    }

    private void EmitDiagnostic(AiResponseShapeDiagnostic diagnostic)
    {
        var sink = _diagnosticSink;
        if (sink is null)
        {
            return;
        }

        try
        {
            sink(diagnostic);
        }
        catch
        {
            // Diagnostics are strictly best-effort and must never alter the
            // provider outcome or expose callback failures to users.
        }
    }

    private static string ClassifyStatus(string? status) => status switch
    {
        "completed" or "complete" => "completed",
        "in_progress" or "queued" or "processing" => "pending",
        "incomplete" => "incomplete",
        "failed" or "cancelled" or "canceled" => "terminal",
        null or "" => "missing",
        _ => "other"
    };

    private sealed class ResponseShapeCollector(int responseBytes)
    {
        private string _rootType = "invalid";
        private readonly List<string> _topLevelFields = [];
        private int _topLevelFieldCount;
        private string _statusClass = "missing";
        private bool _hasOutputText;
        private readonly List<string> _outputBlockTypes = [];

        public void SetRoot(JsonElement root)
        {
            _rootType = root.ValueKind switch
            {
                JsonValueKind.Object => "object",
                JsonValueKind.Array => "array",
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Null => "null",
                _ => "other"
            };

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    _topLevelFieldCount++;
                    if (_topLevelFields.Count >= 64)
                    {
                        continue;
                    }

                    _topLevelFields.Add(SanitizeName(property.Name));
                }
            }
        }

        public void SetStatus(string statusClass) => _statusClass = statusClass;

        public void SetOutputShape(JsonElement root)
        {
            _hasOutputText = root.TryGetProperty("output_text", out _);
            if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in output.EnumerateArray())
            {
                if (_outputBlockTypes.Count >= 64)
                {
                    break;
                }

                if (item.ValueKind != JsonValueKind.Object)
                {
                    _outputBlockTypes.Add("other");
                    continue;
                }

                if (item.TryGetProperty("type", out var itemType) && itemType.ValueKind == JsonValueKind.String)
                {
                    _outputBlockTypes.Add(SanitizeName(itemType.GetString() ?? "other"));
                }

                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in content.EnumerateArray())
                {
                    if (_outputBlockTypes.Count >= 64)
                    {
                        break;
                    }

                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("type", out var partType)
                        && partType.ValueKind == JsonValueKind.String)
                    {
                        _outputBlockTypes.Add(SanitizeName(partType.GetString() ?? "other"));
                    }
                }
            }
        }

        public AiResponseShapeDiagnostic Build(
            string failureStage,
            string? firstSchemaFailurePath = null,
            string? firstSchemaFailureKeyword = null) => new(
            responseBytes,
            _rootType,
            _topLevelFieldCount,
            _topLevelFields.ToArray(),
            _statusClass,
            _hasOutputText,
            _outputBlockTypes.ToArray(),
            failureStage,
            firstSchemaFailurePath,
            firstSchemaFailureKeyword);

        private static string SanitizeName(string value)
        {
            if (value.Length is 0 or > 64
                || !value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
            {
                return "other";
            }

            return value;
        }
    }

    internal static AiProviderUsage? ParseProviderUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var inputTokens = ReadNonNegativeInt(usage, "input_tokens");
        var outputTokens = ReadNonNegativeInt(usage, "output_tokens");
        var totalTokens = ReadNonNegativeInt(usage, "total_tokens");
        if (totalTokens == 0)
        {
            totalTokens = inputTokens + outputTokens;
        }

        var cachedInputTokens = usage.TryGetProperty("cached_tokens", out var cached)
            && cached.ValueKind == JsonValueKind.Number
            && cached.TryGetInt32(out var cachedValue)
            ? Math.Max(0, cachedValue)
            : 0;
        return new AiProviderUsage(inputTokens, outputTokens, totalTokens, cachedInputTokens > 0, cachedInputTokens).Normalize();
    }

    internal static AiProviderUsage? ParseResultUsage(JsonObject resultObject)
    {
        if (resultObject["usage"] is not JsonObject resultUsage)
        {
            return null;
        }

        var input = resultUsage["input_units"]?.GetValue<int>() ?? 0;
        var output = resultUsage["output_units"]?.GetValue<int>() ?? 0;
        return new AiProviderUsage(input, output, input + output, false);
    }

    private static int ReadNonNegativeInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            && parsed >= 0
            ? parsed
            : 0;

    internal static string? ExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var direct) && direct.ValueKind == JsonValueKind.String)
        {
            return direct.GetString();
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string>? textSegments = null;
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (part.TryGetProperty("type", out var typeElement)
                    && (typeElement.ValueKind != JsonValueKind.String
                        || typeElement.GetString() is not ("output_text" or "text")))
                {
                    continue;
                }

                textSegments ??= [];
                textSegments.Add(text.GetString() ?? string.Empty);
            }
        }

        return textSegments is null ? null : string.Concat(textSegments);
    }

    internal static AiAnalysisExecutionResult MapHttpFailure(HttpStatusCode statusCode, Stopwatch stopwatch) =>
        statusCode switch
        {
            HttpStatusCode.BadRequest => Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_BAD_REQUEST", false, "分析请求被供应商拒绝，请检查素材和合同后重试。", stopwatch),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_AUTH_FAILED", false, "AI 凭据无效或已撤回，请更新本机凭据后重试。", stopwatch),
            (HttpStatusCode)429 => Failure(AiProviderOutcome.RetryableError, "AI_PROVIDER_RATE_LIMITED", true, "供应商暂时限流，请稍后重试。", stopwatch),
            >= HttpStatusCode.InternalServerError and <= (HttpStatusCode)599 => Failure(AiProviderOutcome.RetryableError, "AI_PROVIDER_TEMPORARY_FAILURE", true, "供应商暂时不可用，请稍后重试。", stopwatch),
            _ => Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_HTTP_ERROR", false, "供应商请求失败，请检查连接和服务状态。", stopwatch)
        };

    internal static AiAnalysisExecutionResult Failure(
        AiProviderOutcome outcome,
        string code,
        bool retryable,
        string userAction,
        Stopwatch stopwatch,
        AiProviderUsage? usage = null)
    {
        stopwatch.Stop();
        return AiAnalysisExecutionResult.Failure(
            outcome,
            new ContractError(code, retryable, "provider", userAction),
            stopwatch.Elapsed,
            usage);
    }

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
