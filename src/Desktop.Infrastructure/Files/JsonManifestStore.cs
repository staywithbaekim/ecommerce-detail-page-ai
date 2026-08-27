using System.Text.Json;
using Desktop.Application.Files;

namespace Desktop.Infrastructure.Files;

public sealed class JsonManifestStore(IFileStore fileStore) : IManifestStore
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
}
