using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Application.Files;

namespace Desktop.Infrastructure.Persistence;

public sealed class AiAnalysisStateManifestStore(
    IFileStore files) : IAiAnalysisStateManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public async Task<StagedAiAnalysisStateManifest> StageAsync(
        AiAnalysisStateManifestV1 manifest,
        CancellationToken cancellationToken)
    {
        ValidateManifest(manifest);
        var content = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        var stageToken = $"stage_{Guid.NewGuid():N}";
        var staged = new StagedAiAnalysisStateManifest(
            manifest.BatchId,
            ManifestReference(manifest.BatchId),
            Digest(content),
            stageToken);
        var stageReference = StageReference(manifest.BatchId, stageToken);
        try
        {
            await files.WriteAtomicallyAsync(stageReference, content, cancellationToken).ConfigureAwait(false);
            await EnsureDigestAsync(stageReference, staged.ManifestDigest, cancellationToken).ConfigureAwait(false);
            await files.WriteAtomicallyAsync(
                StagedMarkerReference(manifest.BatchId),
                JsonSerializer.SerializeToUtf8Bytes(Marker.From(staged, "staged"), JsonOptions),
                cancellationToken).ConfigureAwait(false);
            return staged;
        }
        catch
        {
            await TryDeleteAsync(stageReference).ConfigureAwait(false);
            throw;
        }
    }

    public async Task CommitAsync(
        StagedAiAnalysisStateManifest stagedManifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stagedManifest);
        var stageReference = StageReference(stagedManifest.BatchId, stagedManifest.StageToken);
        var content = await files.ReadAsync(stageReference, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("AI_STATE_STAGE_MISSING");
        var manifest = JsonSerializer.Deserialize<AiAnalysisStateManifestV1>(content, JsonOptions)
            ?? throw new InvalidDataException("AI_STATE_MANIFEST_CORRUPT");
        ValidateManifest(manifest);
        await EnsureDigestAsync(stageReference, stagedManifest.ManifestDigest, cancellationToken).ConfigureAwait(false);
        await files.WriteAtomicallyAsync(stagedManifest.ManifestReference, content, cancellationToken).ConfigureAwait(false);
        await EnsureDigestAsync(stagedManifest.ManifestReference, stagedManifest.ManifestDigest, cancellationToken).ConfigureAwait(false);
        await files.WriteAtomicallyAsync(
            CommittedMarkerReference(stagedManifest.BatchId),
            JsonSerializer.SerializeToUtf8Bytes(Marker.From(stagedManifest, "committed"), JsonOptions),
            cancellationToken).ConfigureAwait(false);
        await TryDeleteAsync(StagedMarkerReference(stagedManifest.BatchId)).ConfigureAwait(false);
        await TryDeleteAsync(stageReference).ConfigureAwait(false);
    }

    public async Task RollbackAsync(
        StagedAiAnalysisStateManifest stagedManifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stagedManifest);
        await TryDeleteAsync(StageReference(stagedManifest.BatchId, stagedManifest.StageToken)).ConfigureAwait(false);
        var stagedMarker = await files.ReadAsync(StagedMarkerReference(stagedManifest.BatchId), cancellationToken).ConfigureAwait(false);
        if (stagedMarker is not null)
        {
            await TryDeleteAsync(StagedMarkerReference(stagedManifest.BatchId)).ConfigureAwait(false);
        }
    }

    public async Task<AiAnalysisStateManifestInspection?> InspectAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        ValidateId(batchId, "AI_BATCH_ID_INVALID");
        var committed = await ReadMarkerAsync(CommittedMarkerReference(batchId), cancellationToken).ConfigureAwait(false);
        var staged = await ReadMarkerAsync(StagedMarkerReference(batchId), cancellationToken).ConfigureAwait(false);
        var marker = committed ?? staged;
        if (marker is null)
        {
            return null;
        }

        if (!string.Equals(marker.BatchId, batchId, StringComparison.Ordinal)
            || marker.State is not ("staged" or "committed")
            || !string.Equals(marker.ManifestReference, ManifestReference(batchId), StringComparison.Ordinal))
        {
            throw new InvalidDataException("AI_STATE_MARKER_INVALID");
        }

        var reference = committed is null
            ? StageReference(batchId, marker.StageToken)
            : marker.ManifestReference;
        await EnsureDigestAsync(reference, marker.ManifestDigest, cancellationToken).ConfigureAwait(false);
        return new AiAnalysisStateManifestInspection(batchId, marker.ManifestReference, marker.ManifestDigest, staged is not null, committed is not null);
    }

    private async Task<Marker?> ReadMarkerAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await files.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        return bytes is null
            ? null
            : JsonSerializer.Deserialize<Marker>(bytes, JsonOptions)
                ?? throw new InvalidDataException("AI_STATE_MARKER_CORRUPT");
    }

    private async Task EnsureDigestAsync(string path, string expectedDigest, CancellationToken cancellationToken)
    {
        var actual = await files.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("AI_STATE_MANIFEST_MISSING");
        if (!string.Equals("sha256:" + actual.ToLowerInvariant(), expectedDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("AI_STATE_MANIFEST_DIGEST_MISMATCH");
        }
    }

    private async Task TryDeleteAsync(string path)
    {
        try { _ = await files.DeleteAsync(path, CancellationToken.None).ConfigureAwait(false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void ValidateManifest(AiAnalysisStateManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.SchemaVersion, AiAnalysisStateProtocol.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("AI_STATE_SCHEMA_VERSION_INVALID");
        }

        ValidateId(manifest.ManifestId, "AI_STATE_MANIFEST_ID_INVALID");
        ValidateId(manifest.BatchId, "AI_BATCH_ID_INVALID");
        ValidateIds(manifest.SessionIds, "AI_STATE_SESSION_IDS_INVALID");
        ValidateIds(manifest.RequestIds, "AI_STATE_REQUEST_IDS_INVALID");
        ValidateIds(manifest.CacheKeys, "AI_STATE_CACHE_KEYS_INVALID");
    }

    private static void ValidateIds(IEnumerable<string> values, string code)
    {
        ArgumentNullException.ThrowIfNull(values);
        var list = values.ToArray();
        if (list.Any(value => string.IsNullOrWhiteSpace(value))
            || list.Distinct(StringComparer.Ordinal).Count() != list.Length)
        {
            throw new InvalidDataException(code);
        }
    }

    private static void ValidateId(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new InvalidDataException(code);
        }
    }

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Root(string batchId) => $"batches/{batchId}/ai-analysis/state";
    private static string ManifestReference(string batchId) => $"{Root(batchId)}/manifest.json";
    private static string StageReference(string batchId, string token) => $"{Root(batchId)}/staging/{token}.json";
    private static string StagedMarkerReference(string batchId) => $"{Root(batchId)}/staged.marker";
    private static string CommittedMarkerReference(string batchId) => $"{Root(batchId)}/committed.marker";

    private sealed record Marker(
        string BatchId,
        string ManifestReference,
        string ManifestDigest,
        string StageToken,
        string State,
        DateTimeOffset WrittenAt)
    {
        public static Marker From(StagedAiAnalysisStateManifest staged, string state) =>
            new(staged.BatchId, staged.ManifestReference, staged.ManifestDigest, staged.StageToken, state, DateTimeOffset.UtcNow);
    }
}
