using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Ai;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;

namespace Desktop.Infrastructure.Ai;

public sealed record VolcArkAiOptions(
    string ModelId = "doubao-seed-2-1-pro-260628",
    string Endpoint = "https://ark.cn-beijing.volces.com/api/v3/responses",
    TimeSpan? RequestTimeout = null,
    int MaxRequestBytes = 8 * 1024 * 1024,
    int MaxResponseBytes = 1 * 1024 * 1024,
    bool ReleaseCopiesAfterRequest = true)
{
    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(180);
}

/// <summary>
/// Supplier-neutral result contract used by the Application port. The
/// adapter deliberately returns a typed outcome instead of throwing provider
/// exceptions so callers can persist unknown outcomes without guessing.
/// </summary>
public sealed class VolcArkAiAdapter : IAiServicePort
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IAiCredentialSource _credentialSource;
    private readonly IAiAnalysisCopySource _copySource;
    private readonly IJsonContractValidator _contractValidator;
    private readonly VolcArkAiOptions _options;

    public VolcArkAiAdapter(
        HttpClient httpClient,
        IAiCredentialSource credentialSource,
        IAiAnalysisCopySource copySource,
        IJsonContractValidator contractValidator,
        VolcArkAiOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialSource = credentialSource ?? throw new ArgumentNullException(nameof(credentialSource));
        _copySource = copySource ?? throw new ArgumentNullException(nameof(copySource));
        _contractValidator = contractValidator ?? throw new ArgumentNullException(nameof(contractValidator));
        _options = options ?? new VolcArkAiOptions();
        ValidateOptions(_options);
    }

    public async Task<AiAnalysisExecutionResult> AnalyzeAsync(
        AiAnalysisRequestV1 request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ValidateRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            var credential = await _credentialSource.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(credential))
            {
                return Failure(
                    AiProviderOutcome.TerminalError,
                    "AI_PROVIDER_CREDENTIAL_MISSING",
                    retryable: false,
                    "请在本机凭据管理器配置 AI 测试凭据后重试。",
                    stopwatch);
            }

            // Header values must be a single opaque credential. Reject
            // malformed Credential Manager entries before constructing an
            // Authorization header, without logging or echoing the secret.
            if (credential.Any(character => character is '\r' or '\n' or '\0'))
            {
                return Failure(
                    AiProviderOutcome.TerminalError,
                    "AI_PROVIDER_CREDENTIAL_INVALID",
                    retryable: false,
                    "本机 AI 凭据格式无效，请在凭据管理器中重新保存单行 API Key。",
                    stopwatch);
            }

            var images = new List<AiAnalysisCopy>(request.Assets.Count);
            try
            {
                foreach (var asset in request.Assets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var copy = await _copySource.OpenAsync(asset.AnalysisCopyReference, cancellationToken)
                            .ConfigureAwait(false);
                        if (copy.Content.IsEmpty)
                        {
                            throw new InvalidDataException("AI 分析副本为空。");
                        }

                        ValidateCopy(asset, copy);

                        images.Add(copy);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException)
                    {
                        return Failure(AiProviderOutcome.TerminalError, "AI_ANALYSIS_COPY_UNAVAILABLE", false, "分析副本不可用，请重新扫描素材后重试。", stopwatch);
                    }
                }

                var estimatedPayloadBytes = EstimatePayloadBytes(request, images);
                if (estimatedPayloadBytes > _options.MaxRequestBytes)
                {
                    return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_REQUEST_TOO_LARGE", false, "本次分析素材过大，请减少素材或稍后重试。", stopwatch);
                }
                var payload = BuildProviderPayload(request, images);
                if (payload.Length > _options.MaxRequestBytes)
                {
                    return Failure(
                        AiProviderOutcome.TerminalError,
                        "AI_PROVIDER_REQUEST_TOO_LARGE",
                        retryable: false,
                        "本次分析素材过大，请减少素材或稍后重试。",
                        stopwatch);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.EffectiveRequestTimeout);
                using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
                {
                    Content = new ByteArrayContent(payload)
                };
                message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                // The credential is scoped to this request and is never put in
                // a shared HttpClient header or a persisted object.
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(
                        message,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return Failure(AiProviderOutcome.Cancelled, "AI_REQUEST_CANCELLED", false, "分析已取消。", stopwatch);
                }
                catch (OperationCanceledException)
                {
                    return Failure(AiProviderOutcome.AiResultUnknown, "AI_PROVIDER_TIMEOUT", false, "请求超时，结果状态未知，请查询或人工确认后再继续。", stopwatch);
                }
                catch (HttpRequestException)
                {
                    return Failure(AiProviderOutcome.AiResultUnknown, "AI_PROVIDER_NETWORK_UNKNOWN", false, "网络中断，供应商处理结果未知，请查询或人工确认后再继续。", stopwatch);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return MapHttpFailure(response.StatusCode, stopwatch);
                    }

                    if (response.Content.Headers.ContentLength is > 0 and var contentLength
                        && contentLength > _options.MaxResponseBytes)
                    {
                        return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_TOO_LARGE", false, "供应商响应过大，已安全拒绝。", stopwatch);
                    }

                    byte[] body;
                    try
                    {
                        body = await ReadLimitedResponseAsync(response, _options.MaxResponseBytes, timeout.Token).ConfigureAwait(false);
                    }
                    catch (InvalidDataException)
                    {
                        return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_TOO_LARGE", false, "供应商响应过大，已安全拒绝。", stopwatch);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return Failure(AiProviderOutcome.Cancelled, "AI_REQUEST_CANCELLED", false, "分析已取消。", stopwatch);
                    }
                    catch (OperationCanceledException)
                    {
                        return Failure(AiProviderOutcome.AiResultUnknown, "AI_PROVIDER_TIMEOUT", false, "请求超时，结果状态未知，请查询或人工确认后再继续。", stopwatch);
                    }

                    return ParseProviderResponse(request, body, stopwatch);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                return Failure(AiProviderOutcome.TerminalError, "AI_ANALYSIS_COPY_UNAVAILABLE", false, "分析副本不可用，请重新扫描素材后重试。", stopwatch);
            }
            finally
            {
                // Copy lifetime is owned by the application lease/coordinator.
                // The provider adapter never deletes a reference: retries,
                // refinement and restart may still need the same copy.
                // Legacy unleased test copies retain the historical opt-in cleanup
                // for compatibility; formally prepared copies always carry a lease
                // and are left to the application owner.
                if (_options.ReleaseCopiesAfterRequest)
                {
                    foreach (var copy in images.Where(copy => copy.LeaseId is null))
                    {
                        try { await _copySource.ReleaseAsync(copy.Reference, CancellationToken.None).ConfigureAwait(false); }
                        catch { /* cleanup is recoverable application-owned state */ }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(AiProviderOutcome.Cancelled, "AI_REQUEST_CANCELLED", false, "分析已取消。", stopwatch);
        }
        catch (InvalidOperationException)
        {
            return Failure(AiProviderOutcome.TerminalError, "AI_REQUEST_INVALID", false, "分析请求不符合当前版本要求，请重新扫描素材后重试。", stopwatch);
        }
    }

    private static async Task<byte[]> ReadLimitedResponseAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = maxBytes - (int)output.Length;
            if (remaining < 0) throw new InvalidDataException("AI_PROVIDER_RESPONSE_TOO_LARGE");
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maxBytes)
                throw new InvalidDataException("AI_PROVIDER_RESPONSE_TOO_LARGE");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void ValidateCopy(AiAssetInputV1 asset, AiAnalysisCopy copy)
    {
        if (!string.Equals(copy.Reference, asset.AnalysisCopyReference, StringComparison.Ordinal)
            || !IsSupportedMime(copy.MimeType)
            || copy.PixelWidth <= 0 || copy.PixelHeight <= 0)
            throw new InvalidDataException("AI_ANALYSIS_COPY_METADATA_INVALID");
        var digest = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(copy.Content.Span)).ToLowerInvariant();
        if (copy.ContentDigest is not null && !string.Equals(copy.ContentDigest, digest, StringComparison.Ordinal))
            throw new InvalidDataException("AI_ANALYSIS_COPY_DIGEST_MISMATCH");
        if (copy.ByteLength is not null && copy.ByteLength != copy.Content.Length)
            throw new InvalidDataException("AI_ANALYSIS_COPY_LENGTH_MISMATCH");
        if (asset.AnalysisCopyDigest is not null && !string.Equals(asset.AnalysisCopyDigest, digest, StringComparison.Ordinal))
            throw new InvalidDataException("AI_ANALYSIS_COPY_DIGEST_MISMATCH");
        if (asset.AnalysisCopyLength is not null && asset.AnalysisCopyLength != copy.Content.Length)
            throw new InvalidDataException("AI_ANALYSIS_COPY_LENGTH_MISMATCH");
        if (asset.AnalysisCopyMimeType is not null && !string.Equals(asset.AnalysisCopyMimeType, copy.MimeType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("AI_ANALYSIS_COPY_MIME_MISMATCH");
        if (asset.AnalysisCopyCompressionProfile is not null && !string.Equals(asset.AnalysisCopyCompressionProfile, copy.CompressionProfileVersion, StringComparison.Ordinal))
            throw new InvalidDataException("AI_ANALYSIS_COPY_PROFILE_MISMATCH");
    }

    private static bool IsSupportedMime(string value) => value is "image/jpeg" or "image/png" or "image/tiff";

    private static long EstimatePayloadBytes(AiAnalysisRequestV1 request, List<AiAnalysisCopy> images)
    {
        long total = 8_192;
        for (var index = 0; index < images.Count; index++)
        {
            var length = images[index].ByteLength ?? images[index].Content.Length;
            if (length < 0 || length > (long.MaxValue - total - 64) / 2)
                return long.MaxValue;
            total += 64 + ((length + 2) / 3) * 4;
            if (total > int.MaxValue) return long.MaxValue;
        }
        return total;
    }

    private AiAnalysisExecutionResult ParseProviderResponse(
        AiAnalysisRequestV1 request,
        byte[] body,
        Stopwatch stopwatch)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据无法校验，已进入待处理状态。", stopwatch);
        }

        using (document)
        {
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;
            if (string.Equals(status, "in_progress", StringComparison.Ordinal)
                || string.Equals(status, "queued", StringComparison.Ordinal))
            {
                return Failure(AiProviderOutcome.AiResultUnknown, "AI_PROVIDER_RESULT_PENDING", false, "供应商仍在处理，当前结果未知，请稍后查询。", stopwatch);
            }

            var outputText = ExtractOutputText(root);
            if (string.IsNullOrWhiteSpace(outputText))
            {
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商未返回结构化分析结果。", stopwatch);
            }

            JsonObject resultObject;
            try
            {
                resultObject = JsonNode.Parse(outputText)?.AsObject()
                    ?? throw new JsonException();
            }
            catch (JsonException)
            {
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据不是有效 JSON。", stopwatch);
            }

            var providerReference = ReadSafeProviderReference(root);
            if (!resultObject.ContainsKey("provider_request_ref"))
            {
                resultObject["provider_request_ref"] = providerReference;
            }

            var usage = ParseUsage(root, resultObject, request.RequestId);
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
            var validation = _contractValidator.Validate(ContractDocumentKind.AiAnalysis, serialized);
            if (!validation.IsValid)
            {
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据不符合结构化合同。", stopwatch);
            }

            AiAnalysisResultV1 result;
            try
            {
                result = JsonSerializer.Deserialize<AiAnalysisResultV1>(serialized, JsonOptions)
                    ?? throw new JsonException();
            }
            catch (JsonException)
            {
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_INVALID", false, "供应商返回的数据无法读取。", stopwatch);
            }

            if (!string.Equals(result.RequestId, request.RequestId, StringComparison.Ordinal)
                || !string.Equals(result.ModelConfigVersion, request.ModelConfigVersion, StringComparison.Ordinal))
            {
                return Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_REFERENCE_MISMATCH", false, "供应商结果与当前分析请求不匹配。", stopwatch);
            }

            stopwatch.Stop();
            return AiAnalysisExecutionResult.Completed(result, usage, stopwatch.Elapsed);
        }
    }

    private static AiProviderUsage? ParseUsage(JsonElement root, JsonObject resultObject, string requestId)
    {
        JsonElement usage;
        if (root.TryGetProperty("usage", out var providerUsage))
        {
            usage = providerUsage;
        }
        else if (resultObject["usage"] is JsonObject resultUsage)
        {
            var input = resultUsage["input_units"]?.GetValue<int>() ?? 0;
            var output = resultUsage["output_units"]?.GetValue<int>() ?? 0;
            return new AiProviderUsage(input, output, input + output, false);
        }
        else
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

        var cacheHit = usage.TryGetProperty("cached_tokens", out var cached)
            && cached.ValueKind == JsonValueKind.Number
            && cached.GetInt32() > 0;
        return new AiProviderUsage(inputTokens, outputTokens, totalTokens, cacheHit);
    }

    private static int ReadNonNegativeInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            && parsed >= 0
            ? parsed
            : 0;

    private static string? ExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var direct) && direct.ValueKind == JsonValueKind.String)
        {
            return direct.GetString();
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    var type = part.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                    if (type is "output_text" or "text" or null)
                    {
                        return text.GetString();
                    }
                }
            }
        }

        return null;
    }

    private static AiAnalysisExecutionResult MapHttpFailure(HttpStatusCode statusCode, Stopwatch stopwatch) =>
        statusCode switch
        {
            HttpStatusCode.BadRequest => Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_BAD_REQUEST", false, "分析请求被供应商拒绝，请检查素材和合同后重试。", stopwatch),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_AUTH_FAILED", false, "AI 凭据无效或已撤回，请更新本机凭据后重试。", stopwatch),
            (HttpStatusCode)429 => Failure(AiProviderOutcome.RetryableError, "AI_PROVIDER_RATE_LIMITED", true, "供应商暂时限流，请稍后重试。", stopwatch),
            >= HttpStatusCode.InternalServerError and <= (HttpStatusCode)599 => Failure(AiProviderOutcome.RetryableError, "AI_PROVIDER_TEMPORARY_FAILURE", true, "供应商暂时不可用，请稍后重试。", stopwatch),
            _ => Failure(AiProviderOutcome.TerminalError, "AI_PROVIDER_HTTP_ERROR", false, "供应商请求失败，请检查连接和服务状态。", stopwatch)
        };

    private static AiAnalysisExecutionResult Failure(
        AiProviderOutcome outcome,
        string code,
        bool retryable,
        string userAction,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return AiAnalysisExecutionResult.Failure(
            outcome,
            new ContractError(code, retryable, "provider", userAction),
            stopwatch.Elapsed);
    }

    private byte[] BuildProviderPayload(AiAnalysisRequestV1 request, List<AiAnalysisCopy> images)
    {
        var content = new JsonArray();
        for (var index = 0; index < request.Assets.Count; index++)
        {
            var asset = request.Assets[index];
            var copy = images[index];
            content.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = "data:" + copy.MimeType + ";base64," + Convert.ToBase64String(copy.Content.Span)
            });
        }

        var requirementSummary = JsonSerializer.Serialize(request.Requirements, JsonOptions);
        content.Add(new JsonObject
        {
            ["type"] = "input_text",
            ["text"] = "请仅返回符合 ai-analysis-result/v1 的 JSON 对象。request_id="
                + request.RequestId + "，model_config_version=" + request.ModelConfigVersion
                + "。按以下品类中立图片框需求选择素材并给出证据，不要输出 Markdown 或隐藏推理："
                + requirementSummary
        });

        var root = new JsonObject
        {
            ["model"] = _options.ModelId,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = content
                }
            },
            ["thinking"] = new JsonObject { ["type"] = "disabled" },
            ["max_output_tokens"] = 2048,
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = "ai_analysis_result",
                    ["strict"] = true,
                    ["schema"] = ResultSchema()
                }
            }
        };

        return Encoding.UTF8.GetBytes(root.ToJsonString(JsonOptions));
    }

    private static JsonObject ResultSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("schema_version", "document_kind", "request_id", "model_config_version", "requirement_matches", "variant_groups", "subject_geometry", "size_table_candidate", "warnings", "usage"),
        ["properties"] = new JsonObject
        {
            ["schema_version"] = new JsonObject { ["const"] = "ai-analysis-result/v1" },
            ["document_kind"] = new JsonObject { ["const"] = "result" },
            ["request_id"] = new JsonObject { ["type"] = "string" },
            ["provider_request_ref"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
            ["model_config_version"] = new JsonObject { ["type"] = "string" },
            ["requirement_matches"] = new JsonObject { ["type"] = "array" },
            ["variant_groups"] = new JsonObject { ["type"] = "array" },
            ["subject_geometry"] = new JsonObject { ["type"] = "array" },
            ["size_table_candidate"] = new JsonObject { ["type"] = new JsonArray("object", "null") },
            ["warnings"] = new JsonObject { ["type"] = "array" },
            ["usage"] = new JsonObject { ["type"] = "object" }
        }
    };

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

    private static void ValidateOptions(VolcArkAiOptions options)
    {
        if (!string.Equals(options.ModelId, "doubao-seed-2-1-pro-260628", StringComparison.Ordinal)
            || !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || options.EffectiveRequestTimeout <= TimeSpan.Zero
            || options.EffectiveRequestTimeout > TimeSpan.FromMinutes(5)
            || options.MaxRequestBytes is < 1024 or > 16 * 1024 * 1024
            || options.MaxResponseBytes is < 1024 or > 16 * 1024 * 1024)
        {
            throw new ArgumentException("火山方舟配置不符合已批准的安全边界。", nameof(options));
        }
    }

    private static void ValidateRequest(AiAnalysisRequestV1 request)
    {
        if (!string.Equals(request.SchemaVersion, AiAnalysisProtocol.RequestSchemaVersion, StringComparison.Ordinal)
            || !string.Equals(request.DocumentKind, "request", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.RequestId)
            || request.Assets.Count == 0
            || request.Requirements.Count == 0)
        {
            throw new InvalidOperationException("AI_ANALYSIS_REQUEST_INVALID");
        }
    }
}
