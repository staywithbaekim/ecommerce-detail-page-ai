using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Application.Inputs;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
using Desktop.Application.Templates;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Domain.SizeTables;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Inputs;
using Desktop.Infrastructure.Persistence;
using Desktop.Infrastructure.SizeTables;

// S7 evidence preparation only: explicit local selections and calibrated templates, no AI requests.
internal static class MixedProductPreparation
{
    public static async Task PrepareAsync(LocalAppPathPolicy paths, string input, IPsdInspectionPort inspector)
    {
        if (File.Exists(paths.ResolveOwnedPath("batch-preparation.json"))
            || File.Exists(paths.ResolveOwnedPath("app/state/desktop-v1.db"))) throw Invalid();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.ResolveOwnedPath(input)));
        var data = document.RootElement;
        var candidate = JsonSerializer.Deserialize<ProbePage>(await File.ReadAllTextAsync(
            paths.ResolveOwnedPath(data.GetProperty("ordinary_candidate").GetString()!)), BridgeProtocol.JsonOptions)!;
        using var sizeDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
            paths.ResolveOwnedPath(data.GetProperty("size_calibration").GetString()!)));
        var size = sizeDocument.RootElement;
        var validator = new RenderPlanValidator(new JsonSchemaContractValidator());
        if (!validator.Validate(candidate.Plan.GetRawText()).IsValid) throw Invalid();
        var ordinaryPath = candidate.Sources.Single(x => x.Purpose == "template_copy").OwnedRelativePath;
        var sizePath = size.GetProperty("template_relative_path").GetString()!;
        var ordinaryInspection = await inspector.InspectAsync(new("mixed_ordinary", ordinaryPath, IncludeRenderContentEvidence: true), default);
        var sizeInspection = await inspector.InspectAsync(new("mixed_size", sizePath, IncludeRenderContentEvidence: true), default);
        if (!ordinaryInspection.IsSuccess || !sizeInspection.IsSuccess
            || !RenderTemplateEvidenceValidator.Matches(candidate.Plan.GetRawText(), ordinaryInspection.Inspection!)) throw Invalid();
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        var revisionId = "revision_" + suffix;
        var groupId = "group_" + suffix;
        var batchId = "batch_" + suffix;
        var insertion = size.GetProperty("insertion");
        SizeTableTemplateLayer Layer(JsonElement item) => new(item.GetProperty("layer_path").EnumerateArray().Single().GetInt32(),
            item.GetProperty("layer_id").GetInt32(), item.GetProperty("kind").GetString()!, item.GetProperty("name").GetString()!,
            item.GetProperty("structure_signature").GetString()!);
        var area = insertion.GetProperty("safe_bounds");
        var style = size.GetProperty("style");
        var calibration = new SizeTableTemplateCalibration("size-table-template/v1", size.GetProperty("template_sha256").GetString()!,
            insertion.GetProperty("layer_name").GetString()!, Layer(insertion.GetProperty("above_layer")),
            insertion.GetProperty("hidden_layers").EnumerateArray().Select(Layer).ToImmutableArray(),
            TemplateRect.Create(area.GetProperty("left").GetDouble(), area.GetProperty("top").GetDouble(), area.GetProperty("right").GetDouble(), area.GetProperty("bottom").GetDouble()),
            new(style.GetProperty("font_family").GetString()!, style.GetProperty("font_size").GetDouble(), style.GetProperty("target_width").GetInt32(),
                style.GetProperty("minimum_row_height").GetDouble(), style.GetProperty("horizontal_cell_padding").GetDouble(),
                style.GetProperty("vertical_cell_padding").GetDouble(), style.GetProperty("background_color").GetString()!,
                style.GetProperty("header_background_color").GetString()!, style.GetProperty("text_color").GetString()!, style.GetProperty("grid_color").GetString()!));
        if (!SizeTableCalibrationResolver.Matches(calibration, sizeInspection.Inspection!)) throw Invalid();
        TemplatePage[] pages = [
            TemplatePage.Create("page_front", revisionId, 0, "正面", TemplatePageContentType.NormalComposition, TemplateGenerationMode.OncePerVariant, null, true, "template_ordinary"),
            TemplatePage.Create("page_back", revisionId, 1, "背面", TemplatePageContentType.NormalComposition, TemplateGenerationMode.OncePerVariant, null, true, "template_ordinary"),
            TemplatePage.Create("page_size", revisionId, 2, "尺码", TemplatePageContentType.DynamicSizeTable, TemplateGenerationMode.OncePerProduct, null, true, "template_size", sizeTableCalibration: calibration)];
        var target = candidate.Plan.GetProperty("targets").EnumerateArray().Single();
        var expected = target.GetProperty("expected_layer");
        var layout = target.GetProperty("layout");
        var bounds = layout.GetProperty("safe_bounds");
        var bindings = pages.Take(2).Select(page => FrameBinding.Create("frame_" + page.PageId, page.PageId,
            LayerPath.Create(target.GetProperty("layer_path").EnumerateArray().Select(x => x.GetInt32())),
            ExpectedLayerEvidence.Create(expected.GetProperty("layer_id").GetInt32(), expected.GetProperty("name_before").GetString()!,
                expected.GetProperty("visible").GetBoolean(), expected.GetProperty("clipping").GetBoolean(), expected.GetProperty("user_mask").GetBoolean(),
                expected.GetProperty("vector_mask").GetBoolean(), expected.GetProperty("opacity").GetByte(), expected.GetProperty("blend_mode").GetString()!,
                LayerStructureSignature.Create(expected.GetProperty("structure_signature").GetString()!)),
            target.GetProperty("superseded_layers").EnumerateArray().Select(old => SupersededLayerEvidence.Create(
                LayerPath.Create(old.GetProperty("layer_path").EnumerateArray().Select(x => x.GetInt32())), old.GetProperty("expected_clipping").GetBoolean(),
                old.GetProperty("expected_layer_id").GetInt32())), SelectionRequirement.Create(page.Name + "真实商品图；显式人工选择用于渲染联验。"),
            LayoutIntent.Create(layout.GetProperty("intent").GetString()!, layout.GetProperty("scale").GetDouble(),
                layout.GetProperty("offset").GetProperty("x").GetDouble(), layout.GetProperty("offset").GetProperty("y").GetDouble(),
                TemplateRect.Create(bounds.GetProperty("left").GetDouble(), bounds.GetProperty("top").GetDouble(), bounds.GetProperty("right").GetDouble(), bounds.GetProperty("bottom").GetDouble())))).ToArray();
        var revision = TemplateRevision.Restore(revisionId, groupId, 1, TemplateRevisionState.Committed,
            "mixed-template-snapshot.json", "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { pages, bindings }))), now, pages, bindings);
        ImportedTemplateResource Resource(string id, string path, PsdInspectionDocument inspection) => new(id, groupId, revisionId,
            path, inspection.SizeBytes, inspection.FileSha256, "sha256:" + inspection.StructureSignature.Replace("sha256:", "", StringComparison.Ordinal), inspection, now);
        ImportedTemplateResource[] templates = [Resource("template_ordinary", ordinaryPath, ordinaryInspection.Inspection!), Resource("template_size", sizePath, sizeInspection.Inspection!)];
        var assets = new List<SourceAssetMetadata>();
        var styles = new List<StyleDraft>();
        var renderStyles = new List<AiRenderStyleInput>();
        var files = new AtomicFileStore(paths);
        var products = data.GetProperty("products").EnumerateArray().ToArray();
        if (products.Length < 2) throw Invalid();
        foreach (var product in products)
        {
            var styleId = product.GetProperty("id").GetString()!;
            var styleAssets = new List<SourceAssetMetadata>();
            foreach (var asset in product.GetProperty("assets").EnumerateArray())
            {
                var relative = asset.GetProperty("relative_path").GetString()!;
                var metadata = (await new StreamingAssetMetadataReader().ReadAsync(paths.ResolveOwnedPath("inputs"), relative, default)).Metadata ?? throw Invalid();
                if (!metadata.Sha256.Equals(asset.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw Invalid();
                styleAssets.Add(SourceAssetMetadata.CreateReadable(asset.GetProperty("id").GetString()!, styleId, relative,
                    Path.GetFileName(relative), metadata.Length, metadata.Sha256, metadata.PixelWidth, metadata.PixelHeight, metadata.Format, metadata.MimeType));
            }
            assets.AddRange(styleAssets);
            styles.Add(StyleDraft.Create(styleId, product.GetProperty("name").GetString()!, styleId, styles.Count, styleAssets.Select(x => x.AssetId)));
            var variants = product.GetProperty("variants").EnumerateArray().Select((v, i) => new AiOutputVariantPlan(v.GetProperty("id").GetString()!,
                v.GetProperty("name").GetString()!, i == 0, true, new Dictionary<string, string> {
                    ["frame_page_front"] = v.GetProperty("front").GetString()!, ["frame_page_back"] = v.GetProperty("back").GetString()! }, [], [])).ToArray();
            if (variants.Length == 0 || variants.Any(v => v.RequirementAssets.Values.Any(id => styleAssets.All(a => a.AssetId != id)))) throw Invalid();
            var selections = variants[0].RequirementAssets.Select(p => new AiConfirmedRenderSelection(p.Key, p.Value, null, 1)).ToArray();
            var source = styleAssets.Single(a => a.AssetId == product.GetProperty("size_source").GetString());
            var columns = product.GetProperty("columns").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var rows = product.GetProperty("rows").EnumerateArray().Select(r => (IReadOnlyList<string>)r.EnumerateArray().Select(x => x.GetString()!).ToArray()).ToArray();
            var table = SizeTable.FromCandidate("size_" + styleId, batchId, styleId, "page_size",
                SizeTableCandidate.Create("candidate_" + styleId, SizeTableSource.Create(source.AssetId, source.Sha256), 1m, columns, rows), now);
            var raster = await new WpfSizeTableRasterizer(files).RasterizeAsync(new(table, SizeTableCalibrationResolver.RasterStyle(calibration)), default);
            var geometry = product.TryGetProperty("subject_geometry", out var suppliedGeometry)
                ? suppliedGeometry.Deserialize<AiSubjectGeometryV1[]>(BridgeProtocol.JsonOptions)!.ToDictionary(g => g.AssetId, StringComparer.Ordinal)
                : null;
            renderStyles.Add(new(styleId, selections, PreparedSizeTablePages: [new("page_size", table, raster,
                CalibratedInsertion: SizeTableCalibrationResolver.Resolve(pages[2], raster))], VariantPlans: variants,
                SubjectGeometryByAsset: geometry));
        }
        var batch = BatchDraft.Create(batchId, paths.ResolveOwnedPath("inputs"), paths.ResolveOwnedPath("outputs"),
            BatchTemplateSnapshot.Create("snapshot_" + suffix, batchId, revision, pages.Select(p => p.PageId), now),
            BatchSelectionMode.Manual, true, now).BeginScanning(now.AddTicks(1));
        await using var store = new SqliteDesktopStore(new(paths.ResolveOwnedPath("app")));
        await store.InitializeAsync();
        await store.SaveGroupAsync(TemplateGroup.Create(groupId, "真实两款混合渲染联验", now), default);
        await store.SaveDraftRevisionAsync(TemplateRevision.CreateDraft(revisionId, groupId, 1, now, pages, bindings), default);
        await store.CommitRevisionAsync(revision, null, default);
        foreach (var template in templates) await store.SaveImportedAsync("import_" + template.ResourceId, template, default);
        var committed = await new SqliteBatchScanCommitter(store, new AssetManifestFileStore(files, new JsonSchemaContractValidator()),
            TimeProvider.System, new JsonInputCommitJournalStore(files, paths)).CommitAsync(new(batch, BatchInputType.StyleDirectoryBatch,
                styles, assets, AssetManifestV1.Create(batch, BatchInputType.StyleDirectoryBatch, styles, assets,
                    Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(assets))), now)), default);
        batch = committed.Draft ?? throw Invalid();
        var preparation = await new AiRenderPlanFactory(new TemplateRenderPlanBuilder(validator), new UnavailablePhotoshopAvailabilityPort(),
            new SizeTableRenderPlanBuilder(validator), store).PrepareAsync(new(batch, revision, templates, renderStyles, now), default);
        var expectedPages = renderStyles.Sum(s => s.VariantPlans!.Count * 2 + 1);
        var expectedReview = data.TryGetProperty("expected_review_pages", out var review) ? review.GetInt32() : 0;
        if (preparation.Pages.Count != expectedPages || preparation.Pages.Count(p => !p.HasPlan) != expectedReview
            || preparation.Pages.Any(p => !p.HasPlan && p.ReasonCode != "TPL_RENDER_LAYOUT_REVIEW_REQUIRED")) throw Invalid();
        await using var coordinator = new RenderBatchCoordinator(store, new OrdinaryPagePreparation.PreparationOnlySessionFactory(),
            validator, new Desktop.Application.Tasks.TaskCoordinator(store), TimeProvider.System);
        await coordinator.PrepareAsync(preparation, default);
        await files.WriteAtomicallyAsync("mixed-template-snapshot.json", JsonSerializer.SerializeToUtf8Bytes(new { revision, templates }), default);
        await files.WriteAtomicallyAsync("batch-preparation.json", JsonSerializer.SerializeToUtf8Bytes(new {
            batch_id = batchId, mixed_product_probe = true, same_source_identity_probe = false, expected_page_count = expectedPages,
            manual_source_selection = true, ai_accuracy_evidence = false,
            subject_geometry_available = renderStyles.Any(s => s.SubjectGeometryByAsset is { Count: > 0 }),
            review_page_count = expectedReview,
            styles = styles.Select(s => new { s.StyleId }), source_hashes = assets.Select(a => new { a.AssetId, a.Sha256 }),
            plans = preparation.Pages.Select(p => new { p.StyleId, p.PageId, p.OutputVariantId, p.PlanDigest, p.ReasonCode }) }), default);
    }
    private static InvalidDataException Invalid() => new("RENDER_MIXED_PREPARATION_INVALID");
}
