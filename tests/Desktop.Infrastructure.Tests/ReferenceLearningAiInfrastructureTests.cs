using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Ai;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Inputs;
using Desktop.Infrastructure.Templates;

namespace Desktop.Infrastructure.Tests;

public sealed class ReferenceLearningAiInfrastructureTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [ReferenceLearningRealProbeFact]
    public async Task RealReferenceGroupProducesStrictDynamicSemanticResult()
    {
        var inputRoot = Environment.GetEnvironmentVariable("TPL_LEARN_PROBE_INPUT_ROOT");
        var runtimeRoot = Environment.GetEnvironmentVariable("TPL_LEARN_PROBE_RUNTIME_ROOT");
        var sessionRoot = Environment.GetEnvironmentVariable("TPL_LEARN_PROBE_SESSION_ROOT");
        var groupId = Environment.GetEnvironmentVariable("TPL_LEARN_PROBE_GROUP_ID");
        Assert.True(Directory.Exists(inputRoot));
        Assert.False(string.IsNullOrWhiteSpace(runtimeRoot));
        Assert.True(Directory.Exists(sessionRoot));
        Assert.False(string.IsNullOrWhiteSpace(groupId));
        Directory.CreateDirectory(runtimeRoot!);
        var policy = new LocalAppPathPolicy(runtimeRoot!);
        using var preparer = new FileAiAnalysisCopyPreparer(policy, new AtomicFileStore(policy), new AiAnalysisCopyEncodingOptions());
        var metadataReader = new StreamingAssetMetadataReader();
        var prepared = new List<(SourceAssetMetadata Source, AiAnalysisCopyMetadata Copy)>();
        try
        {
            var sessionDocuments = Directory.EnumerateFiles(sessionRoot!, "*.json", SearchOption.TopDirectoryOnly)
                .Select(file => JsonDocument.Parse(File.ReadAllBytes(file)))
                .Where(document => document.RootElement.TryGetProperty("internal_psd_relative_path", out var path)
                    && path.GetString()?.Contains($"templates/groups/{groupId}/", StringComparison.OrdinalIgnoreCase) == true)
                .OrderBy(document => document.RootElement.GetProperty("page_id").GetString(), StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(6, sessionDocuments.Length);

            var referencePhotos = sessionDocuments[0].RootElement.GetProperty("reference_photos")
                .EnumerateArray()
                .ToDictionary(photo => photo.GetProperty("asset_id").GetString()!, StringComparer.Ordinal);
            var frames = sessionDocuments
                .SelectMany(document => document.RootElement.GetProperty("candidates").EnumerateArray()
                    .Where(candidate => candidate.GetProperty("is_product").GetBoolean())
                    .Select(candidate => (
                        FrameId: candidate.GetProperty("frame_id").GetString()!,
                        PageId: candidate.GetProperty("page_id").GetString()!,
                        AssetId: candidate.GetProperty("reference_asset_id").GetString()!)))
                .ToArray();
            Assert.Equal(11, frames.Length);
            var usedAssetIds = frames.Select(frame => frame.AssetId).Distinct(StringComparer.Ordinal).ToArray();
            Assert.InRange(usedAssetIds.Length, 2, AiAnalysisProductionPolicy.MaxAssetsPerRequest);

            foreach (var assetId in usedAssetIds)
            {
                var photo = referencePhotos[assetId];
                var relative = photo.GetProperty("relative_path").GetString()!;
                var read = await metadataReader.ReadAsync(inputRoot!, relative, CancellationToken.None);
                Assert.True(read.IsSuccess, read.Failure?.Code);
                var value = read.Metadata!;
                var source = SourceAssetMetadata.CreateReadable(
                    assetId, "reference", relative, photo.GetProperty("file_name").GetString()!, value.Length,
                    value.Sha256, value.PixelWidth, value.PixelHeight, value.Format, value.MimeType);
                var copy = await preparer.PrepareAsync(new AiAnalysisCopyPreparationRequest(
                    $"rlprobe_{assetId}", inputRoot!, source, AiAnalysisCopyProfiles.WicBoundedV2), CancellationToken.None);
                prepared.Add((source, copy));
            }

            var request = new ReferenceLearningAiRequestV2(
                ReferenceLearningAiProtocol.RequestSchemaVersion, "request",
                ReferenceLearningAiOperation.GenerateSelectionRequirements,
                "rl_real_group_probe_20260912", "learn_real_group_probe_20260912", ReferenceLearningAiService.ModelConfigVersion,
                ReferenceLearningAiProtocol.SemanticPromptVersion, ReferenceLearningAiProtocol.ResultSchemaVersion,
                AiAnalysisCopyProfiles.WicBoundedV2, "approved_probe_20260912",
                sessionDocuments[0].RootElement.GetProperty("reference_folder_digest").GetString()!,
                prepared.Select(item => new AiAssetInputV1(
                    item.Source.AssetId, "sha256:" + item.Source.Sha256, item.Source.MimeType,
                    item.Source.PixelWidth, item.Source.PixelHeight, item.Copy.Reference, item.Copy.Digest,
                    item.Copy.ByteLength, item.Copy.MimeType, item.Copy.CompressionProfileVersion, item.Copy.LeaseId)).ToArray(),
                frames.Select(frame => new ReferenceLearningFramePromptV2(
                    frame.FrameId, frame.PageId, frame.AssetId,
                    "参考 PSD 页面", "本地已通过内容哈希闭合该框与参考照片；只依据可见照片生成未来可复用类别。",
                    "沿用参考 PSD 的布局意图，不把单张照片背景写入规则")).ToArray());
            var adapter = new VolcArkAiAdapter(
                new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
                new WindowsCredentialSource(),
                new FileAiAnalysisCopySource(policy),
                new JsonSchemaContractValidator());

            var execution = await adapter.AnalyzeReferenceLearningAsync(request, CancellationToken.None);

            Assert.True(execution.IsSuccess, execution.Error?.Code + ": " + execution.Error?.UserAction);
            Assert.Equal(frames.Length, execution.Result!.FrameResults.Count);
            foreach (var frame in execution.Result.FrameResults)
            {
                Assert.True(ReferenceSelectionRequirementPolicy.IsValid(frame.SelectionRequirement));
                Assert.NotEmpty(frame.PositiveEvidence);
                Console.WriteLine($"TPL_LEARN_REAL_AI_FRAME frame={frame.FrameId} requirement={frame.SelectionRequirement} confidence={frame.Confidence}");
            }
            Assert.Equal(ReferenceLearningAiProtocol.ResultSchemaVersion, execution.Result.SchemaVersion);
            Console.WriteLine($"TPL_LEARN_REAL_AI outcome={execution.Outcome} frames={frames.Length} assets={prepared.Count} input_tokens={execution.Usage?.InputTokens} output_tokens={execution.Usage?.OutputTokens}");
        }
        finally
        {
            foreach (var item in prepared)
                await preparer.ReleaseAsync(item.Copy.Reference, item.Copy.LeaseId, CancellationToken.None);
            try { Directory.Delete(runtimeRoot!, true); } catch { }
        }
    }

    [Fact]
    public async Task FormalAdapterSendsDynamicSemanticContractAndValidatesResult()
    {
        string? requestBody = null;
        var request = Request(ReferenceLearningAiOperation.GenerateSelectionRequirements);
        request = request with
        {
            Assets =
            [
                request.Assets[0],
                request.Assets[0] with
                {
                    AssetId = "asset_02",
                    AnalysisCopyReference = "copy_02",
                    AnalysisCopyLeaseId = "lease_02"
                }
            ],
            Frames =
            [
                request.Frames[0],
                request.Frames[0] with
                {
                    FrameId = "frame_02",
                    ReferenceAssetId = "asset_02"
                }
            ]
        };
        var result = Result(request, "人物正面端坐图");
        var handler = new Handler(async message =>
        {
            requestBody = await message.Content!.ReadAsStringAsync();
            return Response(result);
        });
        var adapter = new VolcArkAiAdapter(
            new HttpClient(handler),
            new Credential("secret"),
            new CopySource(),
            new JsonSchemaContractValidator());

        var execution = await adapter.AnalyzeReferenceLearningAsync(request, CancellationToken.None);

        Assert.True(execution.IsSuccess);
        Assert.Equal("人物正面端坐图", execution.Result!.FrameResults[0].SelectionRequirement);
        Assert.Contains("reference-learning-ai-result/v2", requestBody, StringComparison.Ordinal);
        using var providerRequest = JsonDocument.Parse(requestBody!);
        var content = providerRequest.RootElement.GetProperty("input")[0].GetProperty("content");
        Assert.Equal("REFERENCE_ASSET asset_id=asset_01 frame_ids=f_01", content[0].GetProperty("text").GetString());
        Assert.Equal("input_image", content[1].GetProperty("type").GetString());
        Assert.Equal("REFERENCE_ASSET asset_id=asset_02 frame_ids=f_02", content[2].GetProperty("text").GetString());
        Assert.Equal("input_image", content[3].GetProperty("type").GetString());
        var prompt = content[4].GetProperty("text").GetString();
        Assert.Contains("可复用的图片类别短语", prompt, StringComparison.Ordinal);
        Assert.Contains("示例不是固定枚举", prompt, StringComparison.Ordinal);
        Assert.Contains("禁止复述单张参考照片", prompt, StringComparison.Ordinal);
        Assert.Contains("2至40 个字符", prompt, StringComparison.Ordinal);
        Assert.Contains("frame_context 和 layout_intent 仅是本地结构审计元数据", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("img_0", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FormalAdapterRejectsReferenceAssetThatNoFrameUsesBeforeHttp()
    {
        var request = Request(ReferenceLearningAiOperation.GenerateSelectionRequirements);
        request = request with
        {
            Assets =
            [
                request.Assets[0],
                request.Assets[0] with
                {
                    AssetId = "asset_unused",
                    AnalysisCopyReference = "copy_unused",
                    AnalysisCopyLeaseId = "lease_unused"
                }
            ]
        };
        var handler = new Handler(_ => throw new Xunit.Sdk.XunitException("HTTP must not be called"));
        var adapter = new VolcArkAiAdapter(
            new HttpClient(handler), new Credential("secret"), new CopySource(), new JsonSchemaContractValidator());

        var execution = await adapter.AnalyzeReferenceLearningAsync(request, CancellationToken.None);

        Assert.False(execution.IsSuccess);
        Assert.Equal("TPL_LEARN_AI_REQUEST_INVALID", execution.Error!.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ReferenceLearningKeepsIndependentTwentyFourAssetLimit()
    {
        var seed = Request(ReferenceLearningAiOperation.GenerateSelectionRequirements);
        var assets = Enumerable.Range(1, AiAnalysisProductionPolicy.MaxReferenceAssetsPerRequest + 1)
            .Select(index => new AiAssetInputV1(
                $"asset_{index:D2}",
                "sha256:" + new string('c', 64),
                "image/jpeg",
                100,
                100,
                $"copy_{index:D2}",
                Digest([1, 2, 3, 4]),
                4,
                "image/jpeg",
                AiAnalysisCopyProfiles.WicBoundedV4,
                $"lease_{index:D2}"))
            .ToArray();
        var frames = assets.Select((asset, index) => new ReferenceLearningFramePromptV2(
                $"frame_{index + 1:D2}",
                "page_01",
                asset.AssetId,
                "模特页",
                "按可见内容判断",
                "主体完整"))
            .ToArray();
        var request = seed with
        {
            CompressionProfileVersion = AiAnalysisCopyProfiles.WicBoundedV4,
            Assets = assets,
            Frames = frames
        };
        var handler = new Handler(_ => throw new Xunit.Sdk.XunitException("HTTP must not be called"));
        var adapter = new VolcArkAiAdapter(
            new HttpClient(handler),
            new Credential("secret"),
            new CopySource(),
            new JsonSchemaContractValidator());

        var execution = await adapter.AnalyzeReferenceLearningAsync(request, CancellationToken.None);

        Assert.False(execution.IsSuccess);
        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("TPL_LEARN_AI_REQUEST_INVALID", execution.Error!.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task FormalAdapterRejectsCopyLeaseIdentityMismatchBeforeHttp()
    {
        var request = Request(ReferenceLearningAiOperation.GenerateSelectionRequirements);
        var handler = new Handler(_ => throw new Xunit.Sdk.XunitException("HTTP must not be called"));
        var adapter = new VolcArkAiAdapter(
            new HttpClient(handler),
            new Credential("secret"),
            new CopySource("lease_other"),
            new JsonSchemaContractValidator());

        var execution = await adapter.AnalyzeReferenceLearningAsync(request, CancellationToken.None);

        Assert.False(execution.IsSuccess);
        Assert.Equal("AI_ANALYSIS_COPY_UNAVAILABLE", execution.Error!.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task FormalAdapterMapsShortFrameAliasBackToClosedRequestId()
    {
        var request = Request(ReferenceLearningAiOperation.GenerateSelectionRequirements);
        var aliased = Result(request, "袖口工艺特写") with
        {
            FrameResults = [Result(request, "袖口工艺特写").FrameResults[0] with { FrameId = "f_01" }]
        };
        var handler = new Handler(_ => Task.FromResult(Response(aliased)));
        var adapter = new VolcArkAiAdapter(
            new HttpClient(handler),
            new Credential("secret"),
            new CopySource(),
            new JsonSchemaContractValidator());

        var execution = await adapter.AnalyzeReferenceLearningAsync(request, CancellationToken.None);

        Assert.True(execution.IsSuccess, execution.Error?.Code);
        Assert.Equal("frame_01", Assert.Single(execution.Result!.FrameResults).FrameId);
    }

    [Fact]
    public async Task CancelAfterReferenceUploadRetainsUnknownOutcome()
    {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async message =>
        { await message.Content!.ReadAsByteArrayAsync(); entered.SetResult(); return await release.Task; });
        var request = Request(ReferenceLearningAiOperation.GenerateSelectionRequirements);
        using var http = new HttpClient(handler);
        var adapter = new VolcArkAiAdapter(http, new Credential("test-secret"), new CopySource(), new JsonSchemaContractValidator());
        var pending = adapter.AnalyzeReferenceLearningAsync(request, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        try
        {
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(AiProviderOutcome.AiResultUnknown, result.Outcome);
            Assert.False(result.Error!.Retryable);
        }
        finally { release.TrySetResult(Response(Result(request, "人物正面站立图"))); }
    }

    [Fact]
    public async Task HardDeadlineTreatsUnwrittenReferenceRequestAsRetryableSubmissionFailure()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(_ => release.Task);
        var request = Request(ReferenceLearningAiOperation.GenerateSelectionRequirements);
        var adapter = new VolcArkAiAdapter(
            new HttpClient(handler),
            new Credential("secret"),
            new CopySource(),
            new JsonSchemaContractValidator(),
            new VolcArkAiOptions(RequestTimeout: TimeSpan.FromMilliseconds(40)));

        var stopwatch = Stopwatch.StartNew();
        var execution = await adapter.AnalyzeReferenceLearningAsync(request, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(AiProviderOutcome.RetryableError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_SUBMISSION_TIMEOUT", execution.Error!.Code);
        Assert.True(execution.Error.Retryable);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Hard deadline took {stopwatch.Elapsed}.");

        release.TrySetResult(Response(Result(request, "人物正面站立图")));
        await Task.Delay(25);
    }

    [Fact]
    public async Task HardDeadlineReturnsWhenReferenceResponseBodyIgnoresCancellation()
    {
        var content = new DeferredStreamContent();
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        var request = Request(ReferenceLearningAiOperation.GenerateSelectionRequirements);
        var adapter = new VolcArkAiAdapter(
            new HttpClient(handler),
            new Credential("secret"),
            new CopySource(),
            new JsonSchemaContractValidator(),
            new VolcArkAiOptions(RequestTimeout: TimeSpan.FromMilliseconds(40)));

        var stopwatch = Stopwatch.StartNew();
        var execution = await adapter.AnalyzeReferenceLearningAsync(request, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(AiProviderOutcome.AiResultUnknown, execution.Outcome);
        Assert.Equal("AI_PROVIDER_RESPONSE_BODY_TIMEOUT", execution.Error!.Code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Hard deadline took {stopwatch.Elapsed}.");

        content.Release(new MemoryStream(Encoding.UTF8.GetBytes("{}")));
        await Task.Delay(25);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("in_progress")]
    [InlineData("processing")]
    public async Task PendingReferenceStatusesRemainUnknownAndPreserveUsage(string status)
    {
        var request = Request(ReferenceLearningAiOperation.GenerateSelectionRequirements);
        var handler = new Handler(_ => Task.FromResult(ProviderResponse(
            status,
            JsonSerializer.Serialize(Result(request, "人物正面端坐图"), JsonOptions),
            inputTokens: 101,
            outputTokens: 7)));
        var adapter = new VolcArkAiAdapter(
            new HttpClient(handler),
            new Credential("secret"),
            new CopySource(),
            new JsonSchemaContractValidator());

        var execution = await adapter.AnalyzeReferenceLearningAsync(request, CancellationToken.None);

        Assert.Equal(AiProviderOutcome.AiResultUnknown, execution.Outcome);
        Assert.Equal("AI_PROVIDER_RESULT_PENDING", execution.Error!.Code);
        Assert.False(execution.Error.Retryable);
        Assert.Equal(101, execution.Usage!.InputTokens);
        Assert.Equal(7, execution.Usage.OutputTokens);
        Assert.Equal(108, execution.Usage.TotalTokens);
    }

    [Theory]
    [InlineData("incomplete", "AI_PROVIDER_OUTPUT_INCOMPLETE")]
    [InlineData("failed", "AI_PROVIDER_REQUEST_FAILED")]
    [InlineData("cancelled", "AI_PROVIDER_REQUEST_CANCELLED")]
    [InlineData("canceled", "AI_PROVIDER_REQUEST_CANCELLED")]
    public async Task ReferenceTerminalStatusesFailClosedAndPreserveUsage(string status, string errorCode)
    {
        var request = Request(ReferenceLearningAiOperation.GenerateSelectionRequirements);
        var handler = new Handler(_ => Task.FromResult(ProviderResponse(
            status,
            JsonSerializer.Serialize(Result(request, "人物正面站立图"), JsonOptions),
            inputTokens: 103,
            outputTokens: 9)));
        var adapter = new VolcArkAiAdapter(
            new HttpClient(handler),
            new Credential("secret"),
            new CopySource(),
            new JsonSchemaContractValidator());

        var execution = await adapter.AnalyzeReferenceLearningAsync(request, CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal(errorCode, execution.Error!.Code);
        Assert.False(execution.Error.Retryable);
        Assert.Equal(103, execution.Usage!.InputTokens);
        Assert.Equal(9, execution.Usage.OutputTokens);
        Assert.Equal(112, execution.Usage.TotalTokens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("future_provider_state")]
    public async Task MissingOrUnknownReferenceStatusFailsClosedAndPreservesUsage(string? status)
    {
        var request = Request(ReferenceLearningAiOperation.GenerateSelectionRequirements);
        var handler = new Handler(_ => Task.FromResult(ProviderResponse(
            status,
            JsonSerializer.Serialize(Result(request, "人物正面近景图"), JsonOptions),
            inputTokens: 107,
            outputTokens: 11)));
        var adapter = new VolcArkAiAdapter(
            new HttpClient(handler),
            new Credential("secret"),
            new CopySource(),
            new JsonSchemaContractValidator());

        var execution = await adapter.AnalyzeReferenceLearningAsync(request, CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_STATUS_INVALID", execution.Error!.Code);
        Assert.False(execution.Error.Retryable);
        Assert.Equal(107, execution.Usage!.InputTokens);
        Assert.Equal(11, execution.Usage.OutputTokens);
        Assert.Equal(118, execution.Usage.TotalTokens);
    }

    [Fact]
    public async Task CompletedReferenceStatusWithTruncatedOutputRemainsInvalid()
    {
        var request = Request(ReferenceLearningAiOperation.GenerateSelectionRequirements);
        var handler = new Handler(_ => Task.FromResult(ProviderResponse(
            "completed",
            "{\"schema_version\":\"reference-learning-ai-result/v2\"",
            inputTokens: 109,
            outputTokens: 13)));
        var adapter = new VolcArkAiAdapter(
            new HttpClient(handler),
            new Credential("secret"),
            new CopySource(),
            new JsonSchemaContractValidator());

        var execution = await adapter.AnalyzeReferenceLearningAsync(request, CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_RESPONSE_INVALID", execution.Error!.Code);
        Assert.Equal(109, execution.Usage!.InputTokens);
        Assert.Equal(13, execution.Usage.OutputTokens);
    }

    [Fact]
    public async Task MissingCredentialFailsBeforeHttpAndDoesNotInventSuccess()
    {
        var handler = new Handler(_ => throw new Xunit.Sdk.XunitException("HTTP must not be called"));
        var adapter = new VolcArkAiAdapter(
            new HttpClient(handler), new Credential(null), new CopySource(), new JsonSchemaContractValidator());

        var execution = await adapter.AnalyzeReferenceLearningAsync(
            Request(ReferenceLearningAiOperation.GenerateSelectionRequirements), CancellationToken.None);

        Assert.False(execution.IsSuccess);
        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_CREDENTIAL_MISSING", execution.Error!.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task OldPromptContractIsRejectedBeforeCredentialOrHttp()
    {
        var handler = new Handler(_ => throw new Xunit.Sdk.XunitException("HTTP must not be called"));
        var adapter = new VolcArkAiAdapter(
            new HttpClient(handler), new Credential("secret"), new CopySource(), new JsonSchemaContractValidator());
        var request = Request(ReferenceLearningAiOperation.GenerateSelectionRequirements) with
        {
            PromptContractVersion = "reference-learning-semantic-prompt/v1"
        };

        var execution = await adapter.AnalyzeReferenceLearningAsync(request, CancellationToken.None);

        Assert.False(execution.IsSuccess);
        Assert.Equal("TPL_LEARN_AI_REQUEST_INVALID", execution.Error!.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RetiredTargetMatchOperationIsRejectedBeforeCredentialOrHttp()
    {
        var handler = new Handler(_ => throw new Xunit.Sdk.XunitException("HTTP must not be called"));
        var adapter = new VolcArkAiAdapter(
            new HttpClient(handler), new Credential("secret"), new CopySource(), new JsonSchemaContractValidator());

        var execution = await adapter.AnalyzeReferenceLearningAsync(
            Request("match_target_group"),
            CancellationToken.None);

        Assert.False(execution.IsSuccess);
        Assert.Equal("TPL_LEARN_AI_REQUEST_INVALID", execution.Error!.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CacheAndUserOverridePersistAcrossStoreRecreationAndInvalidateOneWay()
    {
        using var root = new TempDirectory();
        var policy = new LocalAppPathPolicy(root.Path);
        var files = new AtomicFileStore(policy);
        var first = new JsonReferenceLearningAiCacheStore(files);
        var request = Request(ReferenceLearningAiOperation.GenerateSelectionRequirements);
        var entry = new ReferenceLearningAiCacheEntry(
            "sha256:" + new string('a', 64), request.Operation, request.SchemaVersion,
            request.ResponseSchemaVersion, request.ModelConfigVersion, request.PromptContractVersion,
            request.CompressionProfileVersion, request.ReferenceSetDigest,
            Result(request, "人物正面站立图"), new AiProviderUsage(10, 2, 12, false), DateTimeOffset.UtcNow);
        await first.SaveAsync(entry, CancellationToken.None);
        await first.SaveOverrideAsync(new ReferenceSemanticOverride("learn_01", "frame_01", "坐姿近景", DateTimeOffset.UtcNow), CancellationToken.None);

        var second = new JsonReferenceLearningAiCacheStore(new AtomicFileStore(new LocalAppPathPolicy(root.Path)));
        Assert.NotNull(await second.FindAsync(entry.CacheKey, CancellationToken.None));
        Assert.Equal("坐姿近景", (await second.FindOverrideAsync("learn_01", "frame_01", CancellationToken.None))!.SelectionRequirement);
        Assert.True(await second.InvalidateAsync(entry.CacheKey, CancellationToken.None));
        Assert.False((await second.FindAsync(entry.CacheKey, CancellationToken.None))!.IsReusable);
        Assert.False(await second.InvalidateAsync(entry.CacheKey, CancellationToken.None));

        var legacyNarrative = new ReferenceSemanticOverride(
            "learn_01",
            "frame_legacy",
            "人物坐在木质长凳上，背景为米色墙面",
            DateTimeOffset.UtcNow);
        await files.WriteAtomicallyAsync(
            "learning/reference-semantic-overrides/learn_01/frame_legacy.json",
            JsonSerializer.SerializeToUtf8Bytes(legacyNarrative, JsonOptions),
            CancellationToken.None);
        Assert.Null(await second.FindOverrideAsync("learn_01", "frame_legacy", CancellationToken.None));
    }

    private static ReferenceLearningAiRequestV2 Request(string operation) => new(
        ReferenceLearningAiProtocol.RequestSchemaVersion,
        "request",
        operation,
        "request_01",
        "learn_01",
        ReferenceLearningAiService.ModelConfigVersion,
        ReferenceLearningAiProtocol.SemanticPromptVersion,
        ReferenceLearningAiProtocol.ResultSchemaVersion,
        AiAnalysisCopyProfiles.WicBoundedV1,
        "consent_01",
        "sha256:" + new string('a', 64),
        [new AiAssetInputV1(
            "asset_01", "sha256:" + new string('c', 64), "image/jpeg", 100, 100,
            "copy_01", Digest([1, 2, 3, 4]), 4, "image/jpeg",
            AiAnalysisCopyProfiles.WicBoundedV1, "lease_01")],
        [new ReferenceLearningFramePromptV2(
            "frame_01", "page_01",
            "asset_01",
            "模特页", "按可见内容判断", "主体完整")]);

    private static ReferenceLearningAiResultV2 Result(ReferenceLearningAiRequestV2 request, string requirement) => new(
        ReferenceLearningAiProtocol.ResultSchemaVersion,
        "result",
        request.Operation,
        request.RequestId,
        request.ModelConfigVersion,
        request.Frames.Select(frame => new ReferenceLearningFrameResultV2(
            frame.FrameId, requirement, ["参考组中姿态唯一"], ["排除站姿"], 0.96m)).ToArray(),
        [],
        new AiUsageSummaryV1("usage_01", 10, 2, 1, null, null));

    private static HttpResponseMessage Response(ReferenceLearningAiResultV2 result)
    {
        return ProviderResponse(
            "completed",
            JsonSerializer.Serialize(result, JsonOptions),
            inputTokens: 10,
            outputTokens: 2);
    }

    private static HttpResponseMessage ProviderResponse(
        string? status,
        string outputText,
        int inputTokens,
        int outputTokens,
        params string[]? outputSegments)
    {
        var root = new JsonObject
        {
            ["id"] = "response_01",
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = inputTokens,
                ["output_tokens"] = outputTokens,
                ["total_tokens"] = inputTokens + outputTokens
            }
        };
        if (status is not null)
        {
            root["status"] = status;
        }

        if (outputSegments is { Length: > 0 })
        {
            var content = new JsonArray();
            foreach (var segment in outputSegments)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "output_text",
                    ["text"] = segment
                });
            }

            root["output"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "message",
                    ["content"] = content
                }
            };
        }
        else
        {
            root["output_text"] = outputText;
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(root.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
        };
    }

    private static string Digest(byte[] bytes) =>
        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class Credential(string? value) : IAiCredentialSource
    {
        public ValueTask<string?> GetCredentialAsync(CancellationToken cancellationToken) => ValueTask.FromResult(value);
    }

    private sealed class CopySource(string? forcedLeaseId = null) : IAiAnalysisCopySource
    {
        public Task<AiAnalysisCopy> OpenAsync(string analysisCopyReference, CancellationToken cancellationToken) => Task.FromResult(
            new AiAnalysisCopy(analysisCopyReference, "image/jpeg", 100, 100, new byte[] { 1, 2, 3, 4 },
                Digest([1, 2, 3, 4]),
                4, AiAnalysisCopyProfiles.WicBoundedV1,
                forcedLeaseId ?? analysisCopyReference.Replace("copy_", "lease_", StringComparison.Ordinal)));
        public Task ReleaseAsync(string analysisCopyReference, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return responder(request);
        }
    }

    private sealed class DeferredStreamContent : HttpContent
    {
        private readonly TaskCompletionSource<Stream> _stream =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release(Stream stream) => _stream.TrySetResult(stream);

        protected override Task<Stream> CreateContentReadStreamAsync() => _stream.Task;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tpl-learn-ai-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }

    private sealed class ReferenceLearningRealProbeFactAttribute : FactAttribute
    {
        public ReferenceLearningRealProbeFactAttribute() => Skip =
            string.Equals(Environment.GetEnvironmentVariable("TPL_LEARN_RUN_REAL_PROBE"), "1", StringComparison.Ordinal)
                ? null
                : "Set TPL_LEARN_RUN_REAL_PROBE=1 only for an explicitly approved reference-group request.";
    }
}
