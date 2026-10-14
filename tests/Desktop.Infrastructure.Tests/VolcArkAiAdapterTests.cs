using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Ai;
using Desktop.Application.Diagnostics;
using Desktop.Application.Inputs;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Diagnostics;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Inputs;

namespace Desktop.Infrastructure.Tests;

public sealed class VolcArkAiAdapterTests
{
    [OfflineRegressionFact]
    public async Task Real24ImageCompressionCandidatesReportBoundedTransportSizesWithoutChangingSources()
    {
        var inputRoot = Environment.GetEnvironmentVariable("AI01_OFFLINE_24_INPUT_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(inputRoot));
        Assert.True(Directory.Exists(inputRoot));
        var sourcePaths = Directory.EnumerateFiles(inputRoot!, "*", SearchOption.TopDirectoryOnly)
            .Where(InputImageRules.IsSupportedExtension)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(24, sourcePaths.Length);
        var metadataReader = new StreamingAssetMetadataReader();
        var sources = new List<(string RelativePath, AssetFileMetadata Metadata)>(sourcePaths.Length);
        foreach (var sourcePath in sourcePaths)
        {
            var relativePath = Path.GetFileName(sourcePath);
            var read = await metadataReader.ReadAsync(inputRoot!, relativePath, CancellationToken.None);
            Assert.True(read.IsSuccess, read.Failure?.Code ?? "AI01_OFFLINE_SOURCE_METADATA_INVALID");
            sources.Add((relativePath, read.Metadata!));
        }

        var candidates = new[]
        {
            new AiAnalysisCopyEncodingOptions(
                MaxLongestEdge: 1600,
                JpegQuality: 82,
                CompressionProfileVersion: AiAnalysisCopyProfiles.WicBoundedV2),
            new AiAnalysisCopyEncodingOptions(
                MaxLongestEdge: 1440,
                JpegQuality: 78,
                CompressionProfileVersion: AiAnalysisCopyProfiles.WicTransportCandidate1440Q78V3),
            new AiAnalysisCopyEncodingOptions(
                MaxLongestEdge: 1280,
                JpegQuality: 76,
                CompressionProfileVersion: AiAnalysisCopyProfiles.WicTransportCandidate1280Q76V3)
        };

        foreach (var candidate in candidates)
        {
            var runtimeRoot = Path.Combine(
                Path.GetTempPath(),
                "ai01-compression-candidate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runtimeRoot);
            var prepared = new List<AiAnalysisCopyMetadata>(sources.Count);
            FileAiAnalysisCopyPreparer? preparer = null;
            try
            {
                var policy = new LocalAppPathPolicy(runtimeRoot);
                preparer = new FileAiAnalysisCopyPreparer(
                    policy,
                    new AtomicFileStore(policy),
                    candidate);
                foreach (var ((relativePath, source), index) in sources.Select((item, index) => (item, index)))
                {
                    var sourceAsset = SourceAssetMetadata.CreateReadable(
                        $"asset_{index + 1:D2}",
                        "style_01",
                        relativePath,
                        relativePath,
                        source.Length,
                        source.Sha256,
                        source.PixelWidth,
                        source.PixelHeight,
                        source.Format,
                        source.MimeType);
                    prepared.Add(await preparer.PrepareAsync(
                        new AiAnalysisCopyPreparationRequest(
                            $"candidate_copy_{index + 1:D2}",
                            inputRoot!,
                            sourceAsset,
                            candidate.CompressionProfileVersion),
                        CancellationToken.None));
                }

                var copyBytes = prepared.Sum(copy => copy.ByteLength);
                var base64Bytes = prepared.Sum(copy => 4L * ((copy.ByteLength + 2L) / 3L));
                Console.WriteLine(
                    $"AI01_COMPRESSION_CANDIDATE profile={candidate.CompressionProfileVersion} "
                    + $"max_edge={candidate.MaxLongestEdge} jpeg_quality={candidate.JpegQuality} "
                    + $"copy_bytes={copyBytes} base64_bytes={base64Bytes} "
                    + $"mime_jpeg={prepared.Count(copy => copy.MimeType == "image/jpeg")} "
                    + $"mime_png={prepared.Count(copy => copy.MimeType == "image/png")}");
            }
            finally
            {
                foreach (var copy in prepared)
                {
                    try
                    {
                        await preparer!.ReleaseAsync(copy.Reference, copy.LeaseId, CancellationToken.None);
                    }
                    catch
                    {
                    }
                }

                preparer?.Dispose();
                if (Directory.Exists(runtimeRoot))
                {
                    Directory.Delete(runtimeRoot, recursive: true);
                }
            }
        }

        foreach (var (relativePath, source) in sources)
        {
            var path = Path.Combine(inputRoot!, relativePath);
            var info = new FileInfo(path);
            Assert.Equal(source.Length, info.Length);
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            Assert.Equal(
                source.Sha256,
                Convert.ToHexString(await SHA256.HashDataAsync(stream, CancellationToken.None))
                    .ToLowerInvariant());
        }
    }

    [OfflineRegressionFact]
    public async Task Real24ImageBatchStaysWithinFinalJsonRequestBudgetWithoutProviderNetwork()
    {
        var inputRoot = Environment.GetEnvironmentVariable("AI01_OFFLINE_24_INPUT_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(inputRoot));
        Assert.True(Directory.Exists(inputRoot));

        var sourcePaths = Directory.EnumerateFiles(inputRoot!, "*", SearchOption.TopDirectoryOnly)
            .Where(InputImageRules.IsSupportedExtension)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(24, sourcePaths.Length);

        var runtimeRoot = Path.Combine(Path.GetTempPath(), "ai01-offline-24-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeRoot);
        var prepared = new List<AiAnalysisCopyMetadata>(sourcePaths.Length);
        var sourceMetadata = new List<AssetFileMetadata>(sourcePaths.Length);
        FileAiAnalysisCopyPreparer? preparer = null;
        try
        {
            var encodingOptions = new AiAnalysisCopyEncodingOptions();
            var policy = new LocalAppPathPolicy(runtimeRoot);
            preparer = new FileAiAnalysisCopyPreparer(policy, new AtomicFileStore(policy), encodingOptions);
            var copySource = new FileAiAnalysisCopySource(policy);
            var metadataReader = new StreamingAssetMetadataReader();
            for (var index = 0; index < sourcePaths.Length; index++)
            {
                var relativePath = Path.GetFileName(sourcePaths[index]);
                var read = await metadataReader.ReadAsync(inputRoot!, relativePath, CancellationToken.None);
                Assert.True(read.IsSuccess, read.Failure?.Code ?? "AI01_OFFLINE_SOURCE_METADATA_INVALID");
                var source = read.Metadata!;
                sourceMetadata.Add(source);
                var sourceAsset = SourceAssetMetadata.CreateReadable(
                    $"asset_{index + 1:D2}",
                    "style_01",
                    relativePath,
                    relativePath,
                    source.Length,
                    source.Sha256,
                    source.PixelWidth,
                    source.PixelHeight,
                    source.Format,
                    source.MimeType);
                prepared.Add(await preparer.PrepareAsync(
                    new AiAnalysisCopyPreparationRequest(
                        $"offline24_copy_{index + 1:D2}",
                        inputRoot!,
                        sourceAsset,
                        AiAnalysisCopyProfiles.WicBoundedV3),
                    CancellationToken.None));
            }

            var request = Request(AiAnalysisCopyProfiles.WicBoundedV3) with
            {
                RequestId = "offline_24",
                CorrelationId = "offline_24",
                ProductId = "product_offline_24",
                BatchId = "batch_offline_24",
                StyleId = "style_01",
                ModelConfigVersion = AiAnalysisProductionPolicy.ModelConfigVersionV2,
                PromptContractVersion = AiAnalysisProductionPolicy.PromptContractVersionV3,
                Assets = prepared.Select((copy, index) => new AiAssetInputV1(
                    $"asset_{index + 1:D2}",
                    "sha256:" + sourceMetadata[index].Sha256,
                    sourceMetadata[index].MimeType,
                    sourceMetadata[index].PixelWidth,
                    sourceMetadata[index].PixelHeight,
                    copy.Reference,
                    copy.Digest,
                    copy.ByteLength,
                    copy.MimeType,
                    copy.CompressionProfileVersion,
                    copy.LeaseId)).ToArray(),
                Requirements = CreateProbeRequest("daily-alias-11").Requirements
            };
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    SuccessBody(StructuredResult(request.RequestId, request.ModelConfigVersion)),
                    Encoding.UTF8,
                    "application/json")
            });
            var execution = await CreateAdapter(handler, copySource, options: new VolcArkAiOptions(
                ReleaseCopiesAfterRequest: false)).AnalyzeAsync(request, CancellationToken.None);

            Assert.Equal(AiProviderOutcome.Completed, execution.Outcome);
            Assert.Equal(24, prepared.Count);
            Assert.All(prepared, copy =>
            {
                Assert.Equal(AiAnalysisCopyProfiles.WicBoundedV3, copy.CompressionProfileVersion);
                Assert.InRange(copy.ByteLength, 1, 8 * 1024 * 1024);
                Assert.True(copy.MimeType is "image/jpeg" or "image/png");
            });
            Assert.NotNull(handler.RequestBodyBytes);
            Assert.Equal(Encoding.UTF8.GetByteCount(handler.RequestBody!), handler.RequestBodyBytes!.Length);
            Assert.InRange(handler.RequestBodyBytes.Length, 1, 8 * 1024 * 1024);
            Assert.Equal(1, handler.CallCount);
            for (var index = 0; index < sourcePaths.Length; index++)
            {
                var info = new FileInfo(sourcePaths[index]);
                Assert.Equal(sourceMetadata[index].Length, info.Length);
                await using var stream = new FileStream(
                    sourcePaths[index],
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var digest = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, CancellationToken.None)).ToLowerInvariant();
                Assert.Equal(sourceMetadata[index].Sha256, digest);
            }
            Console.WriteLine(
                $"AI01_OFFLINE_24_METRICS assets={prepared.Count} source_bytes={sourceMetadata.Sum(asset => asset.Length)} "
                + $"copy_bytes={prepared.Sum(copy => copy.ByteLength)} final_json_bytes={handler.RequestBodyBytes.Length} "
                + $"http_requests={handler.CallCount} mime_jpeg={prepared.Count(copy => copy.MimeType == "image/jpeg")} "
                + $"mime_png={prepared.Count(copy => copy.MimeType == "image/png")}");
        }
        finally
        {
            if (Directory.Exists(runtimeRoot))
            {
                foreach (var copy in prepared)
                {
                    try
                    {
                        await preparer!.ReleaseAsync(copy.Reference, copy.LeaseId, CancellationToken.None);
                    }
                    catch { }
                }
                preparer?.Dispose();
                Directory.Delete(runtimeRoot, recursive: true);
            }
        }
    }

    private sealed class OfflineRegressionFactAttribute : FactAttribute
    {
        public OfflineRegressionFactAttribute() => Skip =
            string.Equals(Environment.GetEnvironmentVariable("AI01_RUN_OFFLINE_24_REGRESSION"), "1", StringComparison.Ordinal)
            && Directory.Exists(Environment.GetEnvironmentVariable("AI01_OFFLINE_24_INPUT_ROOT") ?? string.Empty)
                ? null
                : "Opt-in offline 24-image regression using a local fake HTTP handler";
    }

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
        Assert.InRange(expectedAssetCount, 1, AiAnalysisProductionPolicy.MaxAssetsPerRequest);
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
            var transportLogger = new RedactingFileLogger(
                policy,
                Path.Combine("logs", "ai-transport.jsonl"));
            using var handler = new ProbeCountingHandler();
            using var httpClient = CreateRealProbeHttpClient(handler);
            var adapter = new VolcArkAiAdapter(
                httpClient,
                new WindowsCredentialSource(),
                copySource,
                new JsonSchemaContractValidator(),
                new VolcArkAiOptions(
                    ReleaseCopiesAfterRequest: false,
                    ResponseShapeDiagnosticSink: diagnostics.Add),
                transportLogger);
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
            Console.WriteLine("REAL_PROBE_TRANSPORT_LOG relative_path=logs/ai-transport.jsonl");
            if (result.Outcome != AiProviderOutcome.Completed || result.Result is null)
            {
                var shapeSummary = JsonSerializer.Serialize(diagnostics);
                Assert.Fail(
                    $"REAL_PROBE_FAILURE outcome={result.Outcome} error_code={result.Error?.Code ?? "none"} "
                    + $"duration_ms={result.Duration.TotalMilliseconds:F0} diagnostics={shapeSummary}");
            }

            Console.WriteLine("REAL_PROBE_RESULT_SUMMARY " + JsonSerializer.Serialize(new
            {
                profile = probeProfile,
                request.AnalysisKind,
                requirement_match_count = result.Result.RequirementMatches.Count,
                variant_group_count = result.Result.VariantGroups.Count,
                subject_geometry_count = result.Result.SubjectGeometry.Count,
                has_size_table_candidate = result.Result.SizeTableCandidate is not null,
                warning_count = result.Result.Warnings.Count
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
            "daily-alias-11" => (
                AiAnalysisKind.ProductJointUnderstanding,
                new AiRequirementInputV1[]
                {
                    new("learn_frame_probe_00_0123456789abcdef", "领口细节图", "detail_crop", true),
                    new("learn_frame_probe_01_0123456789abcdef", "成分信息图", "detail_crop", true),
                    new("learn_frame_probe_02_0123456789abcdef", "袖口工艺特写图", "detail_crop", true),
                    new("learn_frame_probe_03_0123456789abcdef", "人物正面站立图", "grid_top_left", true),
                    new("learn_frame_probe_04_0123456789abcdef", "人物侧身站立图", "grid_bottom_right", true),
                    new("learn_frame_probe_05_0123456789abcdef", "人物正面端坐图", "grid_top_right", true),
                    new("learn_frame_probe_06_0123456789abcdef", "面料纹理特写图", "hero_centered", true),
                    new("learn_frame_probe_07_0123456789abcdef", "人物侧身端坐图", "grid_top_right", true),
                    new("learn_frame_probe_08_0123456789abcdef", "人物背面全身图", "grid_bottom_right", true),
                    new("learn_frame_probe_09_0123456789abcdef", "人物正面站立图", "hero_centered", true),
                    new("learn_frame_probe_10_0123456789abcdef", "人物背面全身图", "hero_centered", true)
                }),
            _ => (AiAnalysisKind.ProductJointUnderstanding, Request().Requirements.ToArray())
        };
        var normalized = profile.Replace('-', '_');
        var runSuffix = Guid.NewGuid().ToString("N")[..12];
        var digestJson = JsonSerializer.Serialize(requirements.Select(requirement => new
        {
            requirement.RequirementId,
            requirement.SelectionRequirement,
            requirement.LayoutIntent,
            requirement.Required
        }).ToArray());
        return Request() with
        {
            RequestId = $"probe_{normalized}_{runSuffix}",
            CorrelationId = $"probe_{normalized}_{runSuffix}",
            ProductId = $"product_{normalized}_{runSuffix}",
            BatchId = $"batch_{normalized}_{runSuffix}",
            StyleId = $"style_{normalized}_{runSuffix}",
            AnalysisKind = analysisKind,
            ModelConfigVersion = AiAnalysisProductionPolicy.ModelConfigVersionV2,
            PromptContractVersion = AiAnalysisProductionPolicy.PromptContractVersionV3,
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
    public async Task NormalizesAllTransportAliasesBeforeReturningResult()
    {
        var aliased = new AiAnalysisResultV1(
            AiAnalysisProtocol.ResultSchemaVersion,
            "result",
            AiAnalysisResultReferenceNormalizer.RequestAlias,
            null,
            AiAnalysisResultReferenceNormalizer.ModelConfigVersionAlias,
            [new AiRequirementMatchV1("req_0", "img_0", 1, 0.98m, ["SUBJECT_COMPLETE"])],
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
        Assert.Equal("request_01", execution.Result!.RequestId);
        Assert.Equal("model/proposed", execution.Result.ModelConfigVersion);
        Assert.Equal("frame_01", Assert.Single(execution.Result.RequirementMatches).RequirementId);
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

    [Theory]
    [InlineData("request")]
    [InlineData("model")]
    [InlineData("requirement")]
    public async Task RejectsUnknownTransportAliases(string field)
    {
        var result = new AiAnalysisResultV1(
            AiAnalysisProtocol.ResultSchemaVersion,
            "result",
            field == "request" ? "request_9" : AiAnalysisResultReferenceNormalizer.RequestAlias,
            null,
            field == "model" ? "model_9" : AiAnalysisResultReferenceNormalizer.ModelConfigVersionAlias,
            field == "requirement"
                ? [new AiRequirementMatchV1("req_9", "img_0", 1, 0.98m, ["exact"])]
                : [],
            [], [], null, [],
            new AiUsageSummaryV1("usage_request_01", 3, 4, 1, null, null));
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessBody(result), Encoding.UTF8, "application/json")
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

        // The request body was written to the handler's transport stream, but
        // that alone does not prove provider acceptance. Keep it unresolved
        // and never let the scheduler treat it as a safe retry.
        Assert.Equal(AiProviderOutcome.AiResultUnknown, execution.Outcome);
        Assert.Equal("AI_PROVIDER_RESPONSE_HEADERS_NETWORK_FAILURE", execution.Error!.Code);
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
        Assert.Equal("AI_PROVIDER_RESPONSE_HEADERS_NETWORK_FAILURE", execution.Error!.Code);
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
        Assert.Equal("AI_PROVIDER_RESPONSE_BODY_NETWORK_FAILURE", execution.Error!.Code);
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
        Assert.Equal("AI_PROVIDER_RESPONSE_HEADERS_TIMEOUT", timeout.Error!.Code);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellation = await adapter.AnalyzeAsync(Request(), cancelled.Token);
        Assert.Equal(AiProviderOutcome.Cancelled, cancellation.Outcome);
    }

    [Fact]
    public async Task HardDeadlineReturnsWhenSendHandlerIgnoresCancellation()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = Request();
        var handler = new StubHandler(async cancellationToken =>
        {
            observedToken.TrySetResult(cancellationToken);
            return await release.Task.ConfigureAwait(false);
        });
        var adapter = CreateAdapter(
            handler,
            new FakeCopySource(),
            options: new VolcArkAiOptions(RequestTimeout: TimeSpan.FromMilliseconds(40)));

        var stopwatch = Stopwatch.StartNew();
        var execution = await adapter.AnalyzeAsync(request, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(AiProviderOutcome.AiResultUnknown, execution.Outcome);
        Assert.Equal("AI_PROVIDER_RESPONSE_HEADERS_TIMEOUT", execution.Error!.Code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Hard deadline took {stopwatch.Elapsed}.");
        Assert.True((await observedToken.Task.WaitAsync(TimeSpan.FromSeconds(1))).IsCancellationRequested);

        release.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessBody(StructuredResult(request.RequestId, request.ModelConfigVersion)), Encoding.UTF8, "application/json")
        });
        await Task.Delay(25);
    }

    [Fact]
    public async Task HardDeadlineReturnsWhenResponseBodyIgnoresCancellation()
    {
        var content = new DeferredStreamContent();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        var adapter = CreateAdapter(
            handler,
            new FakeCopySource(),
            options: new VolcArkAiOptions(RequestTimeout: TimeSpan.FromMilliseconds(40)));

        var stopwatch = Stopwatch.StartNew();
        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(AiProviderOutcome.AiResultUnknown, execution.Outcome);
        Assert.Equal("AI_PROVIDER_RESPONSE_BODY_TIMEOUT", execution.Error!.Code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Hard deadline took {stopwatch.Elapsed}.");

        content.Release(new MemoryStream(Encoding.UTF8.GetBytes("{}")));
        await Task.Delay(25);
    }

    [Fact]
    public async Task TransportDiagnosticsRecordBoundedPhasesAndSpecificFailureWithoutPayloadValues()
    {
        var logger = new RecordingLogger();
        var content = new DeferredStreamContent();
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.TryAddWithoutValidation("x-request-id", "req-safe-123");
            return response;
        });
        var adapter = CreateAdapter(
            handler,
            new FakeCopySource(),
            options: new VolcArkAiOptions(RequestTimeout: TimeSpan.FromMilliseconds(40)),
            transportLogger: logger);

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal("AI_PROVIDER_RESPONSE_BODY_TIMEOUT", execution.Error!.Code);
        Assert.Contains(logger.InformationEvents, item => Phase(item) == "payload_ready");
        Assert.Contains(logger.InformationEvents, item => Phase(item) == "send_started");
        Assert.Contains(logger.InformationEvents, item => Phase(item) == "upload_progress");
        Assert.Contains(logger.InformationEvents, item => Phase(item) == "upload_completed");
        Assert.Contains(logger.InformationEvents, item => Phase(item) == "response_headers_received");
        Assert.Contains(logger.InformationEvents, item => Phase(item) == "response_body_started");
        var failure = Assert.Single(logger.ErrorEvents);
        Assert.Equal("AI_TRANSPORT_FAILURE", failure.EventCode);
        Assert.Equal("AI_PROVIDER_RESPONSE_BODY_TIMEOUT", failure.ErrorCode);
        Assert.Equal("reading_response_body", Phase(failure));
        Assert.Equal(true, failure.Properties["upload_completed"]);
        Assert.Equal(failure.Properties["payload_bytes"], failure.Properties["upload_bytes_written"]);
        Assert.Equal(true, failure.Properties["response_headers_received"]);
        Assert.Equal("req-safe-123", failure.Properties["provider_request_reference"]);
        Assert.DoesNotContain(
            logger.AllSerializedValues(),
            value => value.Contains("test-key", StringComparison.Ordinal)
                || value.Contains("data:image", StringComparison.Ordinal));

        content.Release(new MemoryStream(Encoding.UTF8.GetBytes("{}")));
        await Task.Delay(25);
    }

    [Fact]
    public async Task PartialUploadTimeoutIsRetryableAndRecordsLastCompletedChunk()
    {
        const int firstCompletedChunk = 64 * 1024;
        var request = RequestWithAssets(
            1,
            bytesPerCopy: 2_000_000,
            AiAnalysisCopyProfiles.WicBoundedV2);
        var logger = new RecordingLogger();
        var transport = new BlockingUploadStream();
        var handler = new SerializingUploadHandler(transport);
        var adapter = CreateAdapter(
            handler,
            new SizedCopySource(2_000_000, request.Assets),
            options: new VolcArkAiOptions(RequestTimeout: TimeSpan.FromMilliseconds(80)),
            transportLogger: logger);

        var execution = await adapter.AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(AiProviderOutcome.RetryableError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_SUBMISSION_TIMEOUT", execution.Error!.Code);
        Assert.True(execution.Error.Retryable);
        Assert.True(await transport.SecondWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        var failure = Assert.Single(logger.ErrorEvents, item =>
            string.Equals(item.ErrorCode, "AI_PROVIDER_SUBMISSION_TIMEOUT", StringComparison.Ordinal));
        Assert.Equal("submitting_request", Phase(failure));
        Assert.Equal(false, failure.Properties["upload_completed"]);
        Assert.Equal(firstCompletedChunk, failure.Properties["upload_bytes_written"]);
        Assert.Contains(logger.InformationEvents, item =>
            Phase(item) == "upload_progress"
            && Equals(firstCompletedChunk, item.Properties["upload_bytes_written"]));
        Assert.DoesNotContain(logger.InformationEvents, item => Phase(item) == "upload_completed");

        transport.Release();
        await handler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(25);
    }

    [Fact]
    public async Task UploadAbsoluteDeadlineIsIndependentFromTotalRequestDeadline()
    {
        var request = RequestWithAssets(
            1,
            bytesPerCopy: 2_000_000,
            AiAnalysisCopyProfiles.WicBoundedV4);
        var transport = new BlockingUploadStream();
        var handler = new SerializingUploadHandler(transport);
        var adapter = CreateAdapter(
            handler,
            new SizedCopySource(2_000_000, request.Assets),
            options: new VolcArkAiOptions(
                RequestTimeout: TimeSpan.FromSeconds(2),
                UploadTimeout: TimeSpan.FromMilliseconds(40),
                UploadIdleTimeout: TimeSpan.FromSeconds(1),
                ResponseHeadersTimeout: TimeSpan.FromSeconds(1),
                ResponseBodyTimeout: TimeSpan.FromSeconds(1)));

        var stopwatch = Stopwatch.StartNew();
        var execution = await adapter.AnalyzeAsync(request, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(AiProviderOutcome.RetryableError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_SUBMISSION_TIMEOUT", execution.Error!.Code);
        Assert.True(execution.Error.Retryable);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Upload deadline took {stopwatch.Elapsed}.");

        transport.Release();
        await handler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ProviderRequestUsesFallbackCapableHttp11WithoutExpectContinue()
    {
        var request = Request(AiAnalysisCopyProfiles.WicBoundedV4);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                SuccessBody(StructuredResult(request.RequestId, request.ModelConfigVersion)),
                Encoding.UTF8,
                "application/json")
        });

        var execution = await CreateAdapter(
            handler,
            new FakeCopySource { CompressionProfile = AiAnalysisCopyProfiles.WicBoundedV4 })
            .AnalyzeAsync(
            request,
            CancellationToken.None);

        Assert.Equal(AiProviderOutcome.Completed, execution.Outcome);
        Assert.Equal(HttpVersion.Version11, handler.RequestVersion);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, handler.RequestVersionPolicy);
        Assert.False(handler.ExpectContinue);
    }

    [Fact]
    public async Task UploadNoProgressDeadlineIsIndependentFromAbsoluteUploadDeadline()
    {
        var request = RequestWithAssets(
            1,
            bytesPerCopy: 2_000_000,
            AiAnalysisCopyProfiles.WicBoundedV4);
        var transport = new BlockingUploadStream();
        var handler = new SerializingUploadHandler(transport);
        var adapter = CreateAdapter(
            handler,
            new SizedCopySource(2_000_000, request.Assets),
            options: new VolcArkAiOptions(
                RequestTimeout: TimeSpan.FromSeconds(2),
                UploadTimeout: TimeSpan.FromSeconds(1),
                UploadIdleTimeout: TimeSpan.FromMilliseconds(40),
                ResponseHeadersTimeout: TimeSpan.FromSeconds(1),
                ResponseBodyTimeout: TimeSpan.FromSeconds(1)));

        var stopwatch = Stopwatch.StartNew();
        var execution = await adapter.AnalyzeAsync(request, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(AiProviderOutcome.RetryableError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_SUBMISSION_TIMEOUT", execution.Error!.Code);
        Assert.True(execution.Error.Retryable);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Upload idle deadline took {stopwatch.Elapsed}.");

        transport.Release();
        await handler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ResponseHeadersDeadlineStartsAfterUploadCompletes()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = Request();
        var handler = new StubHandler(async cancellationToken =>
        {
            return await release.Task.ConfigureAwait(false);
        });
        var adapter = CreateAdapter(
            handler,
            new FakeCopySource(),
            options: new VolcArkAiOptions(
                RequestTimeout: TimeSpan.FromSeconds(3),
                UploadTimeout: TimeSpan.FromSeconds(1),
                UploadIdleTimeout: TimeSpan.FromSeconds(1),
                ResponseHeadersTimeout: TimeSpan.FromMilliseconds(40),
                ResponseBodyTimeout: TimeSpan.FromSeconds(1)));

        var stopwatch = Stopwatch.StartNew();
        var execution = await adapter.AnalyzeAsync(request, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(AiProviderOutcome.AiResultUnknown, execution.Outcome);
        Assert.Equal("AI_PROVIDER_RESPONSE_HEADERS_TIMEOUT", execution.Error!.Code);
        Assert.False(execution.Error.Retryable);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Response-header deadline took {stopwatch.Elapsed}.");

        release.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                SuccessBody(StructuredResult(request.RequestId, request.ModelConfigVersion)),
                Encoding.UTF8,
                "application/json")
        });
        await handler.ResponderCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ResponseBodyDeadlineStartsAfterHeadersAndObservesLateRead()
    {
        var content = new DeferredStreamContent();
        var request = Request();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        var adapter = CreateAdapter(
            handler,
            new FakeCopySource(),
            options: new VolcArkAiOptions(
                RequestTimeout: TimeSpan.FromSeconds(3),
                UploadTimeout: TimeSpan.FromSeconds(1),
                UploadIdleTimeout: TimeSpan.FromSeconds(1),
                ResponseHeadersTimeout: TimeSpan.FromSeconds(1),
                ResponseBodyTimeout: TimeSpan.FromMilliseconds(40)));

        var stopwatch = Stopwatch.StartNew();
        var execution = await adapter.AnalyzeAsync(request, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(AiProviderOutcome.AiResultUnknown, execution.Outcome);
        Assert.Equal("AI_PROVIDER_RESPONSE_BODY_TIMEOUT", execution.Error!.Code);
        Assert.False(execution.Error.Retryable);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Response-body deadline took {stopwatch.Elapsed}.");

        content.Release(new MemoryStream(Encoding.UTF8.GetBytes(
            SuccessBody(StructuredResult(request.RequestId, request.ModelConfigVersion))
        )));
        await content.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static string Phase(RecordedLog item) => Assert.IsType<string>(item.Properties["phase"]);

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
    public async Task IncompleteStatusIsTerminalBeforeTruncatedTextParsingAndPreservesUsage()
    {
        AiResponseShapeDiagnostic? diagnostic = null;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                ProviderBody("incomplete", ["{\"schema_version\":\"ai-analysis-result/v1\""], 33_000, 2_048),
                Encoding.UTF8,
                "application/json")
        });
        var adapter = CreateAdapter(
            handler,
            new FakeCopySource(),
            options: new VolcArkAiOptions(ResponseShapeDiagnosticSink: value => diagnostic = value));

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_OUTPUT_INCOMPLETE", execution.Error!.Code);
        Assert.False(execution.Error.Retryable);
        Assert.Equal(33_000, execution.Usage!.InputTokens);
        Assert.Equal(2_048, execution.Usage.OutputTokens);
        Assert.Equal("incomplete", diagnostic!.StatusClass);
        Assert.Equal("status_incomplete", diagnostic.FailureStage);
    }

    [Theory]
    [InlineData("failed", "AI_PROVIDER_REQUEST_FAILED", "status_failed")]
    [InlineData("cancelled", "AI_PROVIDER_REQUEST_CANCELLED", "status_cancelled")]
    [InlineData("canceled", "AI_PROVIDER_REQUEST_CANCELLED", "status_cancelled")]
    public async Task ExplicitProviderTerminalStatusesCannotBeParsedAsSuccess(
        string status,
        string errorCode,
        string failureStage)
    {
        AiResponseShapeDiagnostic? diagnostic = null;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                ProviderBody(status, [JsonSerializer.Serialize(StructuredResult())]),
                Encoding.UTF8,
                "application/json")
        });
        var adapter = CreateAdapter(
            handler,
            new FakeCopySource(),
            options: new VolcArkAiOptions(ResponseShapeDiagnosticSink: value => diagnostic = value));

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal(errorCode, execution.Error!.Code);
        Assert.Equal(3, execution.Usage!.InputTokens);
        Assert.Equal("terminal", diagnostic!.StatusClass);
        Assert.Equal(failureStage, diagnostic.FailureStage);
    }

    [Theory]
    [InlineData(null, "missing")]
    [InlineData("future_provider_state", "other")]
    public async Task MissingOrUnknownProviderStatusFailsClosed(string? status, string statusClass)
    {
        AiResponseShapeDiagnostic? diagnostic = null;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                ProviderBody(status, [JsonSerializer.Serialize(StructuredResult())]),
                Encoding.UTF8,
                "application/json")
        });
        var adapter = CreateAdapter(
            handler,
            new FakeCopySource(),
            options: new VolcArkAiOptions(ResponseShapeDiagnosticSink: value => diagnostic = value));

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_STATUS_INVALID", execution.Error!.Code);
        Assert.Equal(7, execution.Usage!.TotalTokens);
        Assert.Equal(statusClass, diagnostic!.StatusClass);
        Assert.Equal("status_invalid", diagnostic.FailureStage);
    }

    [Fact]
    public async Task CompletedStatusWithTruncatedOutputRemainsInvalidAndPreservesUsage()
    {
        AiResponseShapeDiagnostic? diagnostic = null;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                ProviderBody("completed", ["{\"schema_version\":\"ai-analysis-result/v1\""], 30_000, 4_096),
                Encoding.UTF8,
                "application/json")
        });
        var adapter = CreateAdapter(
            handler,
            new FakeCopySource(),
            options: new VolcArkAiOptions(ResponseShapeDiagnosticSink: value => diagnostic = value));

        var execution = await adapter.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_RESPONSE_INVALID", execution.Error!.Code);
        Assert.Equal(4_096, execution.Usage!.OutputTokens);
        Assert.Equal("output_json_parse", diagnostic!.FailureStage);
    }

    [Fact]
    public async Task CompletedResponseConcatenatesAllOutputTextSegmentsInOrder()
    {
        var serialized = JsonSerializer.Serialize(StructuredResult());
        var split = serialized.Length / 2;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                ProviderBody("completed", [serialized[..split], serialized[split..]]),
                Encoding.UTF8,
                "application/json")
        });

        var execution = await CreateAdapter(handler, new FakeCopySource())
            .AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(AiProviderOutcome.Completed, execution.Outcome);
        Assert.Equal("request_01", execution.Result!.RequestId);
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
        Assert.Equal(
            AiAnalysisProductionPolicy.MaxOutputTokensPerRequestV2,
            payload["max_output_tokens"]!.GetValue<int>());
        Assert.Contains("requirement_id", match["required"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.DoesNotContain("matched", match["properties"]!.AsObject().Select(property => property.Key));
        var content = payload["input"]![0]!["content"]!.AsArray();
        var instructions = content
            .Last(item => item!["type"]!.GetValue<string>() == "input_text")!["text"]!
            .GetValue<string>();
        Assert.Equal(
            JsonSerializer.Serialize(new { image_alias = "img_0" }),
            content[0]!["text"]!.GetValue<string>());
        Assert.Contains("\"requirement_id\":\"req_0\"", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("asset_01", handler.RequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("frame_01", handler.RequestBody!, StringComparison.Ordinal);
        Assert.Contains("analysis_kind=product_joint_understanding", instructions, StringComparison.Ordinal);
        Assert.Contains("size_table_extraction", instructions, StringComparison.Ordinal);
        Assert.Contains("exact", instructions, StringComparison.Ordinal);
        Assert.Contains("safe_similar", instructions, StringComparison.Ordinal);
        Assert.Contains("material_difference", instructions, StringComparison.Ordinal);
        Assert.Contains("unsafe", instructions, StringComparison.Ordinal);
        Assert.Contains("不要根据文件名推断语义", instructions, StringComparison.Ordinal);
        Assert.Contains("真人模特正面和假模特正面都符合候选", instructions, StringComparison.Ordinal);
        Assert.Contains("低置信度表示建议检查，不表示没有可用图", instructions, StringComparison.Ordinal);
        Assert.Contains("除非用户明确要求“必须真人模特/不要假模特”", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwentyFourImagesAndElevenRequirementsExposeOnlyAdjacentBoundedAliases()
    {
        var request = RequestWithAssetsAndRequirements(24, 11, 8_000);
        var aliased = StructuredResult(
            AiAnalysisResultReferenceNormalizer.RequestAlias,
            AiAnalysisResultReferenceNormalizer.ModelConfigVersionAlias,
            Enumerable.Range(0, 11)
                .Select(index => new AiRequirementMatchV1(
                    $"req_{index}",
                    $"img_{index}",
                    1,
                    0.9m,
                    ["exact"]))
                .ToArray());
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessBody(aliased), Encoding.UTF8, "application/json")
        });

        var execution = await CreateAdapter(handler, new SizedCopySource(8_000, request.Assets))
            .AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(AiProviderOutcome.Completed, execution.Outcome);
        Assert.Equal(request.RequestId, execution.Result!.RequestId);
        Assert.Equal(request.ModelConfigVersion, execution.Result.ModelConfigVersion);
        Assert.Equal(11, execution.Result.RequirementMatches.Count);
        Assert.Equal(
            request.Requirements.Select(requirement => requirement.RequirementId),
            execution.Result.RequirementMatches.Select(match => match.RequirementId));
        Assert.Equal(
            request.Assets.Take(11).Select(asset => asset.AssetId),
            execution.Result.RequirementMatches.Select(match => match.AssetId));

        var payload = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        var content = payload["input"]![0]!["content"]!.AsArray();
        Assert.Equal(49, content.Count);
        for (var index = 0; index < 24; index++)
        {
            Assert.Equal("input_text", content[index * 2]!["type"]!.GetValue<string>());
            Assert.Equal(
                JsonSerializer.Serialize(new { image_alias = $"img_{index}" }),
                content[index * 2]!["text"]!.GetValue<string>());
            Assert.Equal("input_image", content[index * 2 + 1]!["type"]!.GetValue<string>());
        }

        var instructionText = content[^1]!["text"]!.GetValue<string>();
        Assert.Contains("request_id=request_0", instructionText, StringComparison.Ordinal);
        Assert.Contains("model_config_version=model_0", instructionText, StringComparison.Ordinal);
        foreach (var requirement in request.Requirements)
        {
            Assert.DoesNotContain(requirement.RequirementId, handler.RequestBody!, StringComparison.Ordinal);
        }
        foreach (var asset in request.Assets)
        {
            Assert.DoesNotContain(asset.AssetId, handler.RequestBody!, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(request.RequestId, handler.RequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain(request.ModelConfigVersion, handler.RequestBody!, StringComparison.Ordinal);

        var schema = payload["text"]!["format"]!["schema"]!["properties"]!.AsObject();
        Assert.Equal("request_0", schema["request_id"]!["const"]!.GetValue<string>());
        Assert.Equal("model_0", schema["model_config_version"]!["const"]!.GetValue<string>());
        Assert.Equal(
            Enumerable.Range(0, 11).Select(index => $"req_{index}"),
            schema["requirement_matches"]!["items"]!["properties"]!["requirement_id"]!["enum"]!
                .AsArray().Select(value => value!.GetValue<string>()));
        Assert.Equal(
            Enumerable.Range(0, 24).Select(index => $"img_{index}"),
            schema["subject_geometry"]!["items"]!["properties"]!["asset_id"]!["enum"]!
                .AsArray().Select(value => value!.GetValue<string>()));
    }

    [Fact]
    public async Task Approved24ImageRequestUsesFinalUtf8JsonByteCountAndReachesProvider()
    {
        var request = RequestWithAssets(24, 8_000, AiAnalysisCopyProfiles.WicBoundedV2);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                SuccessBody(StructuredResult(request.RequestId, request.ModelConfigVersion)),
                Encoding.UTF8,
                "application/json")
        });
        var copies = new SizedCopySource(8_000, request.Assets);
        var execution = await CreateAdapter(handler, copies).AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(AiProviderOutcome.Completed, execution.Outcome);
        Assert.Equal(1, handler.CallCount);
        Assert.NotNull(handler.RequestBodyBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(handler.RequestBody!), handler.RequestBodyBytes!.Length);
        Assert.InRange(handler.RequestBodyBytes.Length, 1, 16 * 1024 * 1024);
        Assert.Equal(24, request.Assets.Count);
    }

    [Fact]
    public async Task OversizedFinalSerializedJsonIsRejectedBeforeHttpWithExplicitAction()
    {
        var request = RequestWithAssets(24, 700_000, AiAnalysisCopyProfiles.WicBoundedV2);
        var handler = new StubHandler(
            (Func<CancellationToken, HttpResponseMessage>)(_ =>
                throw new InvalidOperationException("provider must not be called")));
        var execution = await CreateAdapter(
            handler,
            new SizedCopySource(700_000, request.Assets)).AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(AiProviderOutcome.TerminalError, execution.Outcome);
        Assert.Equal("AI_PROVIDER_REQUEST_TOO_LARGE", execution.Error!.Code);
        Assert.False(execution.Error.Retryable);
        Assert.Equal(0, handler.CallCount);
        Assert.Contains("16 MiB", execution.Error.UserAction, StringComparison.Ordinal);
        Assert.Contains("未发送 AI 请求", execution.Error.UserAction, StringComparison.Ordinal);
        Assert.Contains("重新建立批次", execution.Error.UserAction, StringComparison.Ordinal);
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
        Assert.Equal(AiAnalysisProductionPolicy.MaxTargetAssetsPerRequest, options.MaxAssetsPerRequest);
        Assert.Equal(AiAnalysisProductionPolicy.MaxReferenceAssetsPerRequest, options.MaxReferenceAssetsPerRequest);
        Assert.Equal(TimeSpan.FromSeconds(300), options.EffectiveRequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(240), options.EffectiveUploadTimeout);
        Assert.Equal(TimeSpan.FromSeconds(90), options.EffectiveUploadIdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), options.EffectiveResponseHeadersTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), options.EffectiveResponseBodyTimeout);
    }

    [Fact]
    public async Task RejectsMoreThanApprovedAssetsBeforeHttp()
    {
        var handler = new StubHandler(
            (Func<CancellationToken, HttpResponseMessage>)(_ => throw new InvalidOperationException("should not send")));
        var request = Request() with
        {
            Assets = Enumerable.Range(1, AiAnalysisProductionPolicy.MaxTargetAssetsPerRequest + 1)
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
    public async Task AcceptsFortyTargetAssetsAndSendsOneBoundedRequest()
    {
        var request = RequestWithAssets(
            AiAnalysisProductionPolicy.MaxTargetAssetsPerRequest,
            bytesPerCopy: 3,
            AiAnalysisCopyProfiles.WicBoundedV4) with
        {
            ModelConfigVersion = AiAnalysisProductionPolicy.ModelConfigVersionV2,
            PromptContractVersion = AiAnalysisProductionPolicy.PromptContractVersionV3
        };
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                SuccessBody(StructuredResult(request.RequestId, request.ModelConfigVersion)),
                Encoding.UTF8,
                "application/json")
        });

        var result = await CreateAdapter(
            handler,
            new SizedCopySource(3, request.Assets),
            options: new VolcArkAiOptions(ReleaseCopiesAfterRequest: false))
            .AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(AiProviderOutcome.Completed, result.Outcome);
        Assert.Equal(1, handler.CallCount);
        Assert.NotNull(handler.RequestBodyBytes);
        Assert.InRange(handler.RequestBodyBytes!.Length, 1, 16 * 1024 * 1024);
        using var payload = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(
            AiAnalysisProductionPolicy.MaxTargetAssetsPerRequest,
            payload.RootElement.GetProperty("input")[0]
                .GetProperty("content")
                .EnumerateArray()
                .Count(item => item.TryGetProperty("type", out var type)
                    && type.GetString() == "input_image"));
    }

    [Fact]
    public void ApprovedTransportLimitsCannotBeLoosenedByOverrides()
    {
        Assert.Throws<ArgumentException>(() => CreateAdapter(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new FakeCopySource(),
            options: new VolcArkAiOptions(MaxAssetsPerRequest: AiAnalysisProductionPolicy.MaxTargetAssetsPerRequest + 1)));
        Assert.Throws<ArgumentException>(() => CreateAdapter(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new FakeCopySource(),
            options: new VolcArkAiOptions(RequestTimeout: TimeSpan.FromSeconds(301))));
        Assert.Throws<ArgumentException>(() => CreateAdapter(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new FakeCopySource(),
            options: new VolcArkAiOptions(UploadTimeout: TimeSpan.FromSeconds(241))));
        Assert.Throws<ArgumentException>(() => CreateAdapter(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new FakeCopySource(),
            options: new VolcArkAiOptions(UploadIdleTimeout: TimeSpan.FromSeconds(91))));
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
                     AiAnalysisCopyProfiles.WicBoundedCandidateV1,
                     AiAnalysisCopyProfiles.WicBoundedV2,
                     AiAnalysisCopyProfiles.WicBoundedCandidateV2
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
        IAiAnalysisCopySource copies,
        string? credential = "test-key",
        VolcArkAiOptions? options = null,
        IRedactedLogger? transportLogger = null) => new(
        new HttpClient(handler),
        new FakeCredentialSource(credential),
        copies,
        new JsonSchemaContractValidator(),
        options,
        transportLogger);

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

    private static AiAnalysisRequestV1 RequestWithAssets(
        int count,
        int bytesPerCopy,
        string compressionProfile)
    {
        var source = Request(compressionProfile);
        var digest = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Enumerable.Repeat((byte)7, bytesPerCopy).ToArray())).ToLowerInvariant();
        return source with
        {
            Assets = Enumerable.Range(1, count)
                .Select(index => new AiAssetInputV1(
                    $"asset_{index:D2}",
                    "sha256:" + new string((char)('a' + index % 26), 64),
                    "image/jpeg",
                    10,
                    10,
                    $"copy_{index:D2}",
                    digest,
                    bytesPerCopy,
                    "image/jpeg",
                    compressionProfile))
                .ToArray()
        };
    }

    private static AiAnalysisRequestV1 RequestWithAssetsAndRequirements(
        int assetCount,
        int requirementCount,
        int bytesPerCopy)
    {
        var source = RequestWithAssets(
            assetCount,
            bytesPerCopy,
            AiAnalysisCopyProfiles.WicBoundedV2);
        return source with
        {
            RequestId = "ai-batch-batch-f9667a3171bb407f9833baf6700663ef-style-01-joint-understanding",
            ModelConfigVersion = AiAnalysisProductionPolicy.ModelConfigVersionV2,
            PromptContractVersion = AiAnalysisProductionPolicy.PromptContractVersionV3,
            Requirements = Enumerable.Range(0, requirementCount)
                .Select(index => new AiRequirementInputV1(
                    $"learn_frame_{index:D2}_0123456789abcdef",
                    $"图片类别 {index + 1}",
                    "保持模板框构图",
                    true))
                .ToArray()
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

    private static string ProviderBody(
        string? status,
        IReadOnlyList<string>? outputSegments,
        int inputTokens = 3,
        int outputTokens = 4)
    {
        var root = new JsonObject
        {
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

        if (outputSegments is not null)
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

        return root.ToJsonString();
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

        public byte[]? RequestBodyBytes { get; private set; }

        public Version? RequestVersion { get; private set; }

        public HttpVersionPolicy RequestVersionPolicy { get; private set; }

        public bool? ExpectContinue { get; private set; }

        public TaskCompletionSource<bool> ResponderCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Authorization = request.Headers.Authorization;
            RequestVersion = request.Version;
            RequestVersionPolicy = request.VersionPolicy;
            ExpectContinue = request.Headers.ExpectContinue;
            RequestBodyBytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            RequestBody = Encoding.UTF8.GetString(RequestBodyBytes);
            try
            {
                return await _responder(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ResponderCompleted.TrySetResult(true);
            }
        }
    }

    private sealed class SerializingUploadHandler(BlockingUploadStream stream) : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await request.Content!.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            Completed.TrySetResult(true);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class BlockingUploadStream : Stream
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeCount;

        public TaskCompletionSource<bool> SecondWriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult(true);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writeCount) == 1)
            {
                return;
            }

            SecondWriteEntered.TrySetResult(true);
            await _release.Task.ConfigureAwait(false);
        }
    }

    private sealed record RecordedLog(
        string EventCode,
        string? ErrorCode,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class RecordingLogger : IRedactedLogger
    {
        public List<RecordedLog> InformationEvents { get; } = [];

        public List<RecordedLog> ErrorEvents { get; } = [];

        public void Information(string eventCode, IReadOnlyDictionary<string, object?> properties) =>
            InformationEvents.Add(new RecordedLog(eventCode, null, properties));

        public void LogError(
            string eventCode,
            string errorCode,
            IReadOnlyDictionary<string, object?> properties) =>
            ErrorEvents.Add(new RecordedLog(eventCode, errorCode, properties));

        public IEnumerable<string> AllSerializedValues() => InformationEvents
            .Concat(ErrorEvents)
            .Select(item => JsonSerializer.Serialize(item));
    }

    private sealed class SizedCopySource(int bytesPerCopy, IReadOnlyList<AiAssetInputV1> assets) : IAiAnalysisCopySource
    {
        private readonly byte[] _content = Enumerable.Repeat((byte)7, bytesPerCopy).ToArray();
        private readonly string _digest = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Enumerable.Repeat((byte)7, bytesPerCopy).ToArray())).ToLowerInvariant();
        private readonly IReadOnlyList<AiAssetInputV1> _assets = assets;

        public Task<AiAnalysisCopy> OpenAsync(string analysisCopyReference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asset = _assets.Single(item => string.Equals(item.AnalysisCopyReference, analysisCopyReference, StringComparison.Ordinal));
            return Task.FromResult(new AiAnalysisCopy(
                analysisCopyReference,
                "image/jpeg",
                10,
                10,
                _content,
                _digest,
                _content.Length,
                asset.AnalysisCopyCompressionProfile));
        }

        public Task ReleaseAsync(string analysisCopyReference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class DeferredStreamContent : HttpContent
    {
        private readonly TaskCompletionSource<Stream> _stream =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Disposed { get; } =
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed.TrySetResult(true);
            }

            base.Dispose(disposing);
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
