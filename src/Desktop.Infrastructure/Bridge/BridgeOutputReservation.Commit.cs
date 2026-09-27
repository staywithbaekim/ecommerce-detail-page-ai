using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;

namespace Desktop.Infrastructure.Bridge;

public sealed partial class BridgeOutputReservation
{
    internal Task<PreparedRenderPage> PrepareCommitAsync(string taskId, string executionId, int attempt,
        CancellationToken cancellationToken)
    {
        EnsureMatches(PlanDigest, Binding);
        if (_verifiedMedia is null || _transferFaulted || attempt < 1) throw Invalid();
        using var document = JsonDocument.Parse(_planJson);
        var plan = document.RootElement;
        if (plan.GetProperty("batch_id").GetString() != taskId) throw Invalid();
        var page = new TaskCheckpoint(taskId, plan.GetProperty("product_id").GetString()!,
            plan.GetProperty("document").GetProperty("page_id").GetString()!, "render_output", attempt,
            PlanDigest, executionId, OutputCommitState.Verified, DateTimeOffset.UtcNow, RenderPlanVariantIdentity.Read(plan));
        var files = _verifiedMedia.Files.Select(file => new RenderCommittedFile(file.Kind,
            plan.GetProperty("output").GetProperty(file.Kind).GetProperty("file_name").GetString()!,
            file.ByteLength, file.Sha256)).ToArray();
        return RenderPageCommitter.PrepareAsync(_paths, _outputRelativePath, _stagingRelativePath,
            Binding.ArtifactSetId, page, _verifiedMedia, files,
            _uploads.ToDictionary(pair => pair.Key, pair => pair.Value.Stream), cancellationToken);
    }
}
