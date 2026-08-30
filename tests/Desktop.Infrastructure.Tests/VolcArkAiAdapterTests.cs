using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Ai;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Ai;

namespace Desktop.Infrastructure.Tests;

public sealed class VolcArkAiAdapterTests
{
    [RealProbeFact]
    public async Task RealProbeThroughFormalAdapter()
    {
        var copyPath = Environment.GetEnvironmentVariable("AI01_PROBE_COPY_PATH");
        if (string.IsNullOrWhiteSpace(copyPath) || !File.Exists(copyPath)) return;

        var handler = new HttpClientHandler();
        var diagnostics = new List<AiResponseShapeDiagnostic>();
        var adapter = new VolcArkAiAdapter(
            new HttpClient(handler),
            new WindowsCredentialSource(),
            new ProbeCopySource(copyPath),
            new JsonSchemaContractValidator(),
            new VolcArkAiOptions(ResponseShapeDiagnosticSink: diagnostics.Add));
        var result = await adapter.AnalyzeAsync(Request(), CancellationToken.None);
        var usage = result.Usage;
        foreach (var diagnostic in diagnostics)
        {
            Console.WriteLine($"REAL_PROBE_RESPONSE_SHAPE {JsonSerializer.Serialize(diagnostic)}");
        }
        Console.WriteLine(
            $"REAL_PROBE_METRICS outcome={result.Outcome} duration_ms={result.Duration.TotalMilliseconds:F0} "
            + $"input_bytes={new FileInfo(copyPath).Length} input_tokens={usage?.InputTokens ?? 0} "
            + $"output_tokens={usage?.OutputTokens ?? 0} total_tokens={usage?.TotalTokens ?? 0} "
            + $"cache_hit={usage?.CacheHit ?? false} error_code={result.Error?.Code ?? "none"}");
        if (result.Outcome != AiProviderOutcome.Completed || result.Result is null)
        {
            var shapeSummary = JsonSerializer.Serialize(diagnostics);
            Assert.Fail(
                $"REAL_PROBE_FAILURE outcome={result.Outcome} error_code={result.Error?.Code ?? "none"} "
                + $"duration_ms={result.Duration.TotalMilliseconds:F0} diagnostics={shapeSummary}");
        }
    }

    private sealed class RealProbeFactAttribute : FactAttribute
    {
        public RealProbeFactAttribute() => Skip = string.Equals(Environment.GetEnvironmentVariable("AI01_RUN_REAL_PROBE"), "1", StringComparison.Ordinal)
            && File.Exists(Environment.GetEnvironmentVariable("AI01_PROBE_COPY_PATH") ?? string.Empty)
            ? null
            : "Opt-in real provider probe with approved temporary copy";
    }

    [Fact]
    public async Task MapsCompletedResponsesAndRedactsProviderDetails()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                SuccessBody(),
                Encoding.UTF8,
                "application/json")
        });
        var copies = new FakeCopySource();
        var adapter = CreateAdapter(handler, copies);

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.True(execution.IsSuccess);
        Assert.Equal(AiProviderOutcome.Completed, execution.Outcome);
        Assert.Equal("request_01", execution.Result!.RequestId);
        Assert.Equal(3, execution.Usage!.InputTokens);
        Assert.Equal(4, execution.Usage.OutputTokens);
        Assert.Equal("copy_01", Assert.Single(copies.Released));
        Assert.Equal("Bearer test-key", handler.Authorization?.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, AiProviderOutcome.TerminalError, "AI_PROVIDER_BAD_REQUEST")]
    [InlineData(HttpStatusCode.Unauthorized, AiProviderOutcome.TerminalError, "AI_PROVIDER_AUTH_FAILED")]
    [InlineData((HttpStatusCode)429, AiProviderOutcome.RetryableError, "AI_PROVIDER_RATE_LIMITED")]
    [InlineData(HttpStatusCode.BadGateway, AiProviderOutcome.RetryableError, "AI_PROVIDER_TEMPORARY_FAILURE")]
    public async Task MapsHttpFailuresToStableOutcomes(
        HttpStatusCode statusCode,
        AiProviderOutcome outcome,
        string errorCode)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(statusCode));
        var adapter = CreateAdapter(handler, new FakeCopySource());

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(outcome, execution.Outcome);
        Assert.Equal(errorCode, execution.Error!.Code);
        Assert.DoesNotContain("test-key", execution.Error.UserAction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCredentialDoesNotSendRequest()
    {
        var handler = new StubHandler((Func<CancellationToken, HttpResponseMessage>)(_ =>
            throw new InvalidOperationException("should not send")));
        var adapter = CreateAdapter(handler, new FakeCopySource(), credential: null);

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_CREDENTIAL_MISSING", execution.Error!.Code);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task RejectsCredentialWithHeaderControlCharactersWithoutSendingRequest()
    {
        var handler = new StubHandler((Func<CancellationToken, HttpResponseMessage>)(_ =>
            throw new InvalidOperationException("should not send")));
        var adapter = CreateAdapter(handler, new FakeCopySource(), credential: "test-key\r\n");

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_CREDENTIAL_INVALID", execution.Error!.Code);
        Assert.Equal(0, handler.CallCount);
        Assert.DoesNotContain("test-key", execution.Error.UserAction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsCredentialWithEmbeddedNulWithoutSendingRequest()
    {
        var handler = new StubHandler((Func<CancellationToken, HttpResponseMessage>)(_ =>
            throw new InvalidOperationException("should not send")));
        var adapter = CreateAdapter(handler, new FakeCopySource(), credential: "test\0key");

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_CREDENTIAL_INVALID", execution.Error!.Code);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task CancellationAndTimeoutAreSeparatedFromUnknownResult()
    {
        var handler = new StubHandler(async cancellationToken =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var adapter = CreateAdapter(
            handler,
            new FakeCopySource(),
            options: new VolcArkAiOptions(RequestTimeout: TimeSpan.FromMilliseconds(25)));

        var timeout = await adapter.AnalyzeAsync(Request(), CancellationToken.None);
        Assert.Equal(AiProviderOutcome.AiResultUnknown, timeout.Outcome);
        Assert.Equal("AI_PROVIDER_TIMEOUT", timeout.Error!.Code);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellation = await adapter.AnalyzeAsync(Request(), cancelled.Token);
        Assert.Equal(AiProviderOutcome.Cancelled, cancellation.Outcome);
    }

    [Fact]
    public async Task RejectsMalformedOrMismatchedStructuredResults()
    {
        var malformed = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"completed\",\"output_text\":\"not-json\"}")
        });
        var adapter = CreateAdapter(malformed, new FakeCopySource());
        var invalid = await adapter.AnalyzeAsync(Request(), CancellationToken.None);
        Assert.Equal(AiProviderOutcome.TerminalError, invalid.Outcome);
        Assert.Equal("AI_PROVIDER_RESPONSE_INVALID", invalid.Error!.Code);

        var pending = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"in_progress\"}")
        });
        var pendingAdapter = CreateAdapter(pending, new FakeCopySource());
        var unknown = await pendingAdapter.AnalyzeAsync(Request(), CancellationToken.None);
        Assert.Equal(AiProviderOutcome.AiResultUnknown, unknown.Outcome);
        Assert.Equal("AI_PROVIDER_RESULT_PENDING", unknown.Error!.Code);
    }

    [Fact]
    public async Task EmitsSanitizedShapeForMalformedJsonOnlyWhenSinkEnabled()
    {
        AiResponseShapeDiagnostic? diagnostic = null;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-json")
        });
        var adapter = CreateAdapter(
            handler,
            new FakeCopySource(),
            options: new VolcArkAiOptions(ResponseShapeDiagnosticSink: value => diagnostic = value));

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.NotNull(diagnostic);
        Assert.Equal("invalid", diagnostic!.RootType);
        Assert.Equal("json_parse", diagnostic.FailureStage);
        Assert.Empty(diagnostic.TopLevelFields);
        Assert.DoesNotContain("not-json", JsonSerializer.Serialize(diagnostic), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmitsShapeForMissingOutputWithoutResponseValues()
    {
        AiResponseShapeDiagnostic? diagnostic = null;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"completed\",\"id\":\"secret-response-id\"}")
        });
        var adapter = CreateAdapter(
            handler,
            new FakeCopySource(),
            options: new VolcArkAiOptions(ResponseShapeDiagnosticSink: value => diagnostic = value));

        await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.NotNull(diagnostic);
        Assert.Equal("object", diagnostic!.RootType);
        Assert.Equal("completed", diagnostic.StatusClass);
        Assert.False(diagnostic.HasOutputText);
        Assert.Equal("missing_output_text", diagnostic.FailureStage);
        Assert.Contains("status", diagnostic.TopLevelFields);
        Assert.Contains("id", diagnostic.TopLevelFields);
        Assert.DoesNotContain("secret-response-id", JsonSerializer.Serialize(diagnostic), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmitsFirstSchemaFailurePathAndKeywordWithoutResponseBody()
    {
        var response = JsonNode.Parse(SuccessBody())!.AsObject();
        var structured = JsonNode.Parse(response["output"]![0]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        structured.Remove("request_id");
        response["output"]![0]!["content"]![0]!["text"] = structured.ToJsonString();

        AiResponseShapeDiagnostic? diagnostic = null;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json")
        });
        var adapter = CreateAdapter(
            handler,
            new FakeCopySource(),
            options: new VolcArkAiOptions(ResponseShapeDiagnosticSink: value => diagnostic = value));

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.NotNull(diagnostic);
        Assert.Equal("schema", diagnostic!.FailureStage);
        Assert.StartsWith("/", diagnostic.FirstSchemaFailurePath);
        Assert.Matches("^[A-Za-z0-9_-]+$", diagnostic.FirstSchemaFailureKeyword!);
        Assert.DoesNotContain("request_01", JsonSerializer.Serialize(diagnostic), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticSinkFailureCannotChangeProviderOutcome()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessBody(), Encoding.UTF8, "application/json")
        });
        var adapter = CreateAdapter(
            handler,
            new FakeCopySource(),
            options: new VolcArkAiOptions(ResponseShapeDiagnosticSink: _ => throw new InvalidOperationException("diagnostic-only")));

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.Completed, execution.Outcome);
    }

    [Fact]
    public async Task ProviderPayloadUsesExactResultItemSchema()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessBody(), Encoding.UTF8, "application/json")
        });
        var execution = await CreateAdapter(handler, new FakeCopySource()).AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.Completed, execution.Outcome);
        var payload = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        var schema = payload["text"]!["format"]!["schema"]!.AsObject();
        var match = schema["properties"]!["requirement_matches"]!["items"]!.AsObject();
        Assert.False(match["additionalProperties"]!.GetValue<bool>());
        Assert.Contains("requirement_id", match["required"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.DoesNotContain("matched", match["properties"]!.AsObject().Select(property => property.Key));
    }

    [Fact]
    public async Task PreparedLeaseCopyIsNotDeletedByProviderAdapter()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var copies = new FakeCopySource { IncludeLease = true };
        var execution = await CreateAdapter(handler, copies).AnalyzeAsync(Request(), CancellationToken.None);
        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Empty(copies.Released);
    }

    [Fact]
    public async Task RejectsCopyMetadataMismatchBeforeHttp()
    {
        var handler = new StubHandler((Func<CancellationToken, HttpResponseMessage>)(_ => throw new InvalidOperationException("should not send")));
        var copies = new FakeCopySource { MimeType = "image/gif" };
        var execution = await CreateAdapter(handler, copies).AnalyzeAsync(Request(), CancellationToken.None);
        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task RejectsResponseContentLengthBeforeReadingBody()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            };
            response.Content.Headers.ContentLength = 2048;
            return response;
        });
        var adapter = CreateAdapter(handler, new FakeCopySource(), options: new VolcArkAiOptions(MaxResponseBytes: 1024));
        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);
        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_RESPONSE_TOO_LARGE", execution.Error!.Code);
    }

    [Fact]
    public async Task RejectsChunkedResponseAtBoundedReadLimit()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new OversizedContent(2048)
        });
        var adapter = CreateAdapter(handler, new FakeCopySource(), options: new VolcArkAiOptions(MaxResponseBytes: 1024));
        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);
        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_RESPONSE_TOO_LARGE", execution.Error!.Code);
    }

    private static VolcArkAiAdapter CreateAdapter(
        HttpMessageHandler handler,
        FakeCopySource copies,
        string? credential = "test-key",
        VolcArkAiOptions? options = null) => new(
        new HttpClient(handler),
        new FakeCredentialSource(credential),
        copies,
        new JsonSchemaContractValidator(),
        options);

    private static AiAnalysisRequestV1 Request() => new(
        AiAnalysisProtocol.RequestSchemaVersion,
        "request",
        "request_01",
        "correlation_01",
        "product_01",
        "batch_01",
        "style_01",
        "snapshot_01",
        "revision_01",
        AiAnalysisKind.ProductJointUnderstanding,
        "model/proposed",
        "prompt/v1",
        AiAnalysisProtocol.ResultSchemaVersion,
        "compression/proposed",
        "consent_01",
        "sha256:" + new string('a', 64),
        [new AiAssetInputV1("asset_01", "sha256:" + new string('b', 64), "image/jpeg", 10, 10, "copy_01")],
        [new AiRequirementInputV1("frame_01", "主图", "居中", true)]);

    private static string SuccessBody()
    {
        var result = new AiAnalysisResultV1(
            AiAnalysisProtocol.ResultSchemaVersion,
            "result",
            "request_01",
            null,
            "model/proposed",
            [],
            [],
            [],
            null,
            [],
            new AiUsageSummaryV1("usage_request_01", 3, 4, 1, null, null));
        return JsonSerializer.Serialize(new
        {
            id = "resp_test_01",
            status = "completed",
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(result) } }
                }
            },
            usage = new { input_tokens = 3, output_tokens = 4, total_tokens = 7 }
        });
    }

    private sealed class FakeCredentialSource(string? credential) : IAiCredentialSource
    {
        public ValueTask<string?> GetCredentialAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(credential);
        }
    }

    private sealed class FakeCopySource : IAiAnalysisCopySource
    {
        public List<string> Released { get; } = [];
        public bool IncludeLease { get; set; }
        public string MimeType { get; set; } = "image/jpeg";

        public Task<AiAnalysisCopy> OpenAsync(string analysisCopyReference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AiAnalysisCopy(
                analysisCopyReference,
                MimeType,
                10,
                10,
                new byte[] { 1, 2, 3 },
                IncludeLease ? "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant() : null,
                3,
                "analysis-copy/v1",
                IncludeLease ? "lease-1" : null));
        }

        public Task ReleaseAsync(string analysisCopyReference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Released.Add(analysisCopyReference);
            return Task.CompletedTask;
        }
    }

    private sealed class ProbeCopySource(string path) : IAiAnalysisCopySource
    {
        public async Task<AiAnalysisCopy> OpenAsync(string analysisCopyReference, CancellationToken cancellationToken)
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var digest = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            return new AiAnalysisCopy(analysisCopyReference, "image/jpeg", 1, 1, bytes, digest, bytes.Length, "probe-temporary-v1", "probe-lease");
        }

        public Task ReleaseAsync(string analysisCopyReference, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubHandler(
        Func<CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _responder = responder;

        public StubHandler(Func<CancellationToken, HttpResponseMessage> responder)
            : this(token => Task.FromResult(responder(token)))
        {
        }

        public int CallCount { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Authorization = request.Headers.Authorization;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return await _responder(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class OversizedContent(int length) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await stream.WriteAsync(new byte[length]);
        protected override bool TryComputeLength(out long lengthValue) { lengthValue = -1; return false; }
    }
}
