using System.Globalization;
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
    long SelectionRevision,
    string? VariantId = null,
    string? SelectionRequirement = null,
    string? SelectionConflictCode = null);

public sealed record AiRenderStyleInput(
    string StyleId,
    IReadOnlyList<AiConfirmedRenderSelection> Selections,
    AiSizeTableCandidateV1? SizeTableCandidate = null,
    IReadOnlyList<AiPreparedSizeTablePage>? PreparedSizeTablePages = null,
    string? SizeTableReasonCode = null,
    IReadOnlyList<AiOutputVariantPlan>? VariantPlans = null,
    IReadOnlyDictionary<string, AiSubjectGeometryV1>? SubjectGeometryByAsset = null);

public sealed record AiPreparedSizeTablePage(
    string PageId,
    Desktop.Domain.SizeTables.SizeTable Table,
    SizeTableRasterizedResource RasterizedResource,
    SizeTableRenderTarget? CalibratedTarget = null,
    SizeTableInsertionTarget? CalibratedInsertion = null);

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
    string? PlanDigest = null,
    string? OutputVariantId = null,
    string? OutputVariantName = null, RenderInstanceIdentity? Instance = null)
{
    public bool HasPlan => RenderPlanJson is not null && PlanDigest is not null;
}

public sealed record AiRenderPlanPreparation(
    string BatchId,
    AiRenderPreparationStatus Status,
    string ReasonCode,
    IReadOnlyList<AiRenderPagePreparation> Pages,
    bool HasFormalOutput = false,
    bool UsesInstancePipeline = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RenderBatchInstanceSet>? InstanceSets = null)
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
    ISizeTableRenderPlanBuilder? sizeTablePlanBuilder = null,
    ITemplateRepository? templates = null) : IAiRenderPlanFactory
{
    public async Task<AiRenderPlanPreparation> PrepareAsync(
        AiRenderPlanPreparationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.TemplateRevision.RuleWorkspace?.Rules is not null || request.Batch.TemplateSnapshot is BatchRuleTemplateContext)
            throw new InvalidOperationException("TPL_RULE_INSTANCE_PIPELINE_REQUIRED");
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
        var bindingsByPage = request.TemplateRevision.ActiveFrameBindings
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

            var geometryByAsset = styleInput.SubjectGeometryByAsset;
            if (geometryByAsset?.Any(item => item.Value is null || !styleAssetIds.Contains(item.Key)
                || !string.Equals(item.Key, item.Value.AssetId, StringComparison.Ordinal)) == true)
                throw new InvalidOperationException("AI_RENDER_GEOMETRY_ASSET_MISMATCH");

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

            var baseSelections = styleInput.Selections.ToDictionary(
                selection => selection.FrameId,
                StringComparer.Ordinal);
            if (baseSelections.Keys.Any(frameId => !selectedSnapshotFrameIds.Contains(frameId)))
            {
                throw new InvalidOperationException("AI_RENDER_SELECTION_FRAME_MISMATCH");
            }

            var outputVariants = styleInput.VariantPlans is { Count: > 0 }
                ? styleInput.VariantPlans.Cast<AiOutputVariantPlan?>().ToArray()
                : new AiOutputVariantPlan?[] { null };
            var outputNames = CreateOutputBaseNames(request.Batch.TemplateSnapshot.Pages
                .Select(snapshotPage => revisionPages[snapshotPage.PageId]), outputVariants);
            foreach (var outputVariant in outputVariants)
            {
                var variantSelections = outputVariant is null
                    ? baseSelections
                    : styleInput.Selections
                        .Where(selection => outputVariant.RequirementAssets.ContainsKey(selection.FrameId))
                        .Select(selection =>
                        {
                            var assetId = outputVariant.RequirementAssets[selection.FrameId];
                            return selection with
                            {
                                AssetId = assetId,
                                SubjectGeometry = geometryByAsset?.GetValueOrDefault(assetId)
                                    ?? (string.Equals(selection.AssetId, assetId, StringComparison.Ordinal)
                                        ? selection.SubjectGeometry : null),
                                VariantId = outputVariant.VariantId
                            };
                        })
                        .ToDictionary(selection => selection.FrameId, StringComparer.Ordinal);

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

                await ValidateTemplateResourceOwnershipAsync(
                    templateResource,
                    request.TemplateRevision,
                    page,
                    snapshotPage,
                    cancellationToken).ConfigureAwait(false);

                if (page.GenerationMode == TemplateGenerationMode.OncePerProduct
                    && !ReferenceEquals(outputVariant, outputVariants[0])) continue;

                var pageVariant = outputVariant is not null
                    && page.GenerationMode != TemplateGenerationMode.OncePerProduct
                    ? outputVariant
                    : null;
                var outputBaseName = outputNames[(page.PageId, pageVariant?.VariantId)];
                var selections = pageVariant is null ? baseSelections : variantSelections;
                var selectionConflict = snapshotPage.FrameIds
                    .Select(frameId => selections.GetValueOrDefault(frameId)?.SelectionConflictCode)
                    .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
                if (page.ContentType != TemplatePageContentType.DynamicSizeTable
                    && (pageVariant?.ConflictCode is not null || selectionConflict is not null))
                {
                    pages.Add(new AiRenderPagePreparation(
                        style.StyleId,
                        snapshotPage.PageId,
                        AiRenderPagePreparationStatus.BusinessBlocked,
                        pageVariant?.ConflictCode ?? selectionConflict!,
                        OutputVariantId: pageVariant?.VariantId,
                        OutputVariantName: pageVariant?.VariantName));
                    continue;
                }

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

                        var sizeIdentity = pageVariant is null
                            ? CreateSizeTablePlanIdentity(
                                request.Batch,
                                page,
                                style,
                                prepared,
                                request.CreatedAt, outputBaseName)
                            : CreateSizeTablePlanIdentity(
                            request.Batch,
                            page,
                            style,
                            prepared,
                            request.CreatedAt,
                            pageVariant.VariantId, outputBaseName);
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
                                outputBaseName + ".jpg",
                                request.Batch.IncludeEditablePsd,
                                request.Batch.IncludeEditablePsd
                                    ? outputBaseName + ".psd"
                                    : null,
                                request.CreatedAt,
                                prepared.Table,
                                prepared.RasterizedResource,
                                prepared.CalibratedTarget,
                                prepared.CalibratedInsertion, pageVariant?.VariantId));
                        using var sizeDocument = JsonDocument.Parse(sizeJson);
                        pages.Add(new AiRenderPagePreparation(
                            style.StyleId,
                            page.PageId,
                            prepared.CalibratedTarget is null && prepared.CalibratedInsertion is null
                                ? AiRenderPagePreparationStatus.BusinessBlocked : AiRenderPagePreparationStatus.ReadyToRender,
                            prepared.CalibratedTarget is null && prepared.CalibratedInsertion is null
                                ? "SIZE_TABLE_RENDER_TARGET_REQUIRED" : "AI_RENDER_PLAN_VALID",
                            sizeJson,
                            sizeDocument.RootElement.GetProperty("plan_digest").GetString(),
                            pageVariant?.VariantId,
                            pageVariant?.VariantName));
                        continue;
                    }

                    pages.Add(new AiRenderPagePreparation(
                        style.StyleId,
                        page.PageId,
                        SizeTablePageStatus(styleInput),
                        SizeTablePageReason(styleInput),
                        OutputVariantId: pageVariant?.VariantId,
                        OutputVariantName: pageVariant?.VariantName));
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
                        "AI_RENDER_REQUIRED_SELECTION_MISSING",
                        OutputVariantId: pageVariant?.VariantId,
                        OutputVariantName: pageVariant?.VariantName));
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
                var identity = pageVariant is null
                    ? CreatePlanIdentity(
                        request.Batch,
                        page,
                        style,
                        selected,
                        selectedAssets,
                        request.CreatedAt, outputBaseName)
                    : CreatePlanIdentity(
                        request.Batch,
                        page,
                        style,
                        selected,
                        selectedAssets,
                        request.CreatedAt,
                        pageVariant!.VariantId, outputBaseName);
                var renderRequest = new TemplateRenderPlanRequest(
                    identity.PlanId,
                    identity.IdempotencyKey,
                    identity.CorrelationId,
                    request.Batch.BatchId,
                    style.StyleId,
                    identity.ArtifactSetId,
                    outputBaseName + ".jpg",
                    request.Batch.IncludeEditablePsd,
                    request.Batch.IncludeEditablePsd ? outputBaseName + ".psd" : null,
                    request.CreatedAt,
                    selectedAssets.Select(asset => new TemplateRenderSource(
                        asset.AssetId,
                        asset.Sha256,
                        asset.Length,
                        asset.MimeType, asset.PixelWidth, asset.PixelHeight)).ToArray(),
                    explicitSelections, RequireAbsolutePlacement: true, OutputVariantId: pageVariant?.VariantId);
                string json;
                try { json = planBuilder.Build(
                    request.TemplateRevision,
                    page,
                    templateResource,
                    renderRequest); }
                catch (TemplateRuleViolationException error) when (error.Code == "TPL_RENDER_LAYOUT_REVIEW_REQUIRED")
                {
                    pages.Add(new(style.StyleId, page.PageId, AiRenderPagePreparationStatus.BusinessBlocked,
                        error.Code, OutputVariantId: pageVariant?.VariantId, OutputVariantName: pageVariant?.VariantName));
                    continue;
                }
                using var document = JsonDocument.Parse(json);
                pages.Add(new AiRenderPagePreparation(
                    style.StyleId,
                    page.PageId,
                    AiRenderPagePreparationStatus.ReadyToRender,
                    usesLayoutFallback
                        ? "AI_RENDER_PLAN_VALID_LAYOUT_FALLBACK"
                        : "AI_RENDER_PLAN_VALID",
                    json,
                    document.RootElement.GetProperty("plan_digest").GetString(),
                    pageVariant?.VariantId,
                    pageVariant?.VariantName));
                }
            }
        }

        var readyPages = pages.Where(page => page.HasPlan && page.Status == AiRenderPagePreparationStatus.ReadyToRender).ToArray();
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
        var bindingsByPage = revision.ActiveFrameBindings
            .Where(binding => revisionPages[binding.PageId].ContentType == TemplatePageContentType.NormalComposition)
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

    private async Task ValidateTemplateResourceOwnershipAsync(
        ImportedTemplateResource resource,
        TemplateRevision revision,
        TemplatePage page,
        BatchTemplatePageSnapshot snapshotPage,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(resource.ResourceId, page.TemplateResourceId, StringComparison.Ordinal)
            || !string.Equals(resource.ResourceId, snapshotPage.TemplateResourceId, StringComparison.Ordinal)
            || !string.Equals(resource.GroupId, revision.GroupId, StringComparison.Ordinal)
            || resource.Inspection is null
            || resource.Length <= 0
            || resource.Inspection.SizeBytes != resource.Length
            || !string.Equals(resource.Inspection.FileSha256, resource.Sha256, StringComparison.OrdinalIgnoreCase)
            || !IsSha256(resource.Sha256)
            || !SameStructureDigest(resource.StructureSignature, resource.Inspection.StructureSignature))
        {
            throw new InvalidOperationException("AI_RENDER_TEMPLATE_RESOURCE_OWNERSHIP_MISMATCH");
        }

        // Incremental template revisions retain immutable PSD resources imported by
        // an earlier revision. Verify that origin instead of rewriting its identity.
        if (!string.Equals(resource.DraftId, revision.RevisionId, StringComparison.Ordinal))
        {
            var origin = templates is null ? null
                : await templates.GetRevisionAsync(resource.DraftId, cancellationToken).ConfigureAwait(false);
            if (origin is not { State: TemplateRevisionState.Committed }
                || origin.RevisionId != resource.DraftId || origin.GroupId != revision.GroupId
                || origin.RevisionNo >= revision.RevisionNo
                || !origin.Pages.Any(candidate => candidate.TemplateResourceId == resource.ResourceId))
                throw new InvalidOperationException("AI_RENDER_TEMPLATE_RESOURCE_OWNERSHIP_MISMATCH");
        }
    }

    private static bool SameStructureDigest(string left, string right)
    {
        static string Bare(string value) => value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;
        var leftBare = Bare(left); var rightBare = Bare(right);
        return IsSha256(leftBare) && IsSha256(rightBare)
            && string.Equals(leftBare, rightBare, StringComparison.OrdinalIgnoreCase);
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
                or "SIZE_TABLE_STATE_CORRUPT" or "SIZE_TABLE_SOURCE_CHANGE_REVIEW_REQUIRED" or "SIZE_TABLE_CALIBRATION_INVALID" =>
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
            "SIZE_TABLE_CALIBRATION_INVALID" => "SIZE_TABLE_CALIBRATION_INVALID",
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
        IReadOnlyList<SourceAssetMetadata> sources,
        DateTimeOffset createdAt,
        string outputBaseName)
    {
        var preimage = JsonSerializer.Serialize(new
        {
            output_base_name = outputBaseName,
            placement_version = TemplatePlacementResolver.Version,
            batch.IncludeEditablePsd,
            sources = sources.Select(source => new { source.AssetId, source.Sha256, source.Length,
                source.MimeType, source.PixelWidth, source.PixelHeight }),
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

    private static PlanIdentity CreatePlanIdentity(
        BatchDraft batch,
        TemplatePage page,
        StyleDraft style,
        IReadOnlyList<AiConfirmedRenderSelection> selections,
        IReadOnlyList<SourceAssetMetadata> sources,
        DateTimeOffset createdAt,
        string variantId,
        string outputBaseName)
    {
        var preimage = JsonSerializer.Serialize(new
        {
            output_base_name = outputBaseName,
            placement_version = TemplatePlacementResolver.Version,
            batch.IncludeEditablePsd,
            sources = sources.Select(source => new { source.AssetId, source.Sha256, source.Length,
                source.MimeType, source.PixelWidth, source.PixelHeight }),
            batch_id = batch.BatchId,
            snapshot_id = batch.TemplateSnapshot.SnapshotId,
            rules_digest = batch.TemplateSnapshot.RulesDigest,
            style_id = style.StyleId,
            page_id = page.PageId,
            variant_id = variantId,
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
            $"render/{batch.BatchId}/{style.StyleId}/{page.PageId}/{variantId}/{digest[..16]}",
            $"corr_{digest[24..48]}",
            $"artifact_{digest[40..64]}");
    }

    private static PlanIdentity CreateSizeTablePlanIdentity(
        BatchDraft batch,
        TemplatePage page,
        StyleDraft style,
        AiPreparedSizeTablePage prepared,
        DateTimeOffset createdAt,
        string outputBaseName)
    {
        var preimage = JsonSerializer.Serialize(new
        {
            output_base_name = outputBaseName,
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

    private static PlanIdentity CreateSizeTablePlanIdentity(
        BatchDraft batch,
        TemplatePage page,
        StyleDraft style,
        AiPreparedSizeTablePage prepared,
        DateTimeOffset createdAt,
        string variantId,
        string outputBaseName)
    {
        var preimage = JsonSerializer.Serialize(new
        {
            output_base_name = outputBaseName,
            batch_id = batch.BatchId,
            snapshot_id = batch.TemplateSnapshot.SnapshotId,
            rules_digest = batch.TemplateSnapshot.RulesDigest,
            style_id = style.StyleId,
            page_id = page.PageId,
            variant_id = variantId,
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
            $"render/{batch.BatchId}/{style.StyleId}/{page.PageId}/{variantId}/{digest[..16]}",
            $"corr_{digest[24..48]}",
            $"artifact_{digest[40..64]}");
    }

    private static Dictionary<(string PageId, string? VariantId), string> CreateOutputBaseNames(
        IEnumerable<TemplatePage> pages,
        IReadOnlyList<AiOutputVariantPlan?> variants)
    {
        var proposed = new Dictionary<(string PageId, string? VariantId), string>();
        foreach (var page in pages.OrderBy(page => page.Order).ThenBy(page => page.PageId, StringComparer.Ordinal))
        {
            // A numeric prefix also keeps Windows device names such as CON out of the leaf-name position.
            var prefix = ((long)page.Order + 1).ToString("D2", CultureInfo.InvariantCulture)
                + "-" + OutputNamePart(page.Name, 80, "页面");
            var pageVariants = page.GenerationMode == TemplateGenerationMode.OncePerProduct
                ? new AiOutputVariantPlan?[] { null } : variants;
            foreach (var variant in pageVariants)
            {
                var name = prefix + (variant is null ? string.Empty : "-" + OutputNamePart(variant.VariantName, 48, "变体"));
                proposed.Add((page.PageId, variant?.VariantId), name);
            }
        }

        // Reserve literal names first, including blocked outputs. Suffixes cannot steal a sibling's
        // name; the result depends on frozen identities, not enumeration order or render readiness.
        var reserved = new HashSet<string>(proposed.Values, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<(string PageId, string? VariantId), string>();
        foreach (var group in proposed.GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            if (group.Count() == 1)
            {
                var item = group.Single();
                result.Add(item.Key, item.Value);
                continue;
            }
            long suffix = 1;
            foreach (var item in group.OrderBy(item => item.Key.PageId, StringComparer.Ordinal)
                .ThenBy(item => item.Key.VariantId, StringComparer.Ordinal))
            {
                string name;
                do { name = item.Value + "-" + (suffix++).ToString("D2", CultureInfo.InvariantCulture); }
                while (!reserved.Add(name));
                result.Add(item.Key, name);
            }
        }
        return result;
    }

    internal static string OutputNamePart(string value, int maxLength, string fallback)
    {
        var cleaned = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator || rune.Value == Rune.ReplacementChar.Value
                || (rune.IsAscii && "\\/:*?\"<>|".Contains((char)rune.Value)))
                cleaned.Append('_');
            else cleaned.Append(rune.ToString());
        }
        var normalized = cleaned.ToString().Normalize(NormalizationForm.FormC).Trim(' ', '.', '_');
        var elements = StringInfo.GetTextElementEnumerator(normalized);
        var bounded = new StringBuilder();
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (bounded.Length + element.Length > maxLength) break;
            bounded.Append(element);
        }
        var result = bounded.ToString().TrimEnd(' ', '.', '_');
        return result.Length == 0 ? fallback : result;
    }

    private sealed record PlanIdentity(
        string PlanId,
        string IdempotencyKey,
        string CorrelationId,
        string ArtifactSetId);
}
