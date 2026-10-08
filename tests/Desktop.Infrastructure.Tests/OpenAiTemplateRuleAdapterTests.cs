using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Desktop.Application.Ai;
using Desktop.Application.Templates;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Ai;

namespace Desktop.Infrastructure.Tests;

public sealed class OpenAiTemplateRuleAdapterTests
{
    [Fact]
    public async Task BothRealAdapterImplementationsReturnTheSameValidatedContract()
    {
        var expected = new TemplateRuleAiResponse(TemplateRuleAiProtocol.ResultVersion, [new("frame", "假模特穿目标衣服背面")], [], "已修改", null);
        JsonNode? openAiRequest = null;
        using var openAiHttp = new HttpClient(new Handler(async (message, token) =>
        {
            Assert.Equal("https://api.openai.com/v1/responses", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            openAiRequest = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token));
            return Success(expected);
        }));
        using var arkHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(new JsonObject { ["status"] = "completed", ["output_text"] = TemplateRuleCodec.Serialize(expected), ["usage"] = Usage() }.ToJsonString()) })));
        var openAi = new OpenAiTemplateRuleAdapter(openAiHttp, new Credential(), new JsonSchemaContractValidator());
        var ark = new VolcArkAiAdapter(arkHttp, new Credential(), new NoCopies(), new JsonSchemaContractValidator());
        var first = await openAi.InterpretTemplateRulesAsync(Request(), default);
        var second = await ark.InterpretTemplateRulesAsync(Request() with { ModelVersion = TemplateRuleAiProtocol.ModelVersion }, default);
        Assert.Equal(AiProviderOutcome.Completed, first.Outcome); Assert.Equal(second.Outcome, first.Outcome);
        Assert.Equal(TemplateRuleCodec.Serialize(second.Response), TemplateRuleCodec.Serialize(first.Response));
        Assert.Equal(second.Usage, first.Usage);
        Assert.False(openAiRequest!["store"]!.GetValue<bool>());
        Assert.Null(openAiRequest["thinking"]);
        Assert.Equal(OpenAiTemplateRuleAdapter.Identity.Model, openAiRequest["model"]!.GetValue<string>());
        var body = openAiRequest.ToJsonString();
        Assert.DoesNotContain("input_image", body); Assert.DoesNotContain("test-secret", body);
        Assert.Equal("string", openAiRequest["text"]!["format"]!["schema"]!["properties"]!["schemaVersion"]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("queued", AiProviderOutcome.AiResultUnknown, "AI_PROVIDER_RESULT_PENDING")]
    [InlineData("in_progress", AiProviderOutcome.AiResultUnknown, "AI_PROVIDER_RESULT_PENDING")]
    [InlineData("incomplete", AiProviderOutcome.TerminalError, "AI_PROVIDER_STATUS_INVALID")]
    [InlineData("failed", AiProviderOutcome.TerminalError, "AI_PROVIDER_STATUS_INVALID")]
    public async Task PendingAndIncompleteNeverApplyRules(string status, AiProviderOutcome outcome, string code)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new JsonObject { ["status"] = status, ["usage"] = Usage() }))));
        var result = await new OpenAiTemplateRuleAdapter(http, new Credential(), new JsonSchemaContractValidator()).InterpretTemplateRulesAsync(Request(), default);
        Assert.Equal(outcome, result.Outcome); Assert.Equal(code, result.Error!.Code); Assert.Null(result.Response); Assert.NotNull(result.Usage);
    }

    [Theory]
    [InlineData(429, AiProviderOutcome.RetryableError)]
    [InlineData(503, AiProviderOutcome.AiResultUnknown)]
    [InlineData(408, AiProviderOutcome.AiResultUnknown)]
    [InlineData(401, AiProviderOutcome.TerminalError)]
    [InlineData(302, AiProviderOutcome.TerminalError)]
    public async Task HttpFailureDoesNotRetryOrChangeProvider(int code, AiProviderOutcome outcome)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)); }));
        var result = await new OpenAiTemplateRuleAdapter(http, new Credential(), new JsonSchemaContractValidator()).InterpretTemplateRulesAsync(Request(), default);
        Assert.Equal(outcome, result.Outcome); Assert.Equal(1, calls); Assert.Null(result.Response);
    }

    [Fact]
    public async Task DeadlineBoundsUncooperativeHandlerAndKeepsResultUnknown()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((_, _) => release.Task));
        var result = await new OpenAiTemplateRuleAdapter(http, new Credential(), new JsonSchemaContractValidator(), TimeSpan.FromMilliseconds(60))
            .InterpretTemplateRulesAsync(Request(), default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AiProviderOutcome.AiResultUnknown, result.Outcome);
        release.SetResult(Success(new(TemplateRuleAiProtocol.ResultVersion, [], [], "待补充", "哪个框？")));
    }

    [Theory]
    [InlineData("refusal", "AI_PROVIDER_REFUSED")]
    [InlineData("tool_call", "TPL_RULE_AI_RESPONSE_INVALID")]
    public async Task RefusalsAndUnexpectedContentCannotBecomePatches(string kind, string code)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new JsonObject
        { ["status"] = "completed", ["output"] = new JsonArray(new JsonObject { ["type"] = "message",
            ["content"] = new JsonArray(new JsonObject { ["type"] = kind, ["text"] = "untrusted" }) }) }))));
        var result = await new OpenAiTemplateRuleAdapter(http, new Credential(), new JsonSchemaContractValidator()).InterpretTemplateRulesAsync(Request(), default);
        Assert.Equal(code, result.Error!.Code); Assert.Null(result.Response);
    }

    [Fact]
    public async Task WrongTaskIdentityOrPreCancellationNeverSend()
    {
        var calls = 0; using var http = new HttpClient(new Handler((_, _) => { calls++; throw new InvalidOperationException(); }));
        var adapter = new OpenAiTemplateRuleAdapter(http, new Credential(), new JsonSchemaContractValidator());
        Assert.Equal("TPL_RULE_AI_REQUEST_INVALID", (await adapter.InterpretTemplateRulesAsync(Request() with { ModelVersion = TemplateRuleAiProtocol.ModelVersion }, default)).Error!.Code);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Equal(AiProviderOutcome.Cancelled, (await adapter.InterpretTemplateRulesAsync(Request(), cancel.Token)).Outcome);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("{\"provider\":\"arbitrary-endpoint\"}")]
    [InlineData("{\"provider\":\"openai-candidate\",\"endpoint\":\"https://attacker.invalid\"}")]
    [InlineData("{\"provider\":\"volc-ark\",\"provider\":\"openai-candidate\"}")]
    public void ConfigurationRejectsArbitraryEndpointsAndDuplicateProperties(string content)
    {
        using var directory = new TestDirectory(); Directory.CreateDirectory(Path.Combine(directory.Path, "ai"));
        File.WriteAllText(Path.Combine(directory.Path, "ai/template-rule-provider.json"), content);
        Assert.Throws<InvalidDataException>(() => TemplateRuleProviderConfiguration.Read(directory.Path));
    }

    private static TemplateRuleAiRequest Request() => new(TemplateRuleAiProtocol.RequestVersion, "request", TemplateRuleAiProtocol.PromptVersion,
        OpenAiTemplateRuleAdapter.Identity.ConfigurationVersion, 1, "sha256:" + new string('a', 64), "@1 框1 改为假模特穿目标衣服背面",
        [new("page", "页面", false)], [new("page", "frame", "正面")], [new("page", null, 1)], null, [new("page", "frame")]);
    private static JsonObject Usage() => new() { ["input_tokens"] = 10, ["output_tokens"] = 10, ["total_tokens"] = 20 };
    private static HttpResponseMessage Success(TemplateRuleAiResponse result) => Json(new JsonObject
    { ["status"] = "completed", ["usage"] = Usage(), ["output"] = new JsonArray(new JsonObject { ["type"] = "message",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = TemplateRuleCodec.Serialize(result) }) }) });
    private static HttpResponseMessage Json(JsonNode json) => new(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class Credential : IAiCredentialSource
    { public ValueTask<string?> GetCredentialAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<string?>("test-secret"); } }
    private sealed class NoCopies : IAiAnalysisCopySource
    {
        public Task<AiAnalysisCopy> OpenAsync(string reference, CancellationToken token) => throw new NotSupportedException();
        public Task ReleaseAsync(string reference, CancellationToken token) => throw new NotSupportedException();
    }
}
