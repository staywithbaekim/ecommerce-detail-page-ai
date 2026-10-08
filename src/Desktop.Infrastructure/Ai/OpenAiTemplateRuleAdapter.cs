using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Ai;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Templates;

namespace Desktop.Infrastructure.Ai;

/// <summary>Candidate text-only Responses implementation. No image capability, retry or fallback.</summary>
public sealed class OpenAiTemplateRuleAdapter : IAiServicePort
{
    public const string CredentialTarget = "EcommerceDetailPage.AI.OpenAI.v1";
    public static AiTaskProviderIdentity Identity { get; } = new(
        "openai", "gpt-4.1-2025-04-14", "openai-gpt-4.1-2025-04-14-tplrules-v1", "ai-template-rules-openai");
    public AiTaskProviderIdentity TemplateRulesIdentity => Identity;
    private readonly HttpClient http;
    private readonly IAiCredentialSource credentials;
    private readonly IJsonContractValidator validator;
    private readonly TimeSpan timeout;
    private const int MaximumBytes = 1024 * 1024;

    public OpenAiTemplateRuleAdapter(HttpClient http, IAiCredentialSource credentials, IJsonContractValidator validator, TimeSpan? timeout = null)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.timeout = timeout ?? TimeSpan.FromSeconds(120);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(120)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public Task<AiAnalysisExecutionResult> AnalyzeAsync(AiAnalysisRequestV1 request, CancellationToken cancellationToken) =>
        Task.FromResult(AiAnalysisExecutionResult.Failure(AiProviderOutcome.TerminalError,
            new("AI_TASK_UNSUPPORTED", false, "provider", "此候选适配器只支持文字规则解释，尚未验收照片或尺码分析。"), TimeSpan.Zero));

    public async Task<TemplateRuleAiExecution> InterpretTemplateRulesAsync(TemplateRuleAiRequest request, CancellationToken cancellationToken)
    {
        AiProviderUsage? usage = null;
        TemplateRuleAiExecution Fail(AiProviderOutcome outcome, string code) => new(outcome, null, usage,
            new(code, outcome == AiProviderOutcome.RetryableError, "provider", "规则请求未完成，原草稿保留；未决结果不会自动重发。"));
        if (!TemplateRuleAiPayload.IsValid(request, Identity.ConfigurationVersion))
            return Fail(AiProviderOutcome.TerminalError, "TPL_RULE_AI_REQUEST_INVALID");
        string? credential;
        try { credential = await credentials.GetCredentialAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Fail(AiProviderOutcome.Cancelled, "AI_REQUEST_CANCELLED"); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return Fail(AiProviderOutcome.TerminalError, "AI_PROVIDER_CREDENTIAL_UNAVAILABLE"); }
        if (string.IsNullOrWhiteSpace(credential)) return Fail(AiProviderOutcome.TerminalError, "AI_PROVIDER_CREDENTIAL_MISSING");
        if (credential.Any(c => c is '\r' or '\n' or '\0')) return Fail(AiProviderOutcome.TerminalError, "AI_PROVIDER_CREDENTIAL_INVALID");
        byte[] payload;
        try
        {
            var schema = TemplateRuleAiPayload.Schema();
            PrepareSchema(schema);
            // This dialect does not accept Ark's thinking property. Disable response
            // storage; images, tools, background work and remote URLs are not sent.
            payload = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["model"] = Identity.Model, ["store"] = false, ["max_output_tokens"] = 8192,
                ["input"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user", ["content"] = new JsonArray(new JsonObject
                    { ["type"] = "input_text", ["text"] = TemplateRuleAiPayload.Prompt(request) })
                }),
                ["text"] = new JsonObject { ["format"] = new JsonObject
                { ["type"] = "json_schema", ["name"] = "template_rule_result_v1", ["strict"] = true, ["schema"] = schema } }
            }.ToJsonString());
        }
        catch (TemplateRuleViolationException error) { return Fail(AiProviderOutcome.TerminalError, error.Code); }
        if (payload.Length > MaximumBytes) return Fail(AiProviderOutcome.TerminalError, "AI_PROVIDER_REQUEST_TOO_LARGE");
        if (cancellationToken.IsCancellationRequested) return Fail(AiProviderOutcome.Cancelled, "AI_REQUEST_CANCELLED");
        var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
        { Content = new ByteArrayContent(payload) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(timeout);
        // Lifetime/response cleanup belongs to the transport task. If a handler ignores
        // cancellation, the caller still returns at the deadline and observes late faults.
        var transport = SendAsync(message, lifetime);
        TransportResult received;
        try { received = await transport.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or HttpRequestException or IOException)
        {
            try { lifetime.Cancel(); } catch (ObjectDisposedException) { /* transport already completed */ }
            _ = ObserveAsync(transport);
            // Once SendAsync starts, cancellation or I/O failure is not proof that no
            // paid operation was accepted. Never downgrade this to retryable.
            return Fail(AiProviderOutcome.AiResultUnknown, "AI_PROVIDER_RESULT_UNKNOWN");
        }
        if (received.Status is HttpStatusCode.TooManyRequests) return Fail(AiProviderOutcome.RetryableError, "AI_PROVIDER_RATE_LIMITED");
        if ((int)received.Status >= 500 || received.Status is HttpStatusCode.RequestTimeout) return Fail(AiProviderOutcome.AiResultUnknown, "AI_PROVIDER_RESULT_UNKNOWN");
        if ((int)received.Status is < 200 or >= 300) return Fail(AiProviderOutcome.TerminalError, "AI_PROVIDER_HTTP_ERROR");
        if (received.TooLarge) return Fail(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_TOO_LARGE");
        try
        {
            using var document = JsonDocument.Parse(received.Body);
            var root = document.RootElement;
            RejectDuplicates(root);
            if (root.TryGetProperty("usage", out var measured) && measured.ValueKind != JsonValueKind.Null)
            {
                var input = measured.GetProperty("input_tokens").GetInt32();
                var output = measured.GetProperty("output_tokens").GetInt32();
                var total = measured.GetProperty("total_tokens").GetInt32();
                if (input < 0 || output < 0 || (long)input + output != total) throw new JsonException();
                usage = new(input, output, total, false);
            }
            var status = root.GetProperty("status").GetString();
            if (status is "queued" or "in_progress") return Fail(AiProviderOutcome.AiResultUnknown, "AI_PROVIDER_RESULT_PENDING");
            if (status != "completed") return Fail(AiProviderOutcome.TerminalError, "AI_PROVIDER_STATUS_INVALID");
            var texts = new List<string>();
            foreach (var item in root.GetProperty("output").EnumerateArray())
            {
                if (item.GetProperty("type").GetString() != "message") throw new JsonException();
                foreach (var content in item.GetProperty("content").EnumerateArray())
                {
                    var kind = content.GetProperty("type").GetString();
                    if (kind == "refusal") return Fail(AiProviderOutcome.TerminalError, "AI_PROVIDER_REFUSED");
                    if (kind != "output_text") throw new JsonException();
                    texts.Add(content.GetProperty("text").GetString() ?? throw new JsonException());
                }
            }
            if (texts.Count != 1 || !validator.Validate(ContractDocumentKind.TemplateRuleAiResult, texts[0]).IsValid) throw new JsonException();
            var result = TemplateRuleCodec.Deserialize<TemplateRuleAiResponse>(texts[0]);
            TemplateRuleInterpretationService.ValidateResponse(request, result);
            return new(AiProviderOutcome.Completed, result, usage, null);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return Fail(AiProviderOutcome.TerminalError, "TPL_RULE_AI_RESPONSE_INVALID"); }
    }

    private sealed record TransportResult(HttpStatusCode Status, byte[] Body, bool TooLarge);
    private async Task<TransportResult> SendAsync(HttpRequestMessage message, CancellationTokenSource lifetime)
    {
        using (lifetime) using (message)
        using (var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, lifetime.Token).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode) return new(response.StatusCode, [], false);
            if (response.Content.Headers.ContentLength > MaximumBytes) return new(response.StatusCode, [], true);
            await using var stream = await response.Content.ReadAsStreamAsync(lifetime.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > MaximumBytes) return new(response.StatusCode, [], true);
                output.Write(buffer, 0, count);
            }
            return new(response.StatusCode, output.ToArray(), false);
        }
    }
    private static async Task ObserveAsync(Task task) { try { await task.ConfigureAwait(false); } catch { /* no sensitive late exception logging */ } }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new JsonException(); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) RejectDuplicates(child);
    }
    private static void PrepareSchema(JsonNode node)
    {
        // Normalize const/type and remove only meta-schema identifiers. The stricter
        // repository schema is always applied again to received output.
        if (node is JsonObject obj)
        {
            obj.Remove("$schema"); obj.Remove("$id");
            if (obj.TryGetPropertyValue("const", out var constant))
            {
                obj["enum"] = new JsonArray(constant?.DeepClone());
                obj["type"] ??= constant is JsonValue v && v.TryGetValue<string>(out _) ? "string" : "integer";
                obj.Remove("const");
            }
            if (obj.ContainsKey("enum") && !obj.ContainsKey("type")) obj["type"] = "string";
            foreach (var child in obj.ToArray()) if (child.Value is not null) PrepareSchema(child.Value);
        }
        else if (node is JsonArray array) foreach (var child in array) if (child is not null) PrepareSchema(child);
    }
}
