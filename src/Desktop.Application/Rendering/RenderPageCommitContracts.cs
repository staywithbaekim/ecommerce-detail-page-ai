using Desktop.Application.Tasks;

namespace Desktop.Application.Rendering;

/// <summary>Durable page identity and verified manifest. Contains no transport secrets.</summary>
public sealed record RenderPageCommitCheckpoint(TaskCheckpoint Page, string ManifestId,
    string ManifestRelativePath, long ManifestByteLength, string ManifestSha256);

public interface IRenderPageCommitStore
{
    Task PrepareRenderCommitAsync(RenderPageCommitCheckpoint checkpoint, CancellationToken cancellationToken);
    Task CompleteRenderCommitAsync(RenderPageCommitCheckpoint checkpoint, CancellationToken cancellationToken);
    Task<OutputCommitState?> ReadRenderCommitAsync(RenderPageCommitCheckpoint checkpoint, CancellationToken cancellationToken);
}
