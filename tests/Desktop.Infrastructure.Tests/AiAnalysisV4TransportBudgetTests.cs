using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Desktop.Application.Ai;
using Desktop.Application.Files;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Tests;

public sealed class AiAnalysisV4TransportBudgetTests
{
    [Fact]
    public async Task FortyJpegOpaquePngAndTransparentPngCopiesStayBoundedAndFinalJsonFitsBudget()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-v4-40-" + Guid.NewGuid().ToString("N"));
        var inputRoot = Path.Combine(root, "input");
        var appRoot = Path.Combine(root, "app");
        Directory.CreateDirectory(inputRoot);
        try
        {
            var sources = new List<(SourceAssetMetadata Source, string ExpectedCopyMime)>();
            for (var index = 0; index < AiAnalysisProductionPolicy.MaxTargetAssetsPerRequest; index++)
            {
                var isPng = index % 2 == 0;
                var isOpaque = !isPng || index % 4 == 0;
                var format = isPng ? SourceImageFormat.Png : SourceImageFormat.Jpeg;
                var extension = isPng ? ".png" : ".jpg";
                var fileName = $"asset_{index + 1:D2}{extension}";
                var bytes = CreateImage(format, 320, 240, index + 11, isOpaque);
                await File.WriteAllBytesAsync(Path.Combine(inputRoot, fileName), bytes);
                var source = SourceAssetMetadata.CreateReadable(
                    $"asset_{index + 1:D2}",
                    "style_01",
                    fileName,
                    fileName,
                    bytes.Length,
                    Digest(bytes),
                    320,
                    240,
                    format,
                    format == SourceImageFormat.Png ? "image/png" : "image/jpeg");
                sources.Add((source, isPng && !isOpaque ? "image/png" : "image/jpeg"));
            }

            var policy = new LocalAppPathPolicy(appRoot);
            using var preparer = new FileAiAnalysisCopyPreparer(policy, new AtomicFileStore(policy));
            var prepared = new List<AiAnalysisCopyMetadata>(sources.Count);
            try
            {
                foreach (var (source, _) in sources)
                {
                    prepared.Add(await preparer.PrepareAsync(
                        new AiAnalysisCopyPreparationRequest(
                            "copy_" + source.AssetId,
                            inputRoot,
                            source,
                            AiAnalysisCopyProfiles.WicBoundedV4),
                        CancellationToken.None));
                }

                Assert.Equal(AiAnalysisProductionPolicy.MaxTargetAssetsPerRequest, prepared.Count);
                Assert.Equal(
                    sources.Count(item => item.ExpectedCopyMime == "image/jpeg"),
                    prepared.Count(copy => copy.MimeType == "image/jpeg"));
                Assert.Equal(
                    sources.Count(item => item.ExpectedCopyMime == "image/png"),
                    prepared.Count(copy => copy.MimeType == "image/png"));
                Assert.All(prepared, copy =>
                {
                    Assert.Equal(AiAnalysisCopyProfiles.WicBoundedV4, copy.CompressionProfileVersion);
                    Assert.InRange(copy.ByteLength, 1, 8 * 1024 * 1024);
                    Assert.InRange(Math.Max(copy.PixelWidth, copy.PixelHeight), 1, 1280);
                });

                var request = new AiAnalysisRequestV1(
                    AiAnalysisProtocol.RequestSchemaVersion,
                    "request",
                    "request_v4_40",
                    "corr_v4_40",
                    "product_v4_40",
                    "batch_v4_40",
                    "style_01",
                    "snapshot_v4",
                    "revision_v4",
                    AiAnalysisKind.ProductJointUnderstanding,
                    AiAnalysisProductionPolicy.ModelConfigVersionV2,
                    AiAnalysisProductionPolicy.PromptContractVersionV3,
                    AiAnalysisProtocol.ResultSchemaVersion,
                    AiAnalysisCopyProfiles.WicBoundedV4,
                    "consent_v4",
                    "sha256:" + new string('a', 64),
                    prepared.Select((copy, index) =>
                    {
                        var source = sources[index].Source;
                        return new AiAssetInputV1(
                            source.AssetId,
                            "sha256:" + source.Sha256,
                            source.MimeType,
                            source.PixelWidth,
                            source.PixelHeight,
                            copy.Reference,
                            copy.Digest,
                            copy.ByteLength,
                            copy.MimeType,
                            copy.CompressionProfileVersion,
                            copy.LeaseId);
                    }).ToArray(),
                    [new AiRequirementInputV1("frame_01", "商品主体图", "主体完整", true)]);

                var handler = new CaptureHandler(request);
                var adapter = new VolcArkAiAdapter(
                    new HttpClient(handler),
                    new CredentialSource(),
                    new FileAiAnalysisCopySource(policy),
                    new JsonSchemaContractValidator(),
                    new VolcArkAiOptions(ReleaseCopiesAfterRequest: false));
                var execution = await adapter.AnalyzeAsync(request, CancellationToken.None);

                Assert.Equal(AiProviderOutcome.Completed, execution.Outcome);
                Assert.Equal(1, handler.CallCount);
                Assert.NotNull(handler.RequestBody);
                Assert.InRange(handler.RequestBody!.Length, 1, 16 * 1024 * 1024);
                Assert.Equal(
                    Encoding.UTF8.GetByteCount(handler.RequestBody),
                    handler.RequestBodyBytes!.Length);
                using var payload = JsonDocument.Parse(handler.RequestBody);
                var content = payload.RootElement.GetProperty("input")[0].GetProperty("content");
                Assert.Equal(
                    AiAnalysisProductionPolicy.MaxTargetAssetsPerRequest,
                    content.EnumerateArray().Count(item =>
                        item.TryGetProperty("type", out var type)
                        && type.GetString() == "input_image"));
            }
            finally
            {
                foreach (var copy in prepared)
                {
                    try { await preparer.ReleaseAsync(copy.Reference, copy.LeaseId, CancellationToken.None); }
                    catch { }
                }
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static byte[] CreateImage(
        SourceImageFormat format,
        int width,
        int height,
        int seed,
        bool opaque)
    {
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        var random = new Random(seed);
        random.NextBytes(pixels);
        for (var index = 3; index < pixels.Length; index += 4)
        {
            pixels[index] = opaque ? byte.MaxValue : (byte)(32 + pixels[index] % 192);
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        BitmapEncoder encoder = format == SourceImageFormat.Png
            ? new PngBitmapEncoder()
            : new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class CredentialSource : IAiCredentialSource
    {
        public ValueTask<string?> GetCredentialAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>("test-key");
    }

    private sealed class CaptureHandler(AiAnalysisRequestV1 request) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? RequestBody { get; private set; }
        public byte[]? RequestBodyBytes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage message,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestBodyBytes = await message.Content!.ReadAsByteArrayAsync(cancellationToken);
            RequestBody = Encoding.UTF8.GetString(RequestBodyBytes);
            var result = new AiAnalysisResultV1(
                AiAnalysisProtocol.ResultSchemaVersion,
                "result",
                request.RequestId,
                null,
                request.ModelConfigVersion,
                [],
                [],
                [],
                null,
                [],
                new AiUsageSummaryV1("usage_v4_40", 1, 1, 1, null, null));
            var providerBody = JsonSerializer.Serialize(new
            {
                id = "response_v4_40",
                status = "completed",
                output = new[]
                {
                    new
                    {
                        type = "message",
                        content = new[]
                        {
                            new
                            {
                                type = "output_text",
                                text = JsonSerializer.Serialize(result)
                            }
                        }
                    }
                },
                usage = new { input_tokens = 1, output_tokens = 1, total_tokens = 2 }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(providerBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
