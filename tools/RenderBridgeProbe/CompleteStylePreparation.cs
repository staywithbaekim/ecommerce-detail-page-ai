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
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Domain.SizeTables;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Inputs;
using Desktop.Infrastructure.Persistence;
using Desktop.Infrastructure.SizeTables;

// S7 full-style evidence: inspected, explicitly selected local assets; no provider request.
internal static class CompleteStylePreparation
{
    public static async Task PrepareAsync(LocalAppPathPolicy paths, string input, IPsdInspectionPort inspector)
    {
        if (File.Exists(paths.ResolveOwnedPath("batch-preparation.json"))
            || File.Exists(paths.ResolveOwnedPath("app/state/desktop-v1.db"))) throw Invalid();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.ResolveOwnedPath(input)));
        var data = document.RootElement;
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        var revisionId = "revision_" + suffix;
        var groupId = "group_" + suffix;
        var batchId = "batch_" + suffix;
        var styleId = data.GetProperty("style_id").GetString()!;
        var files = new AtomicFileStore(paths);
        var pages = new List<TemplatePage>();
        var templates = new List<ImportedTemplateResource>();
        var bindings = new List<FrameBinding>();
        var selections = new List<AiConfirmedRenderSelection>();
        var assets = new List<SourceAssetMetadata>();
        foreach (var item in data.GetProperty("assets").EnumerateArray())
        {
            var relative = item.GetProperty("relative_path").GetString()!;
            var metadata = (await new StreamingAssetMetadataReader().ReadAsync(paths.ResolveOwnedPath("inputs"), relative, default)).Metadata ?? throw Invalid();
            if (!metadata.Sha256.Equals(item.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw Invalid();
            assets.Add(SourceAssetMetadata.CreateReadable(item.GetProperty("id").GetString()!, styleId, relative,
                Path.GetFileName(relative), metadata.Length, metadata.Sha256, metadata.PixelWidth, metadata.PixelHeight, metadata.Format, metadata.MimeType));
        }
        var size = data.GetProperty("size_calibration");
        var insertion = size.GetProperty("insertion");
        var rasterStyle = size.GetProperty("style");
        SizeTableTemplateLayer SizeLayer(JsonElement item) => new(item.GetProperty("layer_path").EnumerateArray().Single().GetInt32(),
            item.GetProperty("layer_id").GetInt32(), item.GetProperty("kind").GetString()!, item.GetProperty("name").GetString()!,
            item.GetProperty("structure_signature").GetString()!);
        var area = insertion.GetProperty("safe_bounds");
        foreach (var item in data.GetProperty("pages").EnumerateArray())
        {
            var pageId = item.GetProperty("id").GetString()!;
            var relative = item.GetProperty("template_relative_path").GetString()!;
            var inspected = await inspector.InspectAsync(new(pageId, relative, IncludeRenderContentEvidence: true), default);
            if (!inspected.IsSuccess) throw new InvalidDataException(inspected.Error!.Code);
            var baseline = inspected.Inspection!;
            if (!baseline.FileSha256.Equals(item.GetProperty("template_sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw Invalid();
            await files.WriteAtomicallyAsync("inspections/" + pageId + ".json", JsonSerializer.SerializeToUtf8Bytes(baseline, BridgeProtocol.JsonOptions), default);
            var frames = item.GetProperty("frames").EnumerateArray().ToArray();
            var calibration = frames.Length == 0 ? new SizeTableTemplateCalibration("size-table-template/v1", baseline.FileSha256,
                insertion.GetProperty("layer_name").GetString()!, SizeLayer(insertion.GetProperty("above_layer")),
                insertion.GetProperty("hidden_layers").EnumerateArray().Select(SizeLayer).ToImmutableArray(),
                TemplateRect.Create(area.GetProperty("left").GetDouble(), area.GetProperty("top").GetDouble(), area.GetProperty("right").GetDouble(), area.GetProperty("bottom").GetDouble()),
                new(rasterStyle.GetProperty("font_family").GetString()!, rasterStyle.GetProperty("font_size").GetDouble(), rasterStyle.GetProperty("target_width").GetInt32(),
                    rasterStyle.GetProperty("minimum_row_height").GetDouble(), rasterStyle.GetProperty("horizontal_cell_padding").GetDouble(),
                    rasterStyle.GetProperty("vertical_cell_padding").GetDouble(), rasterStyle.GetProperty("background_color").GetString()!,
                    rasterStyle.GetProperty("header_background_color").GetString()!, rasterStyle.GetProperty("text_color").GetString()!, rasterStyle.GetProperty("grid_color").GetString()!)) : null;
            if (calibration is not null && !SizeTableCalibrationResolver.Matches(calibration, baseline)) throw Invalid();
            var page = TemplatePage.Create(pageId, revisionId, pages.Count, item.GetProperty("name").GetString()!,
                calibration is null ? TemplatePageContentType.NormalComposition : TemplatePageContentType.DynamicSizeTable,
                TemplateGenerationMode.OncePerProduct, null, true, "template_" + pageId, sizeTableCalibration: calibration);
            pages.Add(page);
            templates.Add(new(page.TemplateResourceId, groupId, revisionId, relative, baseline.SizeBytes, baseline.FileSha256,
                "sha256:" + baseline.StructureSignature.Replace("sha256:", "", StringComparison.Ordinal), baseline, now));
            foreach (var frame in frames)
            {
                var layer = baseline.Layers.Single(l => l.LayerId == frame.GetProperty("layer_id").GetInt32());
                if (!layer.Visible || !layer.EffectiveVisible || layer.SmartObject?.Kind != "data"
                    || layer.PixelEvidence is not { Available: true, CompositeContributionPixelCount: > 0 }) throw Invalid();
                var superseded = frame.GetProperty("superseded_layer_ids").EnumerateArray()
                    .Select(id => baseline.Layers.Single(l => l.LayerId == id.GetInt32())).ToArray();
                if (superseded.Any(old => !old.Visible || !old.EffectiveVisible || old.SmartObject?.Kind != "data"
                    || old.Path.Count != layer.Path.Count || !old.Path.SkipLast(1).SequenceEqual(layer.Path.SkipLast(1))
                    || old.Path[^1] >= layer.Path[^1] || old.Clipping != layer.Clipping
                    || layer.Clipping && old.ClippingBase?.LayerId != layer.ClippingBase?.LayerId)) throw Invalid();
                var bounds = layer.Clipping ? layer.ClippingBase?.Bounds ?? throw Invalid()
                    : new PsdLayerBounds(0, 0, baseline.Canvas.Width, baseline.Canvas.Height);
                var frameId = "frame_" + pageId + "_" + layer.LayerId;
                var assetId = frame.GetProperty("asset_id").GetString()!;
                if (assets.All(a => a.AssetId != assetId)) throw Invalid();
                var purpose = frame.GetProperty("purpose").GetString()!;
                bindings.Add(FrameBinding.Create(frameId, pageId, LayerPath.Create(layer.Path.Select(x => x + 1)),
                    ExpectedLayerEvidence.Create(layer.LayerId, layer.Name, layer.Visible, layer.Clipping, layer.HasMask, layer.HasVectorMask,
                        checked((byte)layer.Opacity), layer.BlendMode, LayerStructureSignature.Create("sha256:" + baseline.StructureSignature.Replace("sha256:", "", StringComparison.Ordinal))),
                    superseded.Select(old => SupersededLayerEvidence.Create(LayerPath.Create(old.Path.Select(x => x + 1)), old.Clipping, old.LayerId)),
                    SelectionRequirement.Create(purpose + "；完整款渲染联验的显式选图。"),
                    LayoutIntent.Create(purpose.Contains("完整", StringComparison.Ordinal) ? "subject_complete" : "detail_crop", 1, 0, 0,
                        TemplateRect.Create(Math.Max(0, bounds.Left), Math.Max(0, bounds.Top), Math.Min(baseline.Canvas.Width, bounds.Right), Math.Min(baseline.Canvas.Height, bounds.Bottom)))));
                selections.Add(new(frameId, assetId, null, 1));
            }
            Console.WriteLine($"INSPECTED {pageId} targets={frames.Length}");
        }
        var revision = TemplateRevision.Restore(revisionId, groupId, 1, TemplateRevisionState.Committed,
            "complete-template-snapshot.json", "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { pages, bindings }))), now, pages, bindings);
        var sizePage = pages.Single(p => p.ContentType == TemplatePageContentType.DynamicSizeTable);
        var source = assets.Single(a => a.AssetId == data.GetProperty("size_source").GetString());
        var columns = data.GetProperty("columns").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var rows = data.GetProperty("rows").EnumerateArray().Select(r => (IReadOnlyList<string>)r.EnumerateArray().Select(x => x.GetString()!).ToArray()).ToArray();
        var table = SizeTable.FromCandidate("size_" + suffix, batchId, styleId, sizePage.PageId,
            SizeTableCandidate.Create("candidate_" + suffix, SizeTableSource.Create(source.AssetId, source.Sha256), 1m, columns, rows), now);
        var raster = await new WpfSizeTableRasterizer(files).RasterizeAsync(new(table, SizeTableCalibrationResolver.RasterStyle(sizePage.SizeTableCalibration!)), default);
        var includeEditablePsd = !data.TryGetProperty("include_editable_psd", out var includePsd) || includePsd.GetBoolean();
        var batch = BatchDraft.Create(batchId, paths.ResolveOwnedPath("inputs"), paths.ResolveOwnedPath("outputs"),
            BatchTemplateSnapshot.Create("snapshot_" + suffix, batchId, revision, pages.Select(p => p.PageId), now), BatchSelectionMode.Manual,
            includeEditablePsd, now).BeginScanning(now.AddTicks(1));
        var style = StyleDraft.Create(styleId, data.GetProperty("style_name").GetString()!, "xinma6015", 0, assets.Select(a => a.AssetId));
        await using var store = new SqliteDesktopStore(new(paths.ResolveOwnedPath("app")));
        await store.InitializeAsync();
        await store.SaveGroupAsync(TemplateGroup.Create(groupId, "A馨妈6015-58完整款渲染联验", now), default);
        await store.SaveDraftRevisionAsync(TemplateRevision.CreateDraft(revisionId, groupId, 1, now, pages, bindings), default);
        await store.CommitRevisionAsync(revision, null, default);
        foreach (var template in templates) await store.SaveImportedAsync("import_" + template.ResourceId, template, default);
        var schemas = new JsonSchemaContractValidator();
        var committed = await new SqliteBatchScanCommitter(store, new AssetManifestFileStore(files, schemas), TimeProvider.System, new JsonInputCommitJournalStore(files, paths))
            .CommitAsync(new(batch, BatchInputType.StyleDirectoryBatch, [style], assets, AssetManifestV1.Create(batch, BatchInputType.StyleDirectoryBatch, [style], assets,
                Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(assets))), now)), default);
        batch = committed.Draft ?? throw Invalid();
        var validator = new RenderPlanValidator(schemas);
        var preparation = await new AiRenderPlanFactory(new TemplateRenderPlanBuilder(validator), new UnavailablePhotoshopAvailabilityPort(), new SizeTableRenderPlanBuilder(validator), store)
            .PrepareAsync(new(batch, revision, templates, [new(styleId, selections, PreparedSizeTablePages: [new(sizePage.PageId, table, raster,
                CalibratedInsertion: SizeTableCalibrationResolver.Resolve(sizePage, raster))])], now), default);
        await files.WriteAtomicallyAsync("complete-template-snapshot.json", JsonSerializer.SerializeToUtf8Bytes(new { revision, templates }), default);
        await files.WriteAtomicallyAsync("preparation-review.json", JsonSerializer.SerializeToUtf8Bytes(preparation), default);
        if (preparation.Pages.Count != pages.Count || preparation.Pages.Any(p => !p.HasPlan
            || !RenderTemplateEvidenceValidator.Matches(p.RenderPlanJson!, templates.Single(t => t.ResourceId == pages.Single(page => page.PageId == p.PageId).TemplateResourceId).Inspection))) throw Invalid();
        await using var coordinator = new RenderBatchCoordinator(store, new OrdinaryPagePreparation.PreparationOnlySessionFactory(), validator,
            new Desktop.Application.Tasks.TaskCoordinator(store), TimeProvider.System);
        await coordinator.PrepareAsync(preparation, default);
        await files.WriteAtomicallyAsync("batch-preparation.json", JsonSerializer.SerializeToUtf8Bytes(new {
            batch_id = batchId, complete_style_probe = true, same_source_identity_probe = false, expected_page_count = pages.Count,
            include_editable_psd = includeEditablePsd,
            manual_source_selection = true, ai_accuracy_evidence = false, target_count = bindings.Count,
            plans = preparation.Pages.Select(p => new { p.StyleId, p.PageId, p.PlanDigest, p.ReasonCode }) }), default);
    }
    private static InvalidDataException Invalid() => new("RENDER_COMPLETE_STYLE_PREPARATION_INVALID");
}
