using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Ai;

public enum AiRenderPreparationStatus
{
    ReadyToRender = 0,
    WaitingForPhotoshop,
    UserActionRequired
}

public enum AiRenderPagePreparationStatus
{
    ReadyToRender = 0,
    AwaitingSubjectAnalysis,
    AwaitingSizeTableRasterization,
    BusinessBlocked
}

public sealed record AiConfirmedRenderSelection(
    string FrameId,
    string AssetId,
    AiSubjectGeometryV1? SubjectGeometry,
    long SelectionRevision);

public sealed record AiRenderStyleInput(
    string StyleId,
    IReadOnlyList<AiConfirmedRenderSelection> Selections,
    AiSizeTableCandidateV1? SizeTableCandidate = null);

public sealed record AiRenderPlanPreparationRequest(
    BatchDraft Batch,
    TemplateRevision TemplateRevision,
    IReadOnlyList<ImportedTemplateResource> TemplateResources,
    IReadOnlyList<AiRenderStyleInput> Styles,
    DateTimeOffset CreatedAt);

public sealed record AiRenderPagePreparation(
    string StyleId,
    string PageId,
    AiRenderPagePreparationStatus Status,
    string ReasonCode,
    string? RenderPlanJson = null,
    string? PlanDigest = null)
{
    public bool HasPlan => RenderPlanJson is not null && PlanDigest is not null;
}

public sealed record AiRenderPlanPreparation(
    string BatchId,
    AiRenderPreparationStatus Status,
    string ReasonCode,
    IReadOnlyList<AiRenderPagePreparation> Pages,
    bool HasFormalOutput = false)
{
    public int ReadyPageCount => Pages.Count(page => page.Status == AiRenderPagePreparationStatus.ReadyToRender);
}

public interface IAiRenderPlanFactory
{
    Task<AiRenderPlanPreparation> PrepareAsync(
        AiRenderPlanPreparationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Closes AI-01 at the renderer-neutral boundary. It consumes only the frozen
/// batch snapshot, confirmed selections, provider-neutral subject geometry
/// and an optional size candidate. It never reads source paths, calls a
/// provider, submits to Photoshop or writes an output file.
/// </summary>
public sealed class AiRenderPlanFactory(
    ITemplateRenderPlanBuilder planBuilder,
    IPhotoshopAvailabilityPort photoshopAvailability) : IAiRenderPlanFactory
{
    public async Task<AiRenderPlanPreparation> PrepareAsync(
        AiRenderPlanPreparationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSnapshot(request.Batch, request.TemplateRevision);

        var styles = request.Styles.ToDictionary(style => style.StyleId, StringComparer.Ordinal);
        if (styles.Count != request.Styles.Count
            || request.Batch.Styles.Any(style => !styles.ContainsKey(style.StyleId))
            || styles.Keys.Any(styleId => request.Batch.Styles.All(style =>
                !string.Equals(style.StyleId, styleId, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("AI_RENDER_STYLE_INPUT_MISMATCH");
        }

        var templateResources = request.TemplateResources.ToDictionary(
            resource => resource.ResourceId,
            StringComparer.Ordinal);
        if (templateResources.Count != request.TemplateResources.Count)
        {
            throw new InvalidOperationException("AI_RENDER_TEMPLATE_RESOURCE_DUPLICATE");
        }

        var revisionPages = request.TemplateRevision.Pages.ToDictionary(
            page => page.PageId,
            StringComparer.Ordinal);
        var bindingsByPage = request.TemplateRevision.FrameBindings
            .GroupBy(binding => binding.PageId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var assets = request.Batch.Assets.ToDictionary(asset => asset.AssetId, StringComparer.Ordinal);
        var pages = new List<AiRenderPagePreparation>();

        foreach (var style in request.Batch.Styles.OrderBy(style => style.Order))
        {
            var styleInput = styles[style.StyleId];
            var selections = styleInput.Selections.ToDictionary(
                selection => selection.FrameId,
                StringComparer.Ordinal);
            if (selections.Count != styleInput.Selections.Count)
            {
                throw new InvalidOperationException("AI_RENDER_SELECTION_DUPLICATE");
            }

            var styleAssetIds = style.AssetIds.ToHashSet(StringComparer.Ordinal);
            foreach (var snapshotPage in request.Batch.TemplateSnapshot.Pages.OrderBy(page => page.Order))
            {
                var page = revisionPages[snapshotPage.PageId];
                if (page.ContentType == TemplatePageContentType.DynamicSizeTable)
                {
                    pages.Add(new AiRenderPagePreparation(
                        style.StyleId,
                        page.PageId,
                        styleInput.SizeTableCandidate is null
                            ? AiRenderPagePreparationStatus.BusinessBlocked
                            : AiRenderPagePreparationStatus.AwaitingSizeTableRasterization,
                        styleInput.SizeTableCandidate is null
                            ? "AI_SIZE_TABLE_CANDIDATE_MISSING"
                            : "AI_SIZE_TABLE_RASTERIZER_PENDING"));
                    continue;
                }

                var bindings = bindingsByPage.GetValueOrDefault(page.PageId, []);
                if (bindings.Length == 0
                    || bindings.Any(binding => !selections.ContainsKey(binding.FrameId)))
                {
                    pages.Add(new AiRenderPagePreparation(
                        style.StyleId,
                        page.PageId,
                        AiRenderPagePreparationStatus.BusinessBlocked,
                        "AI_RENDER_REQUIRED_SELECTION_MISSING"));
                    continue;
                }

                var selected = bindings.Select(binding => selections[binding.FrameId]).ToArray();
                if (selected.Any(selection => !styleAssetIds.Contains(selection.AssetId)
                    || !assets.TryGetValue(selection.AssetId, out var asset)
                    || !string.Equals(asset.StyleId, style.StyleId, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("AI_RENDER_SELECTION_ASSET_MISMATCH");
                }

                if (selected.Any(selection => selection.SubjectGeometry is null))
                {
                    pages.Add(new AiRenderPagePreparation(
                        style.StyleId,
                        page.PageId,
                        AiRenderPagePreparationStatus.AwaitingSubjectAnalysis,
                        "AI_RENDER_SUBJECT_GEOMETRY_PENDING"));
                    continue;
                }

                var explicitSelections = selected.Select(selection =>
                {
                    var geometry = selection.SubjectGeometry!;
                    if (!string.Equals(geometry.AssetId, selection.AssetId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("AI_RENDER_GEOMETRY_ASSET_MISMATCH");
                    }

                    return new TemplateRenderSelection(
                        selection.FrameId,
                        selection.AssetId,
                        new TemplateRenderSubjectGeometry(
                            selection.AssetId,
                            geometry.Left,
                            geometry.Top,
                            geometry.Right,
                            geometry.Bottom,
                            geometry.Confidence));
                }).ToArray();

                if (!templateResources.TryGetValue(page.TemplateResourceId, out var templateResource)
                    || !string.Equals(templateResource.ResourceId, snapshotPage.TemplateResourceId, StringComparison.Ordinal)
                    || !string.Equals(
                        templateResource.StructureSignature,
                        templateResource.Inspection.StructureSignature,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("AI_RENDER_TEMPLATE_RESOURCE_MISMATCH");
                }

                var selectedAssets = selected.Select(selection => assets[selection.AssetId])
                    .DistinctBy(asset => asset.AssetId)
                    .OrderBy(asset => asset.AssetId, StringComparer.Ordinal)
                    .ToArray();
                var identity = CreatePlanIdentity(
                    request.Batch,
                    page,
                    style,
                    selected,
                    request.CreatedAt);
                var renderRequest = new TemplateRenderPlanRequest(
                    identity.PlanId,
                    identity.IdempotencyKey,
                    identity.CorrelationId,
                    request.Batch.BatchId,
                    style.StyleId,
                    identity.ArtifactSetId,
                    $"{style.Order + 1:D3}-{page.Order + 1:D2}.jpg",
                    request.Batch.IncludeEditablePsd,
                    request.Batch.IncludeEditablePsd ? $"{style.Order + 1:D3}-{page.Order + 1:D2}.psd" : null,
                    request.CreatedAt,
                    selectedAssets.Select(asset => new TemplateRenderSource(
                        asset.AssetId,
                        asset.Sha256,
                        asset.Length,
                        asset.MimeType)).ToArray(),
                    explicitSelections);
                var json = planBuilder.Build(
                    request.TemplateRevision,
                    page,
                    templateResource,
                    renderRequest);
                using var document = JsonDocument.Parse(json);
                pages.Add(new AiRenderPagePreparation(
                    style.StyleId,
                    page.PageId,
                    AiRenderPagePreparationStatus.ReadyToRender,
                    "AI_RENDER_PLAN_VALID",
                    json,
                    document.RootElement.GetProperty("plan_digest").GetString()));
            }
        }

        var readyPages = pages.Where(page => page.HasPlan).ToArray();
        if (readyPages.Length == 0)
        {
            return new AiRenderPlanPreparation(
                request.Batch.BatchId,
                AiRenderPreparationStatus.UserActionRequired,
                "AI_RENDER_NO_EXECUTABLE_PAGE",
                pages);
        }

        var availability = await photoshopAvailability.CheckAsync(cancellationToken).ConfigureAwait(false);
        return new AiRenderPlanPreparation(
            request.Batch.BatchId,
            availability.IsAvailable
                ? AiRenderPreparationStatus.ReadyToRender
                : AiRenderPreparationStatus.WaitingForPhotoshop,
            availability.IsAvailable
                ? "AI_RENDER_READY"
                : availability.ReasonCode ?? "PHOTOSHOP_UNAVAILABLE",
            pages);
    }

    private static void ValidateSnapshot(BatchDraft batch, TemplateRevision revision)
    {
        if (batch.ScanStatus != BatchScanStatus.ScanReady
            || revision.State != TemplateRevisionState.Committed
            || revision.RuleDigest is null
            || !string.Equals(batch.TemplateSnapshot.RevisionId, revision.RevisionId, StringComparison.Ordinal)
            || !string.Equals(batch.TemplateSnapshot.RulesDigest, revision.RuleDigest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_RENDER_SNAPSHOT_MISMATCH");
        }

        var revisionPages = revision.Pages.ToDictionary(page => page.PageId, StringComparer.Ordinal);
        var bindingsByPage = revision.FrameBindings
            .GroupBy(binding => binding.PageId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(binding => binding.FrameId)
                    .Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        foreach (var snapshot in batch.TemplateSnapshot.Pages)
        {
            if (!revisionPages.TryGetValue(snapshot.PageId, out var page)
                || page.Order != snapshot.Order
                || page.ContentType != snapshot.ContentType
                || page.GenerationMode != snapshot.GenerationMode
                || page.VariantCapacity != snapshot.VariantCapacity
                || !string.Equals(page.TemplateResourceId, snapshot.TemplateResourceId, StringComparison.Ordinal)
                || !snapshot.FrameIds.SequenceEqual(
                    bindingsByPage.GetValueOrDefault(snapshot.PageId, []),
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException("AI_RENDER_SNAPSHOT_PAGE_MISMATCH");
            }
        }
    }

    private static PlanIdentity CreatePlanIdentity(
        BatchDraft batch,
        TemplatePage page,
        StyleDraft style,
        IReadOnlyList<AiConfirmedRenderSelection> selections,
        DateTimeOffset createdAt)
    {
        var preimage = JsonSerializer.Serialize(new
        {
            batch_id = batch.BatchId,
            snapshot_id = batch.TemplateSnapshot.SnapshotId,
            rules_digest = batch.TemplateSnapshot.RulesDigest,
            style_id = style.StyleId,
            page_id = page.PageId,
            selections = selections.OrderBy(selection => selection.FrameId, StringComparer.Ordinal)
                .Select(selection => new
                {
                    selection.FrameId,
                    selection.AssetId,
                    selection.SelectionRevision,
                    selection.SubjectGeometry!.Left,
                    selection.SubjectGeometry.Top,
                    selection.SubjectGeometry.Right,
                    selection.SubjectGeometry.Bottom,
                    selection.SubjectGeometry.Confidence
                }),
            created_at = createdAt
        });
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preimage)))
            .ToLowerInvariant();
        return new PlanIdentity(
            $"plan_{digest[..24]}",
            $"render/{batch.BatchId}/{style.StyleId}/{page.PageId}/{digest[..16]}",
            $"corr_{digest[24..48]}",
            $"artifact_{digest[40..64]}");
    }

    private sealed record PlanIdentity(
        string PlanId,
        string IdempotencyKey,
        string CorrelationId,
        string ArtifactSetId);
}
