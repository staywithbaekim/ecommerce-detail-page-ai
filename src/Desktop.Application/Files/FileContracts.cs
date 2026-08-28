namespace Desktop.Application.Files;

public interface IPathPolicy
{
    string ResolveOwnedPath(string relativePath);

    bool IsOwnedPath(string path);
}

public interface IFileStore
{
    Task WriteAtomicallyAsync(string relativePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken);

    Task<string?> ComputeSha256Async(string relativePath, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string relativePath, CancellationToken cancellationToken);
}

public sealed record ManifestEntry(
    string LogicalId,
    string RelativePath,
    long Length,
    string Sha256,
    string State);

public sealed record AppManifest(
    string ManifestId,
    int Version,
    IReadOnlyList<ManifestEntry> Entries,
    DateTimeOffset UpdatedAt);

public interface IManifestStore
{
    Task SaveAsync(string relativePath, AppManifest manifest, CancellationToken cancellationToken);

    Task<AppManifest?> LoadAsync(string relativePath, CancellationToken cancellationToken);
}

public sealed record RevisionCommitMarker(
    string RevisionId,
    string ManifestPath,
    string ManifestSha256,
    string State,
    DateTimeOffset WrittenAt);

public interface IRevisionCommitMarkerStore
{
    Task WriteReadyMarkerAsync(
        string revisionDirectory,
        RevisionCommitMarker marker,
        CancellationToken cancellationToken);

    Task WriteCommittedMarkerAsync(
        string revisionDirectory,
        RevisionCommitMarker marker,
        CancellationToken cancellationToken);

    Task<RevisionCommitMarker?> ReadMarkerAsync(
        string revisionDirectory,
        string markerName,
        CancellationToken cancellationToken);
}
