using System.Text.Json;
using Desktop.Application.Files;

namespace Desktop.Infrastructure.Files;

public sealed class JsonManifestStore(IFileStore fileStore) : IManifestStore, IRevisionCommitMarkerStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public Task SaveAsync(
        string relativePath,
        AppManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);
        return fileStore.WriteAtomicallyAsync(relativePath, bytes, cancellationToken);
    }

    public async Task<AppManifest?> LoadAsync(string relativePath, CancellationToken cancellationToken)
    {
        var bytes = await fileStore.ReadAsync(relativePath, cancellationToken).ConfigureAwait(false);
        return bytes is null
            ? null
            : JsonSerializer.Deserialize<AppManifest>(bytes, SerializerOptions);
    }

    public Task WriteReadyMarkerAsync(
        string revisionDirectory,
        RevisionCommitMarker marker,
        CancellationToken cancellationToken) => WriteMarkerAsync(
            revisionDirectory,
            "ready.marker",
            marker with { State = "ready" },
            cancellationToken);

    public Task WriteCommittedMarkerAsync(
        string revisionDirectory,
        RevisionCommitMarker marker,
        CancellationToken cancellationToken) => WriteMarkerAsync(
            revisionDirectory,
            "committed.marker",
            marker with { State = "committed" },
            cancellationToken);

    public async Task<RevisionCommitMarker?> ReadMarkerAsync(
        string revisionDirectory,
        string markerName,
        CancellationToken cancellationToken)
    {
        var normalized = markerName is "ready.marker" or "committed.marker"
            ? markerName
            : throw new ArgumentException("Unsupported marker name.", nameof(markerName));
        var bytes = await fileStore.ReadAsync(
            revisionDirectory.TrimEnd('/') + "/" + normalized,
            cancellationToken).ConfigureAwait(false);
        return bytes is null
            ? null
            : JsonSerializer.Deserialize<RevisionCommitMarker>(bytes, SerializerOptions);
    }

    private Task WriteMarkerAsync(
        string revisionDirectory,
        string markerName,
        RevisionCommitMarker marker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(marker, SerializerOptions);
        return fileStore.WriteAtomicallyAsync(
            revisionDirectory.TrimEnd('/') + "/" + markerName,
            bytes,
            cancellationToken);
    }
}
