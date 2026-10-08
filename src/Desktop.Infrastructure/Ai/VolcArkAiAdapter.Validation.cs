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

public sealed partial class VolcArkAiAdapter
{
    private static void ValidateCopy(
        AiAnalysisRequestV1 request,
        AiAssetInputV1 asset,
        AiAnalysisCopy copy)
    {
        if (!string.Equals(copy.Reference, asset.AnalysisCopyReference, StringComparison.Ordinal)
            || !IsSupportedMime(copy.MimeType)
            || copy.PixelWidth <= 0 || copy.PixelHeight <= 0)
            throw new InvalidDataException("AI_ANALYSIS_COPY_METADATA_INVALID");
        var digest = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(copy.Content.Span)).ToLowerInvariant();
        if (copy.ContentDigest is null
            || !string.Equals(copy.ContentDigest, digest, StringComparison.Ordinal))
            throw new InvalidDataException("AI_ANALYSIS_COPY_DIGEST_MISMATCH");
        if (copy.ByteLength is null || copy.ByteLength != copy.Content.Length)
            throw new InvalidDataException("AI_ANALYSIS_COPY_LENGTH_MISMATCH");
        if (asset.AnalysisCopyDigest is null
            || !string.Equals(asset.AnalysisCopyDigest, digest, StringComparison.Ordinal))
            throw new InvalidDataException("AI_ANALYSIS_COPY_DIGEST_MISMATCH");
        if (asset.AnalysisCopyLength is null || asset.AnalysisCopyLength != copy.Content.Length)
            throw new InvalidDataException("AI_ANALYSIS_COPY_LENGTH_MISMATCH");
        if (asset.AnalysisCopyMimeType is null
            || !string.Equals(asset.AnalysisCopyMimeType, copy.MimeType, StringComparison.Ordinal))
            throw new InvalidDataException("AI_ANALYSIS_COPY_MIME_MISMATCH");
        if (asset.AnalysisCopyCompressionProfile is null
            || copy.CompressionProfileVersion is null
            || !string.Equals(request.CompressionProfileVersion, asset.AnalysisCopyCompressionProfile, StringComparison.Ordinal)
            || !string.Equals(asset.AnalysisCopyCompressionProfile, copy.CompressionProfileVersion, StringComparison.Ordinal))
            throw new InvalidDataException("AI_ANALYSIS_COPY_PROFILE_MISMATCH");
    }

    private static bool IsSupportedMime(string value) => value is "image/jpeg" or "image/png";
    private static void ValidateOptions(VolcArkAiOptions options)
    {
        if (!string.Equals(options.ModelId, "doubao-seed-2-1-pro-260628", StringComparison.Ordinal)
            || !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || options.EffectiveRequestTimeout <= TimeSpan.Zero
            || options.EffectiveRequestTimeout > TimeSpan.FromSeconds(300)
            || options.EffectiveUploadTimeout <= TimeSpan.Zero
            || options.EffectiveUploadTimeout > TimeSpan.FromSeconds(240)
            || options.EffectiveUploadIdleTimeout <= TimeSpan.Zero
            || options.EffectiveUploadIdleTimeout > TimeSpan.FromSeconds(90)
            || options.EffectiveResponseHeadersTimeout <= TimeSpan.Zero
            || options.EffectiveResponseHeadersTimeout > TimeSpan.FromSeconds(120)
            || options.EffectiveResponseBodyTimeout <= TimeSpan.Zero
            || options.EffectiveResponseBodyTimeout > TimeSpan.FromSeconds(60)
            || options.MaxRequestBytes is < 1024 or > 16 * 1024 * 1024
            || options.MaxResponseBytes is < 1024 or > 16 * 1024 * 1024
            || options.MaxAssetsPerRequest is < 1 or > AiAnalysisProductionPolicy.MaxTargetAssetsPerRequest
            || options.MaxReferenceAssetsPerRequest is < 1 or > AiAnalysisProductionPolicy.MaxReferenceAssetsPerRequest)
        {
            throw new ArgumentException("火山方舟配置不符合已批准的安全边界。", nameof(options));
        }
    }

    private static void ValidateRequest(AiAnalysisRequestV1 request, int maxAssetsPerRequest)
    {
        if (!string.Equals(request.SchemaVersion, AiAnalysisProtocol.RequestSchemaVersion, StringComparison.Ordinal)
            || !string.Equals(request.DocumentKind, "request", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.RequestId)
            || request.Assets.Count == 0
            || request.Assets.Count > maxAssetsPerRequest
            || !IsApprovedCompressionProfile(request.CompressionProfileVersion)
            || request.Assets.Any(asset =>
                asset.AnalysisCopyDigest is null
                || asset.AnalysisCopyLength is null or <= 0
                || asset.AnalysisCopyMimeType is null
                || !IsSupportedMime(asset.AnalysisCopyMimeType)
                || asset.AnalysisCopyCompressionProfile is null
                || !string.Equals(
                    request.CompressionProfileVersion,
                    asset.AnalysisCopyCompressionProfile,
                    StringComparison.Ordinal))
            || request.Requirements.Count == 0)
        {
            throw new InvalidOperationException("AI_ANALYSIS_REQUEST_INVALID");
        }
    }

    private static bool IsApprovedCompressionProfile(string value) =>
        AiAnalysisCopyProfiles.IsApproved(value);

    private static void ConfigureTransportRequest(HttpRequestMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Use the conventional HTTP/1.1 request shape, but leave the policy
        // negotiable/fallback-capable.  An exact-version pin adds an avoidable
        // connection negotiation delay on this endpoint and is not required by
        // the provider contract.  The explicit header prevents a delayed
        // 100-continue handshake from consuming the upload budget.
        message.Version = HttpVersion.Version11;
        message.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        message.Headers.ExpectContinue = false;
    }

}
