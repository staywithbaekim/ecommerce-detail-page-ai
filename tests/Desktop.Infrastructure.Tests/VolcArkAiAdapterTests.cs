using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Ai;
using Desktop.Application.Inputs;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Inputs;

namespace Desktop.Infrastructure.Tests;

public sealed class VolcArkAiAdapterTests
{
    [RealProbeFact]
    public async Task RealProbeThroughFormalAdapter()
    {
        var inputRoot = Environment.GetEnvironmentVariable("AI01_PROBE_INPUT_ROOT");
        var runtimeRoot = Environment.GetEnvironmentVariable("AI01_PROBE_RUNTIME_ROOT");
        if (string.IsNullOrWhiteSpace(inputRoot)
            || string.IsNullOrWhiteSpace(runtimeRoot)
            || !Directory.Exists(inputRoot)) return;

        var sourcePaths = Directory.EnumerateFiles(inputRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(InputImageRules.IsSupportedExtension)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedAssetCount = int.TryParse(
            Environment.GetEnvironmentVariable("AI01_PROBE_EXPECTED_ASSET_COUNT"),
            out var parsedCount)
            ? parsedCount
            : sourcePaths.Length;
        Assert.InRange(expectedAssetCount, 1, 4);
        Assert.Equal(expectedAssetCount, sourcePaths.Length);

        var encodingOptions = new AiAnalysisCopyEncodingOptions();
        var policy = new LocalAppPathPolicy(runtimeRoot);
        using var preparer = new FileAiAnalysisCopyPreparer(policy, new AtomicFileStore(policy), encodingOptions);
        var copySource = new FileAiAnalysisCopySource(policy);
        var metadataReader = new StreamingAssetMetadataReader();
        var preparedAssets = new List<ProbePreparedAsset>(sourcePaths.Length);
        try
        {
            for (var index = 0; index < sourcePaths.Length; index++)
            {
                var sourcePath = sourcePaths[index];
                var sourceInfo = new FileInfo(sourcePath);
                if (sourceInfo.Length is <= 0 || sourceInfo.Length > encodingOptions.MaxSourceBytes)
                {
                    throw new InvalidDataException("AI_COPY_SOURCE_TOO_LARGE");
                }

                var relativePath = Path.GetFileName(sourcePath);
                var read = await metadataReader.ReadAsync(inputRoot, relativePath, CancellationToken.None);
                Assert.True(read.IsSuccess, read.Failure?.Code ?? "PROBE_SOURCE_METADATA_INVALID");
                var source = read.Metadata!;
                var assetId = $"asset_{index + 1:D2}";
                var copyReference = $"probe_copy_{index + 1:D2}";
                var sourceAsset = SourceAssetMetadata.CreateReadable(
                    assetId,
                    "style_01",
                    relativePath,
                    relativePath,
                    source.Length,
                    source.Sha256,
                    source.PixelWidth,
                    source.PixelHeight,
                    source.Format,
                    source.MimeType);
                var copy = await preparer.PrepareAsync(
                    new AiAnalysisCopyPreparationRequest(
                        copyReference,
                        inputRoot,
                        sourceAsset,
                        encodingOptions.CompressionProfileVersion),
                    CancellationToken.None);
                preparedAssets.Add(new ProbePreparedAsset(source, copy));
            }

            foreach (var (asset, index) in preparedAssets.Select((asset, index) => (asset, index)))
            {
                Console.WriteLine(
                    $"REAL_PROBE_ASSET index={index + 1} source_mime={asset.Source.MimeType} "
                    + $"source_dimensions={asset.Source.PixelWidth}x{asset.Source.PixelHeight} source_bytes={asset.Source.Length} "
                    + $"copy_mime={asset.Copy.MimeType} copy_dimensions={asset.Copy.PixelWidth}x{asset.Copy.PixelHeight} "
                    + $"copy_bytes={asset.Copy.ByteLength} profile={asset.Copy.CompressionProfileVersion}");
            }

            if (string.Equals(
                    Environment.GetEnvironmentVariable("AI01_PROBE_PREFLIGHT_ONLY"),
                    "1",
                    StringComparison.Ordinal))
            {
                Console.WriteLine(
                    $"REAL_PROBE_PREFLIGHT outcome=prepared http_requests=0 asset_count={preparedAssets.Count} "
                    + $"source_bytes={preparedAssets.Sum(asset => asset.Source.Length)} "
                    + $"copy_bytes={preparedAssets.Sum(asset => asset.Copy.ByteLength)}");
                return;
            }

            var requestAssets = preparedAssets.Select((asset, index) => new AiAssetInputV1(
                $"asset_{index + 1:D2}",
                "sha256:" + asset.Source.Sha256,
                asset.Source.MimeType,
                asset.Source.PixelWidth,
                asset.Source.PixelHeight,
                asset.Copy.Reference,
                asset.Copy.Digest,
                asset.Copy.ByteLength,
                asset.Copy.MimeType,
                asset.Copy.CompressionProfileVersion,
                asset.Copy.LeaseId)).ToArray();
            var probeProfile = Environment.GetEnvironmentVariable("AI01_PROBE_PROFILE") ?? "generic";
            var request = CreateProbeRequest(probeProfile) with
            {
                CompressionProfileVersion = encodingOptions.CompressionProfileVersion,
                Assets = requestAssets
            };
            var diagnostics = new List<AiResponseShapeDiagnostic>();
            using var handler = new ProbeCountingHandler();
            using var httpClient = CreateRealProbeHttpClient(handler);
            var adapter = new VolcArkAiAdapter(
                httpClient,
                new WindowsCredentialSource(),
                copySource,
                new JsonSchemaContractValidator(),
                new VolcArkAiOptions(
                    ReleaseCopiesAfterRequest: false,
                    ResponseShapeDiagnosticSink: diagnostics.Add));
            var result = await adapter.AnalyzeAsync(request, CancellationToken.None);
            var usage = result.Usage;
            var costEstimate = usage is null
                ? "unknown"
                : AiAnalysisProductionPolicy.BudgetV1.Pricing.Estimate(usage)
                    .ToString("0.######", CultureInfo.InvariantCulture);
            foreach (var diagnostic in diagnostics)
            {
                Console.WriteLine($"REAL_PROBE_RESPONSE_SHAPE {JsonSerializer.Serialize(diagnostic)}");
            }
            Console.WriteLine(
                $"REAL_PROBE_METRICS outcome={result.Outcome} http_requests={handler.CallCount} "
                + $"asset_count={preparedAssets.Count} duration_ms={result.Duration.TotalMilliseconds:F0} "
                + $"source_bytes={preparedAssets.Sum(asset => asset.Source.Length)} "
                + $"copy_bytes={preparedAssets.Sum(asset => asset.Copy.ByteLength)} "
                + $"input_tokens={usage?.InputTokens ?? 0} output_tokens={usage?.OutputTokens ?? 0} "
                + $"total_tokens={usage?.TotalTokens ?? 0} cache_hit={usage?.CacheHit ?? false} "
                + $"error_code={result.Error?.Code ?? "none"} "
                + $"cost_estimate_cny={costEstimate} cost_estimate_basis=official_list_price "
                + "billing_status=not_observed");
            if (result.Outcome != AiProviderOutcome.Completed || result.Result is null)
            {
                var shapeSummary = JsonSerializer.Serialize(diagnostics);
                Assert.Fail(
                    $"REAL_PROBE_FAILURE outcome={result.Outcome} error_code={result.Error?.Code ?? "none"} "
                    + $"duration_ms={result.Duration.TotalMilliseconds:F0} diagnostics={shapeSummary}");
            }

            Console.WriteLine("REAL_PROBE_RESULT " + JsonSerializer.Serialize(new
            {
                profile = probeProfile,
                result.Result.RequestId,
                request.AnalysisKind,
                requirement_matches = result.Result.RequirementMatches.Select(match => new
                {
                    match.RequirementId,
                    match.AssetId,
                    match.Rank,
                    match.Confidence,
                    match.EvidenceCodes
                }),
                variant_groups = result.Result.VariantGroups,
                subject_geometry = result.Result.SubjectGeometry,
                size_table_candidate = result.Result.SizeTableCandidate,
                warnings = result.Result.Warnings.Select(warning => new
                {
                    warning.Scope,
                    warning.Code
                })
            }));
        }
        finally
        {
            foreach (var asset in preparedAssets)
            {
                await preparer.ReleaseAsync(asset.Copy.Reference, asset.Copy.LeaseId, CancellationToken.None);
            }
        }
    }

    [Fact]
    public void RealProbeHttpClientLeavesTimeoutClassificationToFormalAdapter()
    {
        using var handler = new ProbeCountingHandler();
        using var client = CreateRealProbeHttpClient(handler);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    private static HttpClient CreateRealProbeHttpClient(HttpMessageHandler handler) =>
        new(handler) { Timeout = Timeout.InfiniteTimeSpan };

    private static AiAnalysisRequestV1 CreateProbeRequest(string profile)
    {
        var (analysisKind, requirements) = profile switch
        {
            "clothing-joint" => (
                AiAnalysisKind.ProductJointUnderstanding,
                new AiRequirementInputV1[]
                {
                    new("black_front", "选择黑色圆领印花短袖的完整正面商品图；黑、白、灰为独立变体，不能混用。", "subject_complete", true),
                    new("white_front", "选择白色圆领印花短袖的完整正面商品图；黑、白、灰为独立变体，不能混用。", "subject_complete", true),
                    new("gray_front", "选择灰色圆领印花短袖的完整正面商品图；黑、白、灰为独立变体，不能混用。", "subject_complete", true),
                    new("back_view", "选择当前商品任一已确认颜色变体的完整背面图；没有背面时必须报告缺失，不能用正面代替。", "subject_complete", true),
                    new("neckline_detail", "选择同款圆领领口细节图；没有独立细节时必须报告缺失。", "detail_clear", true),
                    new("cuff_detail", "选择同款袖口细节图；没有独立细节时必须报告缺失。", "detail_clear", true),
                    new("fabric_detail", "选择同款印花面料细节图，不能把其他变体或旧商品当作面料来源。", "detail_clear", true)
                }),
            "clothing-subject" => (
                AiAnalysisKind.SelectedAssetGeometry,
                new AiRequirementInputV1[]
                {
                    new("locked_subject", "当前图片已由人工锁定；只识别灰色圆领印花短袖的完整主体边界。若仍包含其他商品或边缘被裁切，必须返回 warning。", "subject_complete", true)
                }),
            "size-table" => (
                AiAnalysisKind.SizeTableExtraction,
                new AiRequirementInputV1[]
                {
                    new("size_table", "只提取图片中清晰可读的尺码表原文行列；不得猜测缺失数字，低清、缺失或来源冲突时必须返回 warning 且不返回候选。", "structured_size_table", true)
                }),
            "computer-case" => (
                AiAnalysisKind.ProductJointUnderstanding,
                new AiRequirementInputV1[]
                {
                    new("front_three_quarter", "选择电脑机箱完整的前侧三分之四视角，不能把背景、地面或装饰当作商品。", "subject_complete", true),
                    new("front_view", "选择电脑机箱完整正面视角，不能与背面或侧面混淆。", "subject_complete", true),
                    new("rear_view", "选择电脑机箱完整背面视角，不能与正面或侧面混淆。", "subject_complete", true),
                    new("side_view", "选择电脑机箱完整侧面视角，不能与正面或背面混淆。", "subject_complete", true)
                }),
            _ => (AiAnalysisKind.ProductJointUnderstanding, Request().Requirements.ToArray())
        };
        var normalized = profile.Replace('-', '_');
        var digestJson = JsonSerializer.Serialize(requirements.Select(requirement => new
        {
            requirement.RequirementId,
            requirement.SelectionRequirement,
            requirement.LayoutIntent,
            requirement.Required
        }).ToArray());
        return Request() with
        {
            RequestId = $"probe_{normalized}",
            CorrelationId = $"probe_{normalized}",
            ProductId = $"product_{normalized}",
            BatchId = $"batch_{normalized}",
            StyleId = $"style_{normalized}",
            AnalysisKind = analysisKind,
            RequirementsDigest = "sha256:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(digestJson))).ToLowerInvariant(),
            Requirements = requirements
        };
    }

    private sealed class RealProbeFactAttribute : FactAttribute
    {
        public RealProbeFactAttribute() => Skip = string.Equals(Environment.GetEnvironmentVariable("AI01_RUN_REAL_PROBE"), "1", StringComparison.Ordinal)
            && Directory.Exists(Environment.GetEnvironmentVariable("AI01_PROBE_INPUT_ROOT") ?? string.Empty)
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AI01_PROBE_RUNTIME_ROOT"))
            ? null
            : "Opt-in real provider probe with formally prepared approved temporary copies";
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

    [Fact]
    public async Task NormalizesTransportImageAliasesBeforeReturningResult()
    {
        var aliased = new AiAnalysisResultV1(
            AiAnalysisProtocol.ResultSchemaVersion,
            "result",
            "request_01",
            null,
            "model/proposed",
            [new AiRequirementMatchV1("frame_01", "img_0", 1, 0.98m, ["SUBJECT_COMPLETE"])],
            [new AiVariantGroupV1("variant_01", "款式", "默认", ["img_0"], true)],
            [new AiSubjectGeometryV1("img_0", 0.1m, 0.2m, 0.8m, 0.9m, 0.99m)],
            new AiSizeTableCandidateV1("img_0", 0.9m, ["尺码"], [["M"]]),
            [],
            new AiUsageSummaryV1("usage_request_01", 3, 4, 1, null, null));
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessBody(aliased), Encoding.UTF8, "application/json")
        });

        var execution = await CreateAdapter(handler, new FakeCopySource())
            .AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.Completed, execution.Outcome);
        Assert.Equal("asset_01", Assert.Single(execution.Result!.RequirementMatches).AssetId);
        Assert.Equal("asset_01", Assert.Single(execution.Result.VariantGroups).AssetIds.Single());
        Assert.Equal("asset_01", Assert.Single(execution.Result.SubjectGeometry).AssetId);
        Assert.Equal("asset_01", execution.Result.SizeTableCandidate!.SourceAssetId);
    }

    [Fact]
    public async Task RejectsResultReferencesOutsideProducingRequest()
    {
        var mismatched = new AiAnalysisResultV1(
            AiAnalysisProtocol.ResultSchemaVersion,
            "result",
            "request_01",
            null,
            "model/proposed",
            [new AiRequirementMatchV1("frame_01", "img_9", 1, 0.98m, ["SUBJECT_COMPLETE"])],
            [], [], null, [],
            new AiUsageSummaryV1("usage_request_01", 3, 4, 1, null, null));
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessBody(mismatched), Encoding.UTF8, "application/json")
        });

        var execution = await CreateAdapter(handler, new FakeCopySource())
            .AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_RESPONSE_REFERENCE_MISMATCH", execution.Error!.Code);
    }

    [Fact]
    public async Task RejectsSemanticResultWithDuplicateRequirementRank()
    {
        var request = RequestWithTwoAssets();
        var result = StructuredResult(
            request.RequestId,
            request.ModelConfigVersion,
            [
                new AiRequirementMatchV1("frame_01", "asset_01", 1, 0.98m, ["front"]),
                new AiRequirementMatchV1("frame_01", "asset_02", 1, 0.87m, ["alternate"])
            ]);

        var (execution, diagnostic) = await ExecuteStructuredResultAsync(result, request);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_RESPONSE_INVALID", execution.Error!.Code);
        Assert.Equal("semantic", diagnostic!.FailureStage);
    }

    [Fact]
    public async Task RejectsSemanticResultWithInvalidSubjectGeometry()
    {
        var result = StructuredResult(
            geometry: [new AiSubjectGeometryV1("asset_01", 0.5m, 0.2m, 0.5m, 0.9m, 0.9m)]);

        var (execution, diagnostic) = await ExecuteStructuredResultAsync(result);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_RESPONSE_INVALID", execution.Error!.Code);
        Assert.Equal("semantic", diagnostic!.FailureStage);
    }

    [Fact]
    public async Task RejectsSemanticResultWithRaggedSizeTable()
    {
        var result = StructuredResult(sizeTable: new AiSizeTableCandidateV1(
            "asset_01",
            0.9m,
            ["尺码", "胸围"],
            [["M"]]));

        var (execution, diagnostic) = await ExecuteStructuredResultAsync(result);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_RESPONSE_INVALID", execution.Error!.Code);
        Assert.Equal("semantic", diagnostic!.FailureStage);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, AiProviderOutcome.TerminalError, "AI_PROVIDER_BAD_REQUEST")]
    [InlineData(HttpStatusCode.Unauthorized, AiProviderOutcome.TerminalError, "AI_PROVIDER_AUTH_FAILED")]
    [InlineData((HttpStatusCode)429, AiProviderOutcome.RetryableError, "AI_PROVIDER_RATE_LIMITED")]
    [InlineData(HttpStatusCode.InternalServerError, AiProviderOutcome.RetryableError, "AI_PROVIDER_TEMPORARY_FAILURE")]
    [InlineData(HttpStatusCode.BadGateway, AiProviderOutcome.RetryableError, "AI_PROVIDER_TEMPORARY_FAILURE")]
    [InlineData(HttpStatusCode.ServiceUnavailable, AiProviderOutcome.RetryableError, "AI_PROVIDER_TEMPORARY_FAILURE")]
    [InlineData(HttpStatusCode.GatewayTimeout, AiProviderOutcome.RetryableError, "AI_PROVIDER_TEMPORARY_FAILURE")]
    [InlineData((HttpStatusCode)599, AiProviderOutcome.RetryableError, "AI_PROVIDER_TEMPORARY_FAILURE")]
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
    public async Task TransportNetworkFailureIsUnknownAndNeverMarkedRetryable()
    {
        var handler = new StubHandler(
            (Func<CancellationToken, HttpResponseMessage>)(_ =>
                throw new HttpRequestException("simulated offline transport")));
        var adapter = CreateAdapter(handler, new FakeCopySource());

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        // The request crossed the HTTP boundary, so a dropped connection may
        // have been accepted by the provider.  Keep it unresolved and never
        // let the scheduler treat it as a safe retry.
        Assert.Equal(AiProviderOutcome.AiResultUnknown, execution.Outcome);
        Assert.Equal("AI_PROVIDER_NETWORK_UNKNOWN", execution.Error!.Code);
        Assert.False(execution.Error.Retryable);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task NativeTransportIOExceptionIsUnknownAndNotMisclassifiedAsLocalCopyFailure()
    {
        var handler = new StubHandler(
            (Func<CancellationToken, HttpResponseMessage>)(_ =>
                throw new IOException("simulated native transport break")));
        var adapter = CreateAdapter(handler, new FakeCopySource());

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.AiResultUnknown, execution.Outcome);
        Assert.Equal("AI_PROVIDER_NETWORK_UNKNOWN", execution.Error!.Code);
        Assert.False(execution.Error.Retryable);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ResponseBodyTransportFailureIsUnknownAndNeverRetried()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ThrowingReadContent()
        });
        var adapter = CreateAdapter(handler, new FakeCopySource());

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.AiResultUnknown, execution.Outcome);
        Assert.Equal("AI_PROVIDER_NETWORK_UNKNOWN", execution.Error!.Code);
        Assert.False(execution.Error.Retryable);
        Assert.Equal(1, handler.CallCount);
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
    public async Task CredentialStoreFailureIsStableAndDoesNotSendRequest()
    {
        var handler = new StubHandler((Func<CancellationToken, HttpResponseMessage>)(_ =>
            throw new InvalidOperationException("should not send")));
        var adapter = new VolcArkAiAdapter(
            new HttpClient(handler),
            new ThrowingCredentialSource(),
            new FakeCopySource(),
            new JsonSchemaContractValidator());

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_CREDENTIAL_UNAVAILABLE", execution.Error!.Code);
        Assert.False(execution.Error.Retryable);
        Assert.Equal(0, handler.CallCount);
        Assert.DoesNotContain("credential", execution.Error.UserAction, StringComparison.OrdinalIgnoreCase);
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
        var instructions = payload["input"]![0]!["content"]!.AsArray()
            .Single(item => item!["type"]!.GetValue<string>() == "input_text")!["text"]!
            .GetValue<string>();
        Assert.Contains("\"image_alias\":\"img_0\"", instructions, StringComparison.Ordinal);
        Assert.Contains("\"asset_id\":\"asset_01\"", instructions, StringComparison.Ordinal);
        Assert.Contains("analysis_kind=product_joint_understanding", instructions, StringComparison.Ordinal);
        Assert.Contains("size_table_extraction", instructions, StringComparison.Ordinal);
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

    [Fact]
    public void DefaultsMatchApprovedRequestTransportLimits()
    {
        var options = new VolcArkAiOptions();

        Assert.Equal(16 * 1024 * 1024, options.MaxRequestBytes);
        Assert.Equal(24, options.MaxAssetsPerRequest);
        Assert.Equal(TimeSpan.FromSeconds(180), options.EffectiveRequestTimeout);
    }

    [Fact]
    public async Task RejectsMoreThanApprovedAssetsBeforeHttp()
    {
        var handler = new StubHandler(
            (Func<CancellationToken, HttpResponseMessage>)(_ => throw new InvalidOperationException("should not send")));
        var request = Request() with
        {
            Assets = Enumerable.Range(1, 25)
                .Select(index => new AiAssetInputV1(
                    $"asset_{index:D2}",
                    "sha256:" + new string('b', 64),
                    "image/jpeg",
                    10,
                    10,
                    $"copy_{index:D2}"))
                .ToArray()
        };

        var result = await CreateAdapter(handler, new FakeCopySource()).AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, result.Outcome);
        Assert.Equal("AI_REQUEST_INVALID", result.Error!.Code);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void ApprovedTransportLimitsCannotBeLoosenedByOverrides()
    {
        Assert.Throws<ArgumentException>(() => CreateAdapter(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new FakeCopySource(),
            options: new VolcArkAiOptions(MaxAssetsPerRequest: 25)));
        Assert.Throws<ArgumentException>(() => CreateAdapter(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new FakeCopySource(),
            options: new VolcArkAiOptions(RequestTimeout: TimeSpan.FromSeconds(181))));
        Assert.Throws<ArgumentException>(() => CreateAdapter(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new FakeCopySource(),
            options: new VolcArkAiOptions(MaxRequestBytes: 16 * 1024 * 1024 + 1)));
    }

    [Fact]
    public async Task AcceptsFormalAndCompatibleCandidateCopyProfiles()
    {
        foreach (var profile in new[]
                 {
                     AiAnalysisCopyProfiles.WicBoundedV1,
                     AiAnalysisCopyProfiles.WicBoundedCandidateV1
                 })
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessBody(), Encoding.UTF8, "application/json")
            });
            var request = Request(profile);
            var result = await CreateAdapter(
                handler,
                new FakeCopySource { CompressionProfile = profile }).AnalyzeAsync(request, CancellationToken.None);

            Assert.Equal(AiProviderOutcome.Completed, result.Outcome);
            Assert.Equal(1, handler.CallCount);
        }
    }

    [Theory]
    [InlineData(AiAnalysisCopyProfiles.BytePreservingProvisionalV1)]
    [InlineData(AiAnalysisCopyProfiles.WicBoundedCandidateV1)]
    public async Task RejectsActualCopyProfileSubstitutionBeforeHttp(string actualProfile)
    {
        var handler = new StubHandler(
            (Func<CancellationToken, HttpResponseMessage>)(_ => throw new InvalidOperationException("should not send")));

        var result = await CreateAdapter(
            handler,
            new FakeCopySource { CompressionProfile = actualProfile }).AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, result.Outcome);
        Assert.Equal("AI_ANALYSIS_COPY_UNAVAILABLE", result.Error!.Code);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task RejectsMissingFormalCopyMetadataBeforeHttp()
    {
        var asset = Request().Assets[0];
        var variants = new[]
        {
            asset with { AnalysisCopyDigest = null },
            asset with { AnalysisCopyLength = null },
            asset with { AnalysisCopyMimeType = null },
            asset with { AnalysisCopyCompressionProfile = null }
        };

        foreach (var variant in variants)
        {
            var handler = new StubHandler(
                (Func<CancellationToken, HttpResponseMessage>)(_ => throw new InvalidOperationException("should not send")));
            var result = await CreateAdapter(handler, new FakeCopySource()).AnalyzeAsync(
                Request() with { Assets = [variant] },
                CancellationToken.None);

            Assert.Equal(AiProviderOutcome.TerminalError, result.Outcome);
            Assert.Equal("AI_REQUEST_INVALID", result.Error!.Code);
            Assert.Equal(0, handler.CallCount);
        }
    }

    [Fact]
    public async Task RejectsActualCopyDigestOrLengthMismatchBeforeHttp()
    {
        var variants = new[]
        {
            new FakeCopySource { ContentDigest = "sha256:" + new string('0', 64) },
            new FakeCopySource { ByteLength = 4 }
        };
        foreach (var copies in variants)
        {
            var handler = new StubHandler(
                (Func<CancellationToken, HttpResponseMessage>)(_ => throw new InvalidOperationException("should not send")));
            var result = await CreateAdapter(handler, copies).AnalyzeAsync(Request(), CancellationToken.None);

            Assert.Equal(AiProviderOutcome.TerminalError, result.Outcome);
            Assert.Equal("AI_ANALYSIS_COPY_UNAVAILABLE", result.Error!.Code);
            Assert.Equal(0, handler.CallCount);
        }
    }

    [Fact]
    public async Task RejectsTiffCopyBeforeHttp()
    {
        var handler = new StubHandler(
            (Func<CancellationToken, HttpResponseMessage>)(_ => throw new InvalidOperationException("should not send")));
        var request = Request() with
        {
            Assets = [Request().Assets[0] with { AnalysisCopyMimeType = "image/tiff" }]
        };

        var result = await CreateAdapter(handler, new FakeCopySource { MimeType = "image/tiff" })
            .AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, result.Outcome);
        Assert.Equal("AI_REQUEST_INVALID", result.Error!.Code);
        Assert.Equal(0, handler.CallCount);
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

    private static AiAnalysisRequestV1 Request(
        string compressionProfile = AiAnalysisCopyProfiles.WicBoundedV1) => new(
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
        compressionProfile,
        "consent_01",
        "sha256:" + new string('a', 64),
        [new AiAssetInputV1(
            "asset_01",
            "sha256:" + new string('b', 64),
            "image/jpeg",
            10,
            10,
            "copy_01",
            CopyDigest,
            3,
            "image/jpeg",
            compressionProfile)],
        [new AiRequirementInputV1("frame_01", "主图", "居中", true)]);

    private static AiAnalysisRequestV1 RequestWithTwoAssets()
    {
        var request = Request();
        return request with
        {
            Assets =
            [
                request.Assets[0],
                request.Assets[0] with
                {
                    AssetId = "asset_02",
                    ContentHash = "sha256:" + new string('c', 64),
                    AnalysisCopyReference = "copy_02"
                }
            ]
        };
    }

    private static AiAnalysisResultV1 StructuredResult(
        string requestId = "request_01",
        string modelConfigVersion = "model/proposed",
        IReadOnlyList<AiRequirementMatchV1>? matches = null,
        IReadOnlyList<AiSubjectGeometryV1>? geometry = null,
        AiSizeTableCandidateV1? sizeTable = null) => new(
        AiAnalysisProtocol.ResultSchemaVersion,
        "result",
        requestId,
        null,
        modelConfigVersion,
        matches ?? [],
        [],
        geometry ?? [],
        sizeTable,
        [],
        new AiUsageSummaryV1("usage_" + requestId, 3, 4, 1, null, null));

    private static async Task<(AiAnalysisExecutionResult Execution, AiResponseShapeDiagnostic? Diagnostic)> ExecuteStructuredResultAsync(
        AiAnalysisResultV1 result,
        AiAnalysisRequestV1? request = null)
    {
        AiResponseShapeDiagnostic? diagnostic = null;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessBody(result), Encoding.UTF8, "application/json")
        });
        var adapter = CreateAdapter(
            handler,
            new FakeCopySource(),
            options: new VolcArkAiOptions(ResponseShapeDiagnosticSink: value => diagnostic = value));
        var execution = await adapter.AnalyzeAsync(request ?? Request(), CancellationToken.None);
        return (execution, diagnostic);
    }

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
        return SuccessBody(result);
    }

    private static string SuccessBody(AiAnalysisResultV1 result)
    {
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
        public string CompressionProfile { get; set; } = AiAnalysisCopyProfiles.WicBoundedV1;
        public string ContentDigest { get; set; } = CopyDigest;
        public long ByteLength { get; set; } = 3;

        public Task<AiAnalysisCopy> OpenAsync(string analysisCopyReference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AiAnalysisCopy(
                analysisCopyReference,
                MimeType,
                10,
                10,
                new byte[] { 1, 2, 3 },
                ContentDigest,
                ByteLength,
                CompressionProfile,
                IncludeLease ? "lease-1" : null));
        }

        public Task ReleaseAsync(string analysisCopyReference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Released.Add(analysisCopyReference);
            return Task.CompletedTask;
        }
    }

    private sealed record ProbePreparedAsset(
        AssetFileMetadata Source,
        AiAnalysisCopyMetadata Copy);

    private sealed class ProbeCountingHandler : DelegatingHandler
    {
        private int _callCount;

        public ProbeCountingHandler() : base(new HttpClientHandler())
        {
        }

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static readonly string CopyDigest = "sha256:" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant();

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

    private sealed class ThrowingCredentialSource : IAiCredentialSource
    {
        public ValueTask<string?> GetCredentialAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("simulated credential store failure");
        }
    }

    private sealed class ThrowingReadContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new IOException("simulated response stream failure");

        protected override bool TryComputeLength(out long lengthValue)
        {
            lengthValue = -1;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromException<Stream>(new IOException("simulated response stream failure"));
    }
}
