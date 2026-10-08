using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Ai;
using Desktop.Application.Templates;
using Desktop.Contracts.Validation;
using Desktop.Domain.Templates;

namespace Desktop.Infrastructure.Ai;

public sealed partial class VolcArkAiAdapter
{
    public async Task<TemplateRuleAiExecution> InterpretTemplateRulesAsync(TemplateRuleAiRequest request, CancellationToken cancellationToken)
    {
        AiProviderUsage? measuredUsage = null;
        TemplateRuleAiExecution Fail(AiProviderOutcome outcome, string code, string message) => new(outcome, null, measuredUsage,
            new(code, outcome == AiProviderOutcome.RetryableError, "provider", message));
        if (request.SchemaVersion != TemplateRuleAiProtocol.RequestVersion || request.ModelVersion != TemplateRuleAiProtocol.ModelVersion
            || request.PromptVersion != TemplateRuleAiProtocol.PromptVersion || request.DraftVersion < 0
            || request.Pages.IsDefaultOrEmpty || request.Pages.Length > 256 || request.Frames.IsDefault || request.Frames.Length > 1024
            || request.AllowedTargets.IsDefaultOrEmpty || request.Aliases.IsDefault || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 8000)
            return Fail(AiProviderOutcome.TerminalError, "TPL_RULE_AI_REQUEST_INVALID", "模板规则请求不符合合同。");
        string? credential;
        try { credential = await _credentialSource.GetCredentialAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Fail(AiProviderOutcome.Cancelled, "AI_REQUEST_CANCELLED", "请求已取消。"); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return Fail(AiProviderOutcome.TerminalError, "AI_PROVIDER_CREDENTIAL_UNAVAILABLE", "无法读取本机凭据，请在设置中检查。"); }
        if (string.IsNullOrWhiteSpace(credential)) return Fail(AiProviderOutcome.TerminalError, "AI_PROVIDER_CREDENTIAL_MISSING", "请先在设置中配置 AI。");
        if (credential.Any(character => character is '\r' or '\n' or '\0')) return Fail(AiProviderOutcome.TerminalError, "AI_PROVIDER_CREDENTIAL_INVALID", "本机凭据格式无效。");
        var schema = TemplateRuleAiPayload.Schema();
        string prompt;
        try { prompt = TemplateRuleAiPayload.Prompt(request); }
        catch (TemplateRuleViolationException error) { return Fail(AiProviderOutcome.TerminalError, error.Code, "模板上下文过大，请缩小编辑范围。"); }
        var payload = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["model"] = _options.ModelId,
            ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = prompt }) }),
            ["thinking"] = new JsonObject { ["type"] = "disabled" }, ["max_output_tokens"] = 8192,
            ["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_schema", ["name"] = "template_rule_result_v1", ["strict"] = true, ["schema"] = schema } }
        }.ToJsonString());
        if (payload.Length > _options.MaxRequestBytes) return Fail(AiProviderOutcome.TerminalError, "AI_PROVIDER_REQUEST_TOO_LARGE", "请求超过安全大小上限。");
        var trace = new AiTransportTrace(_transportLogger, request.RequestId, TemplateRuleAiProtocol.RequestVersion, Stopwatch.StartNew(), payload.Length, 0, request.Frames.Length, _options);
        trace.MarkPayloadReady();
        var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        { Content = new ObservableByteArrayContent(payload, trace.MarkUploadProgress, trace.MarkUploadCompleted) };
        ConfigureTransportRequest(message); message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var scope = new HttpRequestScope(message, _options, cancellationToken);
        Task<HttpResponseMessage>? send = null;
        Task<byte[]>? read = null;
        try
        {
            trace.MarkSendStarted(); send = _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, scope.TransportToken);
            var response = await scope.WaitForResponseHeadersAsync(send, trace).ConfigureAwait(false);
            scope.AttachResponse(response); trace.MarkResponseHeaders(response);
            if (!response.IsSuccessStatusCode)
            {
                var failure = MapReferenceHttpFailure(response.StatusCode, Stopwatch.StartNew());
                return new(failure.Outcome, null, failure.Usage, failure.Error);
            }
            if (response.Content.Headers.ContentLength > _options.MaxResponseBytes) return Fail(AiProviderOutcome.TerminalError, "AI_PROVIDER_RESPONSE_TOO_LARGE", "响应过大，已拒绝。");
            trace.MarkResponseBodyStarted(); read = ReadLimitedResponseAsync(response, _options.MaxResponseBytes, scope.TransportToken);
            var body = await scope.WaitForResponseBodyAsync(read, trace).ConfigureAwait(false); trace.MarkResponseBodyCompleted(body);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var usage = measuredUsage = ParseProviderUsage(root);
            var status = root.TryGetProperty("status", out var statusValue) && statusValue.ValueKind == JsonValueKind.String ? statusValue.GetString() : null;
            if (status is "queued" or "in_progress" or "processing") return new(AiProviderOutcome.AiResultUnknown, null, usage, new("AI_PROVIDER_RESULT_PENDING", false, "provider", "供应商结果未知，不会自动重发。"));
            if (status is not ("completed" or "complete")) return new(AiProviderOutcome.TerminalError, null, usage, new("AI_PROVIDER_STATUS_INVALID", false, "provider", "供应商未完成规则解释。"));
            var output = ExtractOutputText(root);
            if (output is null || !_contractValidator.Validate(ContractDocumentKind.TemplateRuleAiResult, output).IsValid)
                return new(AiProviderOutcome.TerminalError, null, usage, new("TPL_RULE_AI_RESPONSE_INVALID", false, "provider", "规则结果不符合严格合同，原草稿保留。"));
            var result = TemplateRuleCodec.Deserialize<TemplateRuleAiResponse>(output);
            TemplateRuleInterpretationService.ValidateResponse(request, result);
            return new(AiProviderOutcome.Completed, result, usage, null);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or HttpRequestException or IOException)
        {
            if (read is not null) scope.DeferBodyCleanup(read, trace.MarkLateBodyCompleted, trace.MarkLateFailure);
            else if (send is not null) scope.DeferSendCleanup(send, trace.MarkLateResponseHeaders, trace.MarkLateFailure);
            // Cancelling local waiting after SendAsync is not evidence that the
            // provider rejected the paid request. Keep its original identity.
            if (cancellationToken.IsCancellationRequested) return Fail(AiProviderOutcome.AiResultUnknown,
                "AI_REQUEST_CANCELLED_AFTER_SUBMISSION", "已停止等待；供应商结果未确定，不会自动重发。");
            var failure = error is TimeoutException or OperationCanceledException ? trace.CreateTimeoutFailure(error) : trace.CreateNetworkFailure(error);
            return Fail(failure.Outcome, failure.Code, failure.UserAction);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { return Fail(AiProviderOutcome.TerminalError, "TPL_RULE_AI_RESPONSE_INVALID", "响应无法安全应用，原草稿保留。"); }
    }
}
