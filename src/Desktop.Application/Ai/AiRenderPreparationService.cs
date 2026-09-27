using System.Text.Json;
using Desktop.Application.Inputs;
using Desktop.Application.SizeTables;
using Desktop.Application.Templates;
using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.SizeTables;
using Desktop.Domain.Templates;

namespace Desktop.Application.Ai;

/// <summary>Prepares confirmed selections and size resources for the renderer-neutral plan factory.</summary>
public sealed class AiRenderPreparationService
{
    private readonly IAiAnalysisResultReader _resultReader;
    private readonly ITemplateResourceRepository _templateResources;
    private readonly IAiRenderPlanFactory _renderPlans;
    private readonly ISizeTableRepository? _sizeTables;
    private readonly ISizeTableRasterizer? _sizeTableRasterizer;

    public AiRenderPreparationService(
        IAiAnalysisResultReader resultReader,
        ITemplateResourceRepository templateResources,
        IAiRenderPlanFactory renderPlans,
        ISizeTableRepository? sizeTables = null,
        ISizeTableRasterizer? sizeTableRasterizer = null)
    {
        _resultReader = resultReader ?? throw new ArgumentNullException(nameof(resultReader));
        _templateResources = templateResources ?? throw new ArgumentNullException(nameof(templateResources));
        _renderPlans = renderPlans ?? throw new ArgumentNullException(nameof(renderPlans));
        _sizeTables = sizeTables;
        _sizeTableRasterizer = sizeTableRasterizer;
    }

    public async Task<AiRenderPlanPreparation> PrepareAsync(
        AiRenderPreparationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Projection.Status is AiBatchUiStatus.RecoveryRequired
            or AiBatchUiStatus.Failed
            or AiBatchUiStatus.Cancelled
            or AiBatchUiStatus.BudgetStopped
            or AiBatchUiStatus.Analysing
            or AiBatchUiStatus.Waiting)
        {
            throw new InvalidOperationException("AI_RENDER_ANALYSIS_NOT_CLOSED");
        }

        var resources = new List<ImportedTemplateResource>();
        foreach (var resourceId in snapshot.Batch.TemplateSnapshot.Pages
                     .Select(page => page.TemplateResourceId)
                     .Distinct(StringComparer.Ordinal))
        {
            var resource = await _templateResources.GetByResourceIdAsync(
                resourceId,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AI_RENDER_TEMPLATE_RESOURCE_NOT_FOUND");
            resources.Add(resource);
        }

        var styleInputs = new List<AiRenderStyleInput>(snapshot.Batch.Styles.Length);
        var planCreatedAt = snapshot.Batch.UpdatedAt;
        foreach (var style in snapshot.Batch.Styles.OrderBy(style => style.Order))
        {
            var firstLayer = snapshot.Requests.FirstOrDefault(request =>
                string.Equals(request.StyleId, style.StyleId, StringComparison.Ordinal)
                && string.Equals(
                    request.AnalysisKind,
                    AiAnalysisKind.ProductJointUnderstanding,
                    StringComparison.Ordinal));
            var session = firstLayer is null
                ? null
                : await _resultReader.LoadSessionAsync($"session-{firstLayer.RequestId}", cancellationToken).ConfigureAwait(false);
            var resultInputs = await _resultReader.LoadStyleResultsAsync(
                snapshot.Requests,
                style.StyleId,
                cancellationToken).ConfigureAwait(false);
            foreach (var input in resultInputs)
            {
                if (input.Ledger?.UpdatedAt > planCreatedAt)
                {
                    planCreatedAt = input.Ledger.UpdatedAt;
                }
            }

            var selectedRequirements = snapshot.Projection.Styles
                .Single(item => string.Equals(item.StyleId, style.StyleId, StringComparison.Ordinal))
                .Pages
                .Where(page => page.CanGenerate)
                .SelectMany(page => page.Requirements ?? [])
                .Where(requirement => requirement.SelectedAssetId is not null)
                .Where(requirement => snapshot.Batch.TemplateSnapshot.Pages
                    .SelectMany(page => page.FrameIds)
                    .Contains(requirement.RequirementId, StringComparer.Ordinal))
                .GroupBy(requirement => requirement.RequirementId, StringComparer.Ordinal)
                .Select(group => group.First())
                .Select(requirement =>
                {
                    var assetId = requirement.SelectedAssetId!;
                    var revision = session?.LockedSelections.TryGetValue(
                        requirement.RequirementId,
                        out var locked) == true
                        ? locked.SelectionRevision
                        : 0;
                    return new AiConfirmedRenderSelection(
                        requirement.RequirementId,
                        assetId,
                        null,
                        revision,
                        SelectionRequirement: requirement.SelectionRequirement);
                })
                .ToArray();

            AiVariantPlanningResult? variantPlanning = null;
            var jointResult = resultInputs
                .Where(input => input.Result is not null
                    && string.Equals(
                        input.Request.AnalysisKind,
                        AiAnalysisKind.ProductJointUnderstanding,
                        StringComparison.Ordinal))
                .OrderByDescending(input => input.Ledger?.UpdatedAt ?? DateTimeOffset.MinValue)
                .Select(input => input.Result)
                .FirstOrDefault(result => result is not null);
            if (jointResult is not null && firstLayer is not null)
            {
                var manualSelections = session?.LockedSelections
                    .ToDictionary(
                        item => item.Key,
                        item => item.Value.AssetId,
                        StringComparer.Ordinal)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
                variantPlanning = AiVariantPlanner.Plan(
                    jointResult.VariantGroups,
                    firstLayer.Requirements,
                    jointResult.RequirementMatches,
                    manualSelections);
            }
            if (variantPlanning?.HasVariants == true)
            {
                var primary = variantPlanning.Variants.First(item => item.IsPrimary);
                var secondary = variantPlanning.Variants
                    .Where(item => !item.IsPrimary)
                    .OrderBy(item => item.VariantId, StringComparer.Ordinal)
                    .FirstOrDefault();
                selectedRequirements = selectedRequirements.Select(selection =>
                {
                    // User-confirmed product-page selections are authoritative,
                    // including information images outside the color groups.
                    // Per-variant outputs still use the color planner's gates.
                    if (selection.SelectionRevision > 0 && snapshot.Batch.TemplateSnapshot.Pages.Any(page =>
                        page.GenerationMode == TemplateGenerationMode.OncePerProduct
                        && page.FrameIds.Contains(selection.FrameId, StringComparer.Ordinal)))
                    {
                        return selection;
                    }
                    var requestedSecondary = selection.SelectionRequirement?.Contains("副色", StringComparison.OrdinalIgnoreCase) == true
                        || selection.SelectionRequirement?.Contains("secondary", StringComparison.OrdinalIgnoreCase) == true;
                    var plan = requestedSecondary && secondary is not null ? secondary : primary;
                    return plan.RequirementAssets.TryGetValue(selection.FrameId, out var roleAsset)
                        ? selection with { AssetId = roleAsset, VariantId = plan.VariantId }
                        : selection with { SelectionConflictCode = "AI_VARIANT_ROLE_SELECTION_MISSING" };
                }).ToArray();
            }
            var selectedAssetIds = selectedRequirements
                .Select(selection => selection.AssetId)
                .ToHashSet(StringComparer.Ordinal);
            // Every rendered color uses its own source geometry, including non-primary assets.
            selectedAssetIds.UnionWith(variantPlanning?.Variants.SelectMany(variant => variant.RequirementAssets.Values) ?? []);
            var geometryByAsset = resultInputs
                .Where(input => input.Result is not null
                    && input.Request.AnalysisKind is AiAnalysisKind.ProductJointUnderstanding
                        or AiAnalysisKind.SelectedAssetGeometry
                    && (input.Request.AnalysisKind == AiAnalysisKind.ProductJointUnderstanding
                        || input.Request.Assets.Any(asset => selectedAssetIds.Contains(asset.AssetId))))
                .SelectMany(input => input.Result!.SubjectGeometry)
                .Where(geometry => selectedAssetIds.Contains(geometry.AssetId))
                .GroupBy(geometry => geometry.AssetId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(geometry => geometry.Confidence).First(),
                    StringComparer.Ordinal);
            selectedRequirements = selectedRequirements.Select(selection => selection with
            {
                SubjectGeometry = geometryByAsset.GetValueOrDefault(selection.AssetId)
            }).ToArray();

            IReadOnlyList<AiOutputVariantPlan>? variantPlans = variantPlanning?.HasVariants == true
                ? variantPlanning.Variants
                : null;
            var sizeResultInputs = resultInputs.Where(input => input.Result is not null
                && input.Request.AnalysisKind is AiAnalysisKind.ProductJointUnderstanding
                    or AiAnalysisKind.SizeTableExtraction)
                .ToArray();
            var sizeCandidates = sizeResultInputs
                .Where(input => input.Result!.SizeTableCandidate is not null)
                .Select(input => input.Result!.SizeTableCandidate!)
                .GroupBy(candidate => JsonSerializer.Serialize(new
                {
                    candidate.SourceAssetId,
                    candidate.Columns,
                    candidate.Rows
                }), StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(candidate => candidate.Confidence).First())
                .ToArray();
            var hasProviderConflict = sizeResultInputs.SelectMany(input => input.Result!.Warnings)
                .Any(warning => warning.Code == "size_source_conflict");
            var sizeCandidate = sizeCandidates.Length == 1 && !hasProviderConflict
                ? sizeCandidates[0]
                : null;
            var preparedSizePages = Array.Empty<AiPreparedSizeTablePage>();
            string? sizeTableReason = null;
            if (snapshot.TemplateRevision.Pages.Any(page =>
                    page.ContentType == TemplatePageContentType.DynamicSizeTable))
            {
                var preparation = await PrepareSizeTablesAsync(
                    snapshot,
                    style,
                    sizeCandidates,
                    hasProviderConflict,
                    session,
                    planCreatedAt,
                    cancellationToken).ConfigureAwait(false);
                preparedSizePages = preparation.Pages;
                sizeTableReason = preparation.ReasonCode;
            }
            styleInputs.Add(new AiRenderStyleInput(
                style.StyleId,
                selectedRequirements,
                sizeCandidate,
                preparedSizePages,
                sizeTableReason,
                variantPlans,
                geometryByAsset));
        }

        return await _renderPlans.PrepareAsync(
            new AiRenderPlanPreparationRequest(
                snapshot.Batch,
                snapshot.TemplateRevision,
                resources,
                styleInputs,
                planCreatedAt),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PreparedSizeTableResult> PrepareSizeTablesAsync(
        AiRenderPreparationSnapshot snapshot,
        StyleDraft style,
        AiSizeTableCandidateV1[] candidates,
        bool hasProviderConflict,
        AiAnalysisSession? session,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        if (candidates.Length == 0)
        {
            return new PreparedSizeTableResult([], "SIZE_TABLE_SOURCE_MISSING");
        }
        if (_sizeTables is null || _sizeTableRasterizer is null)
        {
            return new PreparedSizeTableResult([], "AI_SIZE_TABLE_RASTERIZER_PENDING");
        }

        var manifest = ProjectManifest(snapshot.Batch);
        var prepared = new List<AiPreparedSizeTablePage>();
        string? blockingReason = null;
        foreach (var page in snapshot.TemplateRevision.Pages
                     .Where(page => page.ContentType == TemplatePageContentType.DynamicSizeTable)
                     .OrderBy(page => page.Order))
        {
            var selectedSource = session?.LockedSelections.GetValueOrDefault(
                AiAnalysisInputProjector.SizeTableRequirementId(page.PageId));
            if (hasProviderConflict && selectedSource is null)
            {
                blockingReason = "SIZE_TABLE_SOURCE_CONFLICT";
                continue;
            }
            var result = SizeTableService.Prepare(new PrepareSizeTableCommand(
                manifest,
                style.StyleId,
                page.PageId,
                candidates,
                selectedSource?.AssetId,
                selectedSource?.SelectionRevision ?? 0,
                createdAt));
            if (result.Table is null)
            {
                blockingReason = result.ReasonCode;
                continue;
            }

            var table = result.Table;
            var persisted = await _sizeTables.LoadAsync(
                snapshot.Batch.BatchId,
                style.StyleId,
                page.PageId,
                cancellationToken).ConfigureAwait(false);
            if (persisted.Status == SizeTableLoadStatus.RecoveryRequired)
            {
                blockingReason = "SIZE_TABLE_STATE_CORRUPT";
                continue;
            }
            if (persisted.Table is not null)
            {
                if (!string.Equals(
                        persisted.Table.Source.ContentDigest,
                        table.Source.ContentDigest,
                        StringComparison.Ordinal))
                {
                    blockingReason = "SIZE_TABLE_SOURCE_CHANGE_REVIEW_REQUIRED";
                    continue;
                }
                table = persisted.Table;
            }
            else
            {
                var save = await _sizeTables.SaveAsync(table, null, cancellationToken).ConfigureAwait(false);
                if (save.Status != SizeTableSaveStatus.Saved)
                {
                    blockingReason = save.ReasonCode ?? "SIZE_TABLE_STATE_CORRUPT";
                    continue;
                }
            }

            if (!table.CanSave)
            {
                blockingReason = table.Status == SizeTableStatus.ReviewRequired
                    ? "SIZE_TABLE_REVIEW_REQUIRED"
                    : "SIZE_TABLE_INVALID";
                continue;
            }

            if (page.SizeTableCalibration is { } savedCalibration)
            {
                var template = await _templateResources.GetByResourceIdAsync(page.TemplateResourceId, cancellationToken).ConfigureAwait(false);
                if (template is null || !SizeTableCalibrationResolver.Matches(savedCalibration, template.Inspection))
                {
                    blockingReason = "SIZE_TABLE_CALIBRATION_INVALID";
                    continue;
                }
            }
            var raster = await _sizeTableRasterizer.RasterizeAsync(
                new SizeTableRasterizationRequest(
                    table,
                    page.SizeTableCalibration is { } calibration
                        ? SizeTableCalibrationResolver.RasterStyle(calibration)
                        : TemplateBoundRasterStyle(snapshot.TemplateRevision.RuleDigest!)),
                cancellationToken).ConfigureAwait(false);
            prepared.Add(new AiPreparedSizeTablePage(page.PageId, table, raster,
                CalibratedInsertion: page.SizeTableCalibration is null ? null : SizeTableCalibrationResolver.Resolve(page, raster)));
        }

        return new PreparedSizeTableResult(
            prepared.ToArray(),
            blockingReason ?? (prepared.Count > 0 ? "SIZE_TABLE_VALID" : "SIZE_TABLE_SOURCE_MISSING"));
    }

    private static AssetManifestV1 ProjectManifest(BatchDraft batch) => new(
        AssetManifestProtocol.Version,
        $"manifest_{batch.BatchId}",
        batch.BatchId,
        batch.InputType == BatchInputType.SingleStyle ? "single_style" : "style_directory_batch",
        new AssetManifestTemplateSnapshot(
            batch.TemplateSnapshot.SnapshotId,
            batch.TemplateSnapshot.RevisionId,
            batch.TemplateSnapshot.RulesDigest,
            batch.TemplateSnapshot.Pages.OrderBy(page => page.Order).Select(page => page.PageId).ToArray()),
        batch.Styles.OrderBy(style => style.Order).Select(style => new AssetManifestStyle(
            style.StyleId,
            style.DefaultStyleCode,
            style.SourceDirectoryRelativePath,
            style.Order,
            style.AssetIds.ToArray())).ToArray(),
        batch.Assets.OrderBy(asset => asset.SourceRelativePath, StringComparer.Ordinal).Select(asset =>
            new AssetManifestAsset(
                asset.AssetId,
                asset.StyleId,
                asset.SourceRelativePath,
                asset.FileName,
                asset.Length,
                asset.Sha256,
                asset.PixelWidth,
                asset.PixelHeight,
                asset.Format.ToString().ToLowerInvariant(),
                asset.MimeType,
                asset.Readability == AssetReadability.Readable ? "readable" : "unreadable")).ToArray(),
        batch.ManifestDigest ?? throw new InvalidOperationException("SIZE_TABLE_MANIFEST_MISSING"),
        batch.UpdatedAt);

    private static SizeTableRasterStyle TemplateBoundRasterStyle(string rulesDigest)
    {
        var suffix = rulesDigest.StartsWith("sha256:", StringComparison.Ordinal)
            ? new string(rulesDigest.AsSpan(7, 16))
            : throw new InvalidOperationException("SIZE_TABLE_TEMPLATE_RULES_DIGEST_INVALID");
        return SizeTableRasterStyle.Create(
            $"template_{suffix}_v1",
            "dynamic_grid_v1",
            "Microsoft YaHei UI",
            24,
            1000,
            56,
            24,
            12,
            "#FFFFFFFF",
            "#FFF2F4F7",
            "#FF101828",
            "#FF98A2B3");
    }

    private sealed record PreparedSizeTableResult(
        AiPreparedSizeTablePage[] Pages,
        string ReasonCode);
}
