using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
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
    AiSizeTableCandidateV1? SizeTableCandidate = null,
    IReadOnlyList<AiPreparedSizeTablePage>? PreparedSizeTablePages = null,
    string? SizeTableReasonCode = null);

public sealed record AiPreparedSizeTablePage(
    string PageId,
    Desktop.Domain.SizeTables.SizeTable Table,
    SizeTableRasterizedResource RasterizedResource);

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
    IPhotoshopAvailabilityPort photoshopAvailability,
    ISizeTableRenderPlanBuilder? sizeTablePlanBuilder = null) : IAiRenderPlanFactory
{
    public async Task<AiRenderPlanPreparation> PrepareAsync(
        AiRenderPlanPreparationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSnapshot(request.Batch, request.TemplateRevision);

        if (request.Styles.Any(style => style is null)
            || request.Styles.Select(style => style.StyleId)
                .Distinct(StringComparer.Ordinal).Count() != request.Styles.Count)
        {
            throw new InvalidOperationException("AI_RENDER_STYLE_INPUT_MISMATCH");
        }

        var styles = request.Styles.ToDictionary(style => style.StyleId, StringComparer.Ordinal);
        if (request.Batch.Styles.Any(style => !styles.ContainsKey(style.StyleId))
            || styles.Keys.Any(styleId => request.Batch.Styles.All(style =>
                !string.Equals(style.StyleId, styleId, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("AI_RENDER_STYLE_INPUT_MISMATCH");
        }

        if (request.TemplateResources.Any(resource => resource is null)
            || request.TemplateResources.Select(resource => resource.ResourceId)
                .Distinct(StringComparer.Ordinal).Count() != request.TemplateResources.Count)
        {
            throw new InvalidOperationException("AI_RENDER_TEMPLATE_RESOURCE_DUPLICATE");
        }

        var templateResources = request.TemplateResources.ToDictionary(
            resource => resource.ResourceId,
            StringComparer.Ordinal);

        var expectedTemplateResourceIds = request.Batch.TemplateSnapshot.Pages
            .Select(page => page.TemplateResourceId)
            .ToHashSet(StringComparer.Ordinal);
        if (templateResources.Keys.Any(resourceId => !expectedTemplateResourceIds.Contains(resourceId))
            || expectedTemplateResourceIds.Any(resourceId => !templateResources.ContainsKey(resourceId)))
        {
            throw new InvalidOperationException("AI_RENDER_TEMPLATE_RESOURCE_SET_MISMATCH");
        }

        var revisionPages = request.TemplateRevision.Pages.ToDictionary(
            page => page.PageId,
            StringComparer.Ordinal);
        var bindingsByPage = request.TemplateRevision.FrameBindings
            .GroupBy(binding => binding.PageId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (request.Batch.Assets.Select(asset => asset.AssetId)
            .Distinct(StringComparer.Ordinal).Count() != request.Batch.Assets.Length)
        {
            throw new InvalidOperationException("AI_RENDER_ASSET_DUPLICATE");
        }

        var assets = request.Batch.Assets.ToDictionary(asset => asset.AssetId, StringComparer.Ordinal);
        var pages = new List<AiRenderPagePreparation>();
        var selectedSnapshotFrameIds = request.Batch.TemplateSnapshot.Pages
            .SelectMany(page => page.FrameIds)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var style in request.Batch.Styles.OrderBy(style => style.Order))
        {
            var styleInput = styles[style.StyleId];
            var styleAssetIds = style.AssetIds.ToHashSet(StringComparer.Ordinal);
            foreach (var assetId in style.AssetIds)
            {
                if (!assets.TryGetValue(assetId, out var asset)
                    || !string.Equals(asset.StyleId, style.StyleId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("AI_RENDER_ASSET_STYLE_MISMATCH");
                }

                ValidateSourceAsset(asset);
            }

            if (styleInput.SizeTableCandidate is not null)
            {
                ValidateSizeTableCandidate(styleInput.SizeTableCandidate, style, styleAssetIds, assets);
            }

            var preparedSizeTables = styleInput.PreparedSizeTablePages ?? [];
            if (preparedSizeTables.Any(prepared => prepared is null)
                || preparedSizeTables.Select(prepared => prepared.PageId)
                    .Distinct(StringComparer.Ordinal).Count() != preparedSizeTables.Count)
            {
                throw new InvalidOperationException("AI_RENDER_SIZE_TABLE_PREPARATION_DUPLICATE");
            }
            var preparedSizeTablesByPage = preparedSizeTables.ToDictionary(
                prepared => prepared.PageId,
                StringComparer.Ordinal);

            if (styleInput.Selections.Any(selection => selection is null)
                || styleInput.Selections.Select(selection => selection.FrameId)
                    .Distinct(StringComparer.Ordinal).Count() != styleInput.Selections.Count)
            {
                throw new InvalidOperationException("AI_RENDER_SELECTION_DUPLICATE");
            }

            var selections = styleInput.Selections.ToDictionary(
                selection => selection.FrameId,
                StringComparer.Ordinal);
            if (selections.Keys.Any(frameId => !selectedSnapshotFrameIds.Contains(frameId)))
            {
                throw new InvalidOperationException("AI_RENDER_SELECTION_FRAME_MISMATCH");
            }

            foreach (var snapshotPage in request.Batch.TemplateSnapshot.Pages.OrderBy(page => page.Order))
            {
                if (!revisionPages.TryGetValue(snapshotPage.PageId, out var page))
                {
                    throw new InvalidOperationException("AI_RENDER_SNAPSHOT_PAGE_MISMATCH");
                }

                if (!templateResources.TryGetValue(page.TemplateResourceId, out var templateResource))
                {
                    throw new InvalidOperationException("AI_RENDER_TEMPLATE_RESOURCE_NOT_FOUND");
                }

                ValidateTemplateResourceOwnership(
                    templateResource,
                    request.TemplateRevision,
                    page,
                    snapshotPage);

                if (page.ContentType == TemplatePageContentType.DynamicSizeTable)
                {
                    if (preparedSizeTablesByPage.TryGetValue(page.PageId, out var prepared))
                    {
                        ValidatePreparedSizeTable(
                            prepared,
                            request.Batch,
                            style,
                            page,
                            styleAssetIds,
                            assets);
                        if (sizeTablePlanBuilder is null)
                        {
                            throw new InvalidOperationException("AI_SIZE_TABLE_RENDER_PLAN_BUILDER_MISSING");
                        }

                        var sizeIdentity = CreateSizeTablePlanIdentity(
                            request.Batch,
                            page,
                            style,
                            prepared,
                            request.CreatedAt);
                        var sizeJson = sizeTablePlanBuilder.Build(
                            request.TemplateRevision,
                            page,
                            templateResource,
                            new SizeTableRenderPlanRequest(
                                sizeIdentity.PlanId,
                                sizeIdentity.IdempotencyKey,
                                sizeIdentity.CorrelationId,
                                request.Batch.BatchId,
                                style.StyleId,
                                sizeIdentity.ArtifactSetId,
                                $"{style.Order + 1:D3}-{page.Order + 1:D2}.jpg",
                                request.Batch.IncludeEditablePsd,
                                request.Batch.IncludeEditablePsd
                                    ? $"{style.Order + 1:D3}-{page.Order + 1:D2}.psd"
                                    : null,
                                request.CreatedAt,
                                prepared.Table,
                                prepared.RasterizedResource));
                        using var sizeDocument = JsonDocument.Parse(sizeJson);
                        pages.Add(new AiRenderPagePreparation(
                            style.StyleId,
                            page.PageId,
                            AiRenderPagePreparationStatus.ReadyToRender,
                            "AI_RENDER_PLAN_VALID",
                            sizeJson,
                            sizeDocument.RootElement.GetProperty("plan_digest").GetString()));
                        continue;
                    }

                    pages.Add(new AiRenderPagePreparation(
                        style.StyleId,
                        page.PageId,
                        SizeTablePageStatus(styleInput),
                        SizeTablePageReason(styleInput)));
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

                var usesLayoutFallback = selected.Any(selection => selection.SubjectGeometry is null);
                var explicitSelections = selected.Select(selection =>
                {
                    var geometry = selection.SubjectGeometry;
                    if (geometry is not null
                        && !string.Equals(geometry.AssetId, selection.AssetId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("AI_RENDER_GEOMETRY_ASSET_MISMATCH");
                    }

                    return new TemplateRenderSelection(
                        selection.FrameId,
                        selection.AssetId,
                        geometry is null
                            ? null
                            : new TemplateRenderSubjectGeometry(
                                selection.AssetId,
                                geometry.Left,
                                geometry.Top,
                                geometry.Right,
                                geometry.Bottom,
                                geometry.Confidence));
                }).ToArray();

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
                    usesLayoutFallback
                        ? "AI_RENDER_PLAN_VALID_LAYOUT_FALLBACK"
                        : "AI_RENDER_PLAN_VALID",
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

    private static void ValidateTemplateResourceOwnership(
        ImportedTemplateResource resource,
        TemplateRevision revision,
        TemplatePage page,
        BatchTemplatePageSnapshot snapshotPage)
    {
        if (!string.Equals(resource.ResourceId, page.TemplateResourceId, StringComparison.Ordinal)
            || !string.Equals(resource.ResourceId, snapshotPage.TemplateResourceId, StringComparison.Ordinal)
            || !string.Equals(resource.GroupId, revision.GroupId, StringComparison.Ordinal)
            || !string.Equals(resource.DraftId, revision.RevisionId, StringComparison.Ordinal)
            || resource.Inspection is null
            || resource.Length <= 0
            || resource.Inspection.SizeBytes != resource.Length
            || !string.Equals(resource.Inspection.FileSha256, resource.Sha256, StringComparison.OrdinalIgnoreCase)
            || !IsSha256(resource.Sha256)
            || !IsDigest(resource.StructureSignature)
            || !string.Equals(
                resource.StructureSignature,
                resource.Inspection.StructureSignature,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("AI_RENDER_TEMPLATE_RESOURCE_OWNERSHIP_MISMATCH");
        }
    }

    private static void ValidateSourceAsset(SourceAssetMetadata asset)
    {
        var expectedMime = asset.Format switch
        {
            SourceImageFormat.Jpeg => "image/jpeg",
            SourceImageFormat.Png => "image/png",
            SourceImageFormat.Tiff => "image/tiff",
            _ => null
        };
        if (asset.Readability != AssetReadability.Readable
            || asset.Length <= 0
            || asset.PixelWidth <= 0
            || asset.PixelHeight <= 0
            || !IsSha256(asset.Sha256)
            || expectedMime is null
            || !string.Equals(asset.MimeType, expectedMime, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("AI_RENDER_ASSET_METADATA_MISMATCH");
        }
    }

    private static void ValidateSizeTableCandidate(
        AiSizeTableCandidateV1 candidate,
        StyleDraft style,
        HashSet<string> styleAssetIds,
        Dictionary<string, SourceAssetMetadata> assets)
    {
        if (!styleAssetIds.Contains(candidate.SourceAssetId)
            || !assets.TryGetValue(candidate.SourceAssetId, out var source)
            || !string.Equals(source.StyleId, style.StyleId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_RENDER_SIZE_TABLE_SOURCE_MISMATCH");
        }

        if (candidate.Columns is null
            || candidate.Rows is null
            || candidate.Columns.Count == 0
            || candidate.Rows.Count == 0
            || candidate.Columns.Any(string.IsNullOrWhiteSpace)
            || !candidate.Rows.All(row => row is not null
                && row.Count == candidate.Columns.Count))
        {
            throw new InvalidOperationException("AI_RENDER_SIZE_TABLE_CANDIDATE_INVALID");
        }
    }

    private static AiRenderPagePreparationStatus SizeTablePageStatus(AiRenderStyleInput input) =>
        input.SizeTableReasonCode switch
        {
            "SIZE_TABLE_REVIEW_REQUIRED" or "SIZE_TABLE_INVALID" or "SIZE_TABLE_SOURCE_CONFLICT"
                or "SIZE_TABLE_STATE_CORRUPT" or "SIZE_TABLE_SOURCE_CHANGE_REVIEW_REQUIRED" =>
                AiRenderPagePreparationStatus.BusinessBlocked,
            _ when input.SizeTableCandidate is null => AiRenderPagePreparationStatus.BusinessBlocked,
            _ => AiRenderPagePreparationStatus.AwaitingSizeTableRasterization
        };

    private static string SizeTablePageReason(AiRenderStyleInput input) =>
        input.SizeTableReasonCode switch
        {
            "SIZE_TABLE_REVIEW_REQUIRED" => "SIZE_TABLE_REVIEW_REQUIRED",
            "SIZE_TABLE_INVALID" => "SIZE_TABLE_INVALID",
            "SIZE_TABLE_SOURCE_CONFLICT" => "SIZE_TABLE_SOURCE_CONFLICT",
            "SIZE_TABLE_STATE_CORRUPT" => "SIZE_TABLE_STATE_CORRUPT",
            "SIZE_TABLE_SOURCE_CHANGE_REVIEW_REQUIRED" => "SIZE_TABLE_SOURCE_CHANGE_REVIEW_REQUIRED",
            _ when input.SizeTableCandidate is null => "AI_SIZE_TABLE_CANDIDATE_MISSING",
            _ => "AI_SIZE_TABLE_RASTERIZER_PENDING"
        };

    private static void ValidatePreparedSizeTable(
        AiPreparedSizeTablePage prepared,
        BatchDraft batch,
        StyleDraft style,
        TemplatePage page,
        HashSet<string> styleAssetIds,
        Dictionary<string, SourceAssetMetadata> assets)
    {
        var table = prepared.Table;
        if (!string.Equals(prepared.PageId, page.PageId, StringComparison.Ordinal)
            || !string.Equals(table.BatchId, batch.BatchId, StringComparison.Ordinal)
            || !string.Equals(table.StyleId, style.StyleId, StringComparison.Ordinal)
            || !string.Equals(table.PageId, page.PageId, StringComparison.Ordinal)
            || !table.CanSave
            || !styleAssetIds.Contains(table.Source.AssetId)
            || !assets.TryGetValue(table.Source.AssetId, out var source)
            || !string.Equals(source.StyleId, style.StyleId, StringComparison.Ordinal)
            || !string.Equals(
                "sha256:" + source.Sha256.ToLowerInvariant(),
                table.Source.ContentDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                prepared.RasterizedResource.TableDataDigest,
                table.DataDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI_RENDER_SIZE_TABLE_PREPARATION_MISMATCH");
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(Uri.IsHexDigit);

    private static bool IsDigest(string? value) => value is { Length: 71 }
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && IsSha256(value[7..]);

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
                    geometry = selection.SubjectGeometry is null
                        ? null
                        : new
                        {
                            selection.SubjectGeometry.Left,
                            selection.SubjectGeometry.Top,
                            selection.SubjectGeometry.Right,
                            selection.SubjectGeometry.Bottom,
                            selection.SubjectGeometry.Confidence
                        }
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

    private static PlanIdentity CreateSizeTablePlanIdentity(
        BatchDraft batch,
        TemplatePage page,
        StyleDraft style,
        AiPreparedSizeTablePage prepared,
        DateTimeOffset createdAt)
    {
        var preimage = JsonSerializer.Serialize(new
        {
            batch_id = batch.BatchId,
            snapshot_id = batch.TemplateSnapshot.SnapshotId,
            rules_digest = batch.TemplateSnapshot.RulesDigest,
            style_id = style.StyleId,
            page_id = page.PageId,
            size_table_id = prepared.Table.TableId,
            size_table_revision = prepared.Table.Revision,
            structured_data_digest = prepared.Table.DataDigest,
            raster_resource_id = prepared.RasterizedResource.ResourceId,
            raster_sha256 = prepared.RasterizedResource.Sha256,
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
