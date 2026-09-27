using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Application.Psd;
using Desktop.Application.Ai;
using Desktop.Application.Inputs;
using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Inputs;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
using Desktop.Application.Templates;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;
using Desktop.Domain.SizeTables;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.SizeTables;
using Desktop.Infrastructure.Persistence;

// S5 uses the existing probe, domain table, rasterizer and plan builder. This
// explicit candidate snapshot never updates a user's template or batch store.
internal static class SizePagePreparation
{
    public static async Task PrepareAsync(LocalAppPathPolicy paths, string input, IPsdInspectionPort inspector,
        bool persistCalibration = false, bool batchProbe = false)
    {
        var inputPath = paths.ResolveOwnedPath(input);
        if (new FileInfo(inputPath).Length > 1024 * 1024
            || File.Exists(paths.ResolveOwnedPath("page-input.json")) || File.Exists(paths.ResolveOwnedPath("batch-preparation.json")))
            throw new InvalidDataException("SIZE_PREPARATION_INPUT_INVALID");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(inputPath));
        var data = json.RootElement;
        var templatePath = data.GetProperty("template_relative_path").GetString()!;
        var sourcePath = data.GetProperty("size_source_relative_path").GetString()!;
        async Task RequireHash(string relative, string expected)
        {
            await using var stream = File.OpenRead(paths.ResolveOwnedPath(relative));
            if (!string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SIZE_PREPARATION_SOURCE_CHANGED");
        }
        await RequireHash(templatePath, data.GetProperty("template_sha256").GetString()!);
        await RequireHash(sourcePath, data.GetProperty("size_source_sha256").GetString()!);
        var inspected = await inspector.InspectAsync(new("size_preparation", templatePath,
            persistCalibration ? TemplatePageImportService.GetPreviewRelativePath(templatePath) : null,
            IncludeRenderContentEvidence: true), default);
        if (!inspected.IsSuccess) throw new InvalidDataException(inspected.Error!.Code);
        var baseline = inspected.Inspection!;
        var now = DateTimeOffset.UtcNow;
        var columns = data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()!).ToArray();
        var rows = data.GetProperty("rows").EnumerateArray()
            .Select(row => (IReadOnlyList<string>)row.EnumerateArray().Select(value => value.GetString()!).ToArray()).ToArray();
        var candidate = SizeTableCandidate.Create("candidate_size_probe",
            SizeTableSource.Create("source_size_probe", data.GetProperty("size_source_sha256").GetString()!), 1m, columns, rows);
        var table = SizeTable.FromCandidate("size_probe", "render_live_probe", "product_size_probe", "page_size_probe", candidate, now);
        var style = data.GetProperty("style");
        var rasterStyle = SizeTableRasterStyle.Create("size_probe_style_v1", "size_probe_grid_v1",
            style.GetProperty("font_family").GetString()!, style.GetProperty("font_size").GetDouble(),
            style.GetProperty("target_width").GetInt32(), style.GetProperty("minimum_row_height").GetDouble(),
            style.GetProperty("horizontal_cell_padding").GetDouble(), style.GetProperty("vertical_cell_padding").GetDouble(),
            style.GetProperty("background_color").GetString()!, style.GetProperty("header_background_color").GetString()!,
            style.GetProperty("text_color").GetString()!, style.GetProperty("grid_color").GetString()!);
        var files = new AtomicFileStore(paths);
        var inserted = data.GetProperty("insertion");
        var bounds = inserted.GetProperty("safe_bounds");
        var insertion = new SizeTableInsertion(inserted.GetProperty("layer_name").GetString()!,
            inserted.GetProperty("above_layer").Deserialize<SizeTableLayerReference>(BridgeProtocol.JsonOptions)!,
            inserted.GetProperty("hidden_layers").Deserialize<SizeTableLayerReference[]>(BridgeProtocol.JsonOptions)!,
            TemplateRect.Create(bounds.GetProperty("left").GetDouble(), bounds.GetProperty("top").GetDouble(),
                bounds.GetProperty("right").GetDouble(), bounds.GetProperty("bottom").GetDouble()),
            inserted.GetProperty("placement").Deserialize<RenderTargetPlacement>(BridgeProtocol.JsonOptions)!);
        var target = new SizeTableInsertionTarget("revision_size_probe", baseline.FileSha256, "page_size_probe", insertion);
        var page = TemplatePage.Create(target.PageId, target.TemplateRevisionId, 0, "尺码受控验证候选",
            TemplatePageContentType.DynamicSizeTable, TemplateGenerationMode.OncePerProduct, null, true, "template_size_probe");
        var revision = TemplateRevision.Restore(target.TemplateRevisionId, "group_size_probe", 1,
            TemplateRevisionState.Committed, "candidate-template-snapshot.json", "sha256:" + baseline.FileSha256, now, [page], []);
        var resource = new ImportedTemplateResource(page.TemplateResourceId, revision.GroupId, revision.RevisionId,
            templatePath, baseline.SizeBytes, baseline.FileSha256, baseline.StructureSignature, baseline, now);
        if (persistCalibration)
        {
            var dataOptions = new DesktopDataOptions(Path.GetDirectoryName(inputPath)!);
            if (File.Exists(dataOptions.DatabasePath)) throw new InvalidDataException("SIZE_PREPARATION_INPUT_INVALID");
            static TemplateSizeLayer Layer(SizeTableLayerReference layer) => new(layer.LayerPath.Single(), layer.LayerId,
                layer.Kind, layer.Name, layer.StructureSignature);
            var calibration = new TemplateSizeCalibration("size-table-template/v1", baseline.FileSha256, insertion.LayerName,
                Layer(insertion.AboveLayer), insertion.HiddenLayers.Select(Layer).ToArray(),
                new(insertion.SafeBounds.Left, insertion.SafeBounds.Top, insertion.SafeBounds.Right, insertion.SafeBounds.Bottom),
                new(rasterStyle.FontFamily, rasterStyle.FontSize, rasterStyle.TargetWidth, rasterStyle.MinimumRowHeight,
                    rasterStyle.HorizontalCellPadding, rasterStyle.VerticalCellPadding, rasterStyle.BackgroundColor,
                    rasterStyle.HeaderBackgroundColor, rasterStyle.TextColor, rasterStyle.GridColor));
            await using (var store = new SqliteDesktopStore(dataOptions))
            {
                await store.InitializeAsync();
                await store.SaveGroupAsync(TemplateGroup.Create(revision.GroupId, "RENDER 尺码校准受控验证", now), default);
                await store.SaveImportedAsync("import_size_probe", resource, default);
                await store.SaveDraftRevisionAsync(TemplateRevision.CreateDraft(revision.RevisionId, revision.GroupId, 1, now, [page]), default);
                var workspace = new TemplateWorkspaceService(store, store, null!, TimeProvider.System,
                    new JsonManifestStore(files), paths: paths, files: files);
                var session = new TemplateDraftSession(revision.GroupId, "RENDER 尺码校准受控验证", revision.RevisionId, 1, new HashSet<string>());
                await workspace.CalibrateSizeTableAsync(session, page.PageId, calibration, default);
                await workspace.CommitDraftAsync(session, default);
            }
            await using var reopened = new SqliteDesktopStore(dataOptions);
            await reopened.InitializeAsync();
            revision = (await reopened.GetRevisionAsync(revision.RevisionId, default))!;
            page = revision.Pages.Single();
            rasterStyle = SizeTableCalibrationResolver.RasterStyle(page.SizeTableCalibration!);
        }
        var raster = await new WpfSizeTableRasterizer(files).RasterizeAsync(new(table, rasterStyle), default);
        if (persistCalibration) target = SizeTableCalibrationResolver.Resolve(page, raster);
        var validator = new RenderPlanValidator(new JsonSchemaContractValidator());
        var attempt = Guid.NewGuid().ToString("N");
        var plan = new SizeTableRenderPlanBuilder(validator).Build(revision, page, resource,
            new("plan_size_" + attempt, "size_attempt_" + attempt, "size_correlation_" + attempt, table.BatchId, table.StyleId,
                "artifact_size_" + attempt, "size-table.jpg", true, "size-table.psd", now, table, raster, CalibratedInsertion: target));
        BridgeResourceSource[] sources = [new(resource.ResourceId, "template_copy", "image/vnd.adobe.photoshop", templatePath),
            new(raster.ResourceId, raster.Role, raster.MediaType, raster.OwnedRelativePath)];
        using var parsed = JsonDocument.Parse(plan);
        var prepared = new ProbePage(parsed.RootElement.Clone(), sources, baseline);
        // No overwrite: use a new controlled root for every independent attempt.
        async Task Create(string relative, object value)
        {
            await using var output = new FileStream(paths.ResolveOwnedPath(relative), FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(output, value, BridgeProtocol.JsonOptions);
            output.Flush(true);
        }
        await Create("candidate-template-snapshot.json", new { candidate = true, revision, resource });
        await Create("size-preparation-evidence.json", new { candidate = true, calibration_persisted_and_reopened = persistCalibration,
            source_digest = table.Source.ContentDigest,
            structured_data_digest = table.DataDigest, raster, baseline_layer_count = baseline.LayerCount,
            requires_product_visual_acceptance = true });
        await Create("page-input.json", prepared);
        if (batchProbe)
        {
            // Register the real source for scan provenance only. The plan still renders only the deterministic PNG.
            var inputRoot = paths.ResolveOwnedPath("inputs");
            var relativeSource = Path.GetRelativePath(inputRoot, paths.ResolveOwnedPath(sourcePath));
            var metadata = (await new StreamingAssetMetadataReader().ReadAsync(inputRoot, relativeSource, default)).Metadata
                ?? throw new InvalidDataException("SIZE_PREPARATION_INPUT_INVALID");
            var asset = SourceAssetMetadata.CreateReadable("source_size_probe", table.StyleId, relativeSource,
                Path.GetFileName(sourcePath), metadata.Length, metadata.Sha256, metadata.PixelWidth, metadata.PixelHeight,
                metadata.Format, metadata.MimeType);
            var styleDraft = StyleDraft.Create(table.StyleId, "尺码受控验证", "", 0, [asset.AssetId]);
            var batch = BatchDraft.Create(table.BatchId, inputRoot, paths.ResolveOwnedPath("outputs"),
                BatchTemplateSnapshot.Create("snapshot_size_probe", table.BatchId, revision, [page.PageId], now),
                BatchSelectionMode.Manual, true, now).BeginScanning(now.AddTicks(1));
            await using var store = new SqliteDesktopStore(new(paths.ResolveOwnedPath("app")));
            await store.InitializeAsync();
            await store.SaveGroupAsync(TemplateGroup.Create(revision.GroupId, "尺码批次受控验证", now), default);
            await store.SaveDraftRevisionAsync(TemplateRevision.CreateDraft(revision.RevisionId, revision.GroupId, 1, now, revision.Pages), default);
            await store.CommitRevisionAsync(revision, null, default);
            await store.SaveImportedAsync("import_size_probe", resource, default);
            var manifests = new AssetManifestFileStore(files, new JsonSchemaContractValidator());
            var committed = await new SqliteBatchScanCommitter(store, manifests, TimeProvider.System,
                new JsonInputCommitJournalStore(files, paths)).CommitAsync(new(batch, BatchInputType.SingleStyle,
                    [styleDraft], [asset], AssetManifestV1.Create(batch, BatchInputType.SingleStyle, [styleDraft], [asset],
                        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(asset))), now)), default);
            if (committed.Draft is null) throw new InvalidDataException("SIZE_PREPARATION_INPUT_INVALID");
            await using var coordinator = new RenderBatchCoordinator(store, new OrdinaryPagePreparation.PreparationOnlySessionFactory(),
                validator, new Desktop.Application.Tasks.TaskCoordinator(store), TimeProvider.System);
            await coordinator.PrepareAsync(new(table.BatchId, AiRenderPreparationStatus.ReadyToRender, "READY",
                [new(table.StyleId, page.PageId, AiRenderPagePreparationStatus.ReadyToRender, "SIZE_TABLE_PLAN_READY",
                    plan, parsed.RootElement.GetProperty("plan_digest").GetString())]), default);
            await Create("batch-preparation.json", new { batch_id = table.BatchId, dynamic_size_probe = true });
        }
    }
}
