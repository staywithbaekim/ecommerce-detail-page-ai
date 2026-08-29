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
        ValidateStagedManifest(stagedManifest);
        var stagedMarker = await ReadMarkerAsync(StagedMarkerReference(stagedManifest.BatchId), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("AI_STATE_STAGED_MARKER_MISSING");
        ValidateMarker(stagedMarker, stagedManifest.BatchId, "staged");
        EnsureStagedMatches(stagedMarker, stagedManifest);
        var stageReference = StageReference(stagedManifest.BatchId, stagedManifest.StageToken);
        var content = await files.ReadAsync(stageReference, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("AI_STATE_STAGE_MISSING");
        var manifest = JsonSerializer.Deserialize<AiAnalysisStateManifestV1>(content, JsonOptions)
            ?? throw new InvalidDataException("AI_STATE_MANIFEST_CORRUPT");
        ValidateManifest(manifest);
        if (!string.Equals(manifest.BatchId, stagedManifest.BatchId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("AI_STATE_STAGE_BATCH_MISMATCH");
        }
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
        ValidateStagedManifest(stagedManifest);
        var marker = await ReadMarkerAsync(StagedMarkerReference(stagedManifest.BatchId), cancellationToken).ConfigureAwait(false);
        if (marker is not null)
        {
            ValidateMarker(marker, stagedManifest.BatchId, "staged");
            if (StagedMatches(marker, stagedManifest))
            {
                await TryDeleteAsync(StagedMarkerReference(stagedManifest.BatchId)).ConfigureAwait(false);
            }
        }

        await TryDeleteAsync(StageReference(stagedManifest.BatchId, stagedManifest.StageToken)).ConfigureAwait(false);
    }

    public async Task<AiAnalysisStateManifestInspection?> InspectAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        ValidateId(batchId, "AI_BATCH_ID_INVALID");
        var committed = await ReadMarkerAsync(CommittedMarkerReference(batchId), cancellationToken).ConfigureAwait(false);
        var staged = await ReadMarkerAsync(StagedMarkerReference(batchId), cancellationToken).ConfigureAwait(false);
        if (committed is null && staged is null)
        {
            return null;
        }

        if (committed is not null)
        {
            ValidateMarker(committed, batchId, "committed");
            await EnsureDigestAsync(ManifestReference(batchId), committed.ManifestDigest, cancellationToken).ConfigureAwait(false);
        }

        if (staged is not null)
        {
            ValidateMarker(staged, batchId, "staged");
            await EnsureDigestAsync(StageReference(batchId, staged.StageToken), staged.ManifestDigest, cancellationToken).ConfigureAwait(false);
        }

        var marker = committed ?? staged!;
        return new AiAnalysisStateManifestInspection(batchId, marker.ManifestReference, marker.ManifestDigest, staged is not null, committed is not null);
    }

    private async Task<Marker?> ReadMarkerAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await files.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            return bytes is null
                ? null
                : JsonSerializer.Deserialize<Marker>(bytes, JsonOptions)
                    ?? throw new InvalidDataException("AI_STATE_MARKER_CORRUPT");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("AI_STATE_MARKER_CORRUPT", exception);
        }
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
        ValidateIds(manifest.SessionIds, "AI_STATE_SESSION_IDS_INVALID", value => ValidateId(value, "AI_STATE_SESSION_IDS_INVALID"));
        ValidateIds(manifest.RequestIds, "AI_STATE_REQUEST_IDS_INVALID", value => ValidateId(value, "AI_STATE_REQUEST_IDS_INVALID"));
        ValidateIds(manifest.CacheKeys, "AI_STATE_CACHE_KEYS_INVALID", value => ValidateDigest(value, "AI_STATE_CACHE_KEYS_INVALID"));
    }

    private static void ValidateIds(IEnumerable<string> values, string code, Action<string> validator)
    {
        if (values is null)
        {
            throw new InvalidDataException(code);
        }
        var list = values.ToArray();
        if (list.Length > 4096
            || list.Distinct(StringComparer.Ordinal).Count() != list.Length)
        {
            throw new InvalidDataException(code);
        }

        try
        {
            foreach (var value in list)
            {
                validator(value);
            }
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(code, exception);
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

    private static void ValidateDigest(string value, string code)
    {
        if (value is null
            || value.Length != 71
            || !value.StartsWith("sha256:", StringComparison.Ordinal)
            || value.Skip(7).Any(character => !((character >= '0' && character <= '9')
                || (character >= 'a' && character <= 'f'))))
        {
            throw new InvalidDataException(code);
        }
    }

    private static void ValidateStagedManifest(StagedAiAnalysisStateManifest stagedManifest)
    {
        ArgumentNullException.ThrowIfNull(stagedManifest);
        ValidateId(stagedManifest.BatchId, "AI_BATCH_ID_INVALID");
        ValidateId(stagedManifest.StageToken, "AI_STATE_STAGE_TOKEN_INVALID");
        ValidateDigest(stagedManifest.ManifestDigest, "AI_STATE_MANIFEST_DIGEST_INVALID");
        if (!string.Equals(stagedManifest.ManifestReference, ManifestReference(stagedManifest.BatchId), StringComparison.Ordinal))
        {
            throw new InvalidDataException("AI_STATE_MANIFEST_REFERENCE_INVALID");
        }
    }

    private static void ValidateMarker(Marker marker, string batchId, string expectedState)
    {
        if (!string.Equals(marker.BatchId, batchId, StringComparison.Ordinal)
            || !string.Equals(marker.State, expectedState, StringComparison.Ordinal)
            || !string.Equals(marker.ManifestReference, ManifestReference(batchId), StringComparison.Ordinal)
            || marker.WrittenAt == default)
        {
            throw new InvalidDataException("AI_STATE_MARKER_INVALID");
        }

        ValidateId(marker.StageToken, "AI_STATE_STAGE_TOKEN_INVALID");
        ValidateDigest(marker.ManifestDigest, "AI_STATE_MANIFEST_DIGEST_INVALID");
    }

    private static bool StagedMatches(Marker marker, StagedAiAnalysisStateManifest stagedManifest) =>
        string.Equals(marker.BatchId, stagedManifest.BatchId, StringComparison.Ordinal)
        && string.Equals(marker.ManifestReference, stagedManifest.ManifestReference, StringComparison.Ordinal)
        && string.Equals(marker.ManifestDigest, stagedManifest.ManifestDigest, StringComparison.Ordinal)
        && string.Equals(marker.StageToken, stagedManifest.StageToken, StringComparison.Ordinal);

    private static void EnsureStagedMatches(Marker marker, StagedAiAnalysisStateManifest stagedManifest)
    {
        if (!StagedMatches(marker, stagedManifest))
        {
            throw new InvalidDataException("AI_STATE_STAGED_MARKER_CONFLICT");
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
