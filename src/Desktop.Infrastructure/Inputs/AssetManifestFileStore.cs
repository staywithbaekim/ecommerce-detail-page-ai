using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Application.Files;
using Desktop.Application.Inputs;
using Desktop.Contracts.Validation;

namespace Desktop.Infrastructure.Inputs;

public sealed class AssetManifestFileStore(
    IFileStore files,
    IJsonContractValidator contracts) : IAssetManifestStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public async Task<StagedAssetManifest> StageAsync(
        AssetManifestV1 manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var content = JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);
        ValidateContract(content);

        var stageToken = $"stage_{Guid.NewGuid():N}";
        var manifestReference = ManifestReference(manifest.BatchId);
        var stageReference = StageReference(manifest.BatchId, stageToken);
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var staged = new StagedAssetManifest(
            manifest.BatchId,
            manifestReference,
            digest,
            stageToken);
        try
        {
            await files.WriteAtomicallyAsync(stageReference, content, cancellationToken).ConfigureAwait(false);
            await EnsureDigestAsync(stageReference, digest, cancellationToken).ConfigureAwait(false);
            await WriteMarkerAsync(
                StagedMarkerReference(manifest.BatchId),
                ManifestMarker.From(staged, "staged"),
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
        StagedAssetManifest stagedManifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stagedManifest);
        var stageReference = StageReference(stagedManifest.BatchId, stagedManifest.StageToken);
        var content = await files.ReadAsync(stageReference, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("INPUT_MANIFEST_STAGE_MISSING");
        ValidateContract(content);
        await EnsureDigestAsync(stageReference, stagedManifest.ManifestDigest, cancellationToken)
            .ConfigureAwait(false);

        await files.WriteAtomicallyAsync(
            stagedManifest.ManifestReference,
            content,
            cancellationToken).ConfigureAwait(false);
        await EnsureDigestAsync(
            stagedManifest.ManifestReference,
            stagedManifest.ManifestDigest,
            cancellationToken).ConfigureAwait(false);
        await WriteMarkerAsync(
            CommittedMarkerReference(stagedManifest.BatchId),
            ManifestMarker.From(stagedManifest, "committed"),
            cancellationToken).ConfigureAwait(false);

        await TryDeleteAsync(StagedMarkerReference(stagedManifest.BatchId)).ConfigureAwait(false);
        await TryDeleteAsync(stageReference).ConfigureAwait(false);
    }

    public async Task RollbackAsync(
        StagedAssetManifest stagedManifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stagedManifest);
        cancellationToken.ThrowIfCancellationRequested();
        var committed = await ReadMarkerAsync(
            CommittedMarkerReference(stagedManifest.BatchId),
            cancellationToken).ConfigureAwait(false);
        if (committed is not null
            && string.Equals(committed.ManifestDigest, stagedManifest.ManifestDigest, StringComparison.Ordinal))
        {
            return;
        }

        await TryDeleteAsync(StageReference(stagedManifest.BatchId, stagedManifest.StageToken)).ConfigureAwait(false);
        var finalDigest = await files.ComputeSha256Async(
            stagedManifest.ManifestReference,
            CancellationToken.None).ConfigureAwait(false);
        if (finalDigest is not null
            && string.Equals(
                "sha256:" + finalDigest.ToLowerInvariant(),
                stagedManifest.ManifestDigest,
                StringComparison.Ordinal))
        {
            await TryDeleteAsync(stagedManifest.ManifestReference).ConfigureAwait(false);
        }
        var staged = await ReadMarkerAsync(
            StagedMarkerReference(stagedManifest.BatchId),
            cancellationToken).ConfigureAwait(false);
        if (staged is not null
            && string.Equals(staged.StageToken, stagedManifest.StageToken, StringComparison.Ordinal))
        {
            await TryDeleteAsync(StagedMarkerReference(stagedManifest.BatchId)).ConfigureAwait(false);
        }
    }

    public async Task<AssetManifestInspection?> InspectAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        var committed = await ReadMarkerAsync(
            CommittedMarkerReference(batchId),
            cancellationToken).ConfigureAwait(false);
        var staged = await ReadMarkerAsync(
            StagedMarkerReference(batchId),
            cancellationToken).ConfigureAwait(false);
        var marker = committed ?? staged;
        if (marker is null)
        {
            return null;
        }

        if (!string.Equals(marker.BatchId, batchId, StringComparison.Ordinal)
            || marker.State is not ("staged" or "committed"))
        {
            throw new InvalidDataException("INPUT_MANIFEST_MARKER_INVALID");
        }

        var reference = committed is null
            ? StageReference(batchId, marker.StageToken)
            : marker.ManifestReference;
        var content = await files.ReadAsync(reference, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("INPUT_MANIFEST_FILE_MISSING");
        ValidateContract(content);
        await EnsureDigestAsync(reference, marker.ManifestDigest, cancellationToken).ConfigureAwait(false);
        return new AssetManifestInspection(
            batchId,
            marker.ManifestReference,
            marker.ManifestDigest,
            IsStaged: staged is not null,
            IsCommitted: committed is not null);
    }

    private void ValidateContract(ReadOnlySpan<byte> content)
    {
        var result = contracts.Validate(ContractDocumentKind.AssetManifest, Encoding.UTF8.GetString(content));
        if (!result.IsValid)
        {
            throw new InvalidDataException("INPUT_MANIFEST_CONTRACT_INVALID");
        }
    }

    private async Task EnsureDigestAsync(
        string relativePath,
        string expectedDigest,
        CancellationToken cancellationToken)
    {
        var actual = await files.ComputeSha256Async(relativePath, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("INPUT_MANIFEST_FILE_MISSING");
        var normalized = "sha256:" + actual.ToLowerInvariant();
        if (!string.Equals(normalized, expectedDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("INPUT_MANIFEST_DIGEST_MISMATCH");
        }
    }

    private Task WriteMarkerAsync(
        string relativePath,
        ManifestMarker marker,
        CancellationToken cancellationToken) => files.WriteAtomicallyAsync(
        relativePath,
        JsonSerializer.SerializeToUtf8Bytes(marker, SerializerOptions),
        cancellationToken);

    private async Task<ManifestMarker?> ReadMarkerAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        var bytes = await files.ReadAsync(relativePath, cancellationToken).ConfigureAwait(false);
        return bytes is null
            ? null
            : JsonSerializer.Deserialize<ManifestMarker>(bytes, SerializerOptions)
                ?? throw new InvalidDataException("INPUT_MANIFEST_MARKER_INVALID");
    }

    private async Task TryDeleteAsync(string relativePath)
    {
        try
        {
            _ = await files.DeleteAsync(relativePath, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Cleanup is recoverable from marker inspection and must not rewrite a successful commit result.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is recoverable from marker inspection and must not rewrite a successful commit result.
        }
    }

    private static string ManifestDirectory(string batchId) => $"batches/{batchId}/asset-manifest";

    private static string ManifestReference(string batchId) => $"{ManifestDirectory(batchId)}/manifest.json";

    private static string StageReference(string batchId, string stageToken) =>
        $"{ManifestDirectory(batchId)}/staging/{stageToken}.json";

    private static string StagedMarkerReference(string batchId) =>
        $"{ManifestDirectory(batchId)}/staged.marker";

    private static string CommittedMarkerReference(string batchId) =>
        $"{ManifestDirectory(batchId)}/committed.marker";

    private sealed record ManifestMarker(
        string BatchId,
        string ManifestReference,
        string ManifestDigest,
        string StageToken,
        string State,
        DateTimeOffset WrittenAt)
    {
        public static ManifestMarker From(StagedAssetManifest staged, string state) => new(
            staged.BatchId,
            staged.ManifestReference,
            staged.ManifestDigest,
            staged.StageToken,
            state,
            DateTimeOffset.UtcNow);
    }
}
