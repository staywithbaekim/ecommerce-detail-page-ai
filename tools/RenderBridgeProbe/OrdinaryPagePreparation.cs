using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Application.Psd;
using Desktop.Application.Rendering;
using Desktop.Application.Templates;
using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Inputs;
using Desktop.Infrastructure.Persistence;
using Desktop.Application.Inputs;
using System.Text.Json.Nodes;

// Reuse registered technical-candidate bindings, but generate new geometry and identities
// through the production factory. Historical plans and outputs are never rewritten.
internal static class OrdinaryPagePreparation
{
    public static async Task PrepareAsync(LocalAppPathPolicy paths, string input, IPsdInspectionPort inspector,
        bool batchProbe = false, bool styleBoundaryProbe = false)
    {
        var inputPath = paths.ResolveOwnedPath(input);
        if (new FileInfo(inputPath).Length > 16 * 1024 * 1024
            || File.Exists(paths.ResolveOwnedPath("page-input.json")) || File.Exists(paths.ResolveOwnedPath("batch-preparation.json")))
            throw new InvalidDataException("RENDER_PREPARATION_INPUT_INVALID");
        var candidate = JsonSerializer.Deserialize<ProbePage>(await File.ReadAllTextAsync(inputPath), BridgeProtocol.JsonOptions)!;
        var original = candidate.Plan;
        if (original.GetProperty("document").GetProperty("content_type").GetString() != "ordinary_set"
            || !new RenderPlanValidator(new JsonSchemaContractValidator()).Validate(original.GetRawText()).IsValid)
            throw new InvalidDataException("RENDER_PREPARATION_INPUT_INVALID");
        var templateId = original.GetProperty("document").GetProperty("template_resource_id").GetString()!;
        var templateSource = candidate.Sources.Single(source => source.ResourceId == templateId);
        var inspected = await inspector.InspectAsync(new("ordinary_preparation", templateSource.OwnedRelativePath,
            IncludeRenderContentEvidence: true), default);
        if (!inspected.IsSuccess) throw new InvalidDataException(inspected.Error!.Code);
        var baseline = inspected.Inspection!;
        if (!RenderTemplateEvidenceValidator.Matches(original.GetRawText(), baseline))
            throw new InvalidDataException("RENDER_TEMPLATE_EVIDENCE_INVALID");
        var now = DateTimeOffset.UtcNow;
        var attempt = Guid.NewGuid().ToString("N");
        var revisionId = "revision_" + attempt;
        var page = TemplatePage.Create("page_" + attempt, revisionId, 0, "普通页放置验证",
            TemplatePageContentType.NormalComposition, batchProbe ? TemplateGenerationMode.OncePerVariant : TemplateGenerationMode.OncePerProduct, null, true, templateId);
        var bindings = original.GetProperty("targets").EnumerateArray().Select(target =>
        {
            var expected = target.GetProperty("expected_layer");
            var layout = target.GetProperty("layout");
            var bounds = layout.GetProperty("safe_bounds");
            return FrameBinding.Create(target.GetProperty("frame_id").GetString()!, page.PageId,
                LayerPath.Create(target.GetProperty("layer_path").EnumerateArray().Select(value => value.GetInt32())),
                ExpectedLayerEvidence.Create(expected.GetProperty("layer_id").GetInt32(), expected.GetProperty("name_before").GetString()!,
                    expected.GetProperty("visible").GetBoolean(), expected.GetProperty("clipping").GetBoolean(),
                    expected.GetProperty("user_mask").GetBoolean(), expected.GetProperty("vector_mask").GetBoolean(),
                    expected.GetProperty("opacity").GetByte(), expected.GetProperty("blend_mode").GetString()!,
                    LayerStructureSignature.Create(expected.GetProperty("structure_signature").GetString()!)),
                target.GetProperty("superseded_layers").EnumerateArray().Select(old => SupersededLayerEvidence.Create(
                    LayerPath.Create(old.GetProperty("layer_path").EnumerateArray().Select(value => value.GetInt32())),
                    old.GetProperty("expected_clipping").GetBoolean(),
                    old.TryGetProperty("expected_layer_id", out var id) ? id.GetInt32() : null)),
                SelectionRequirement.Create("沿用已登记技术样本的商品框；缺主体定位时普通填充并标黄。"),
                LayoutIntent.Create(layout.GetProperty("intent").GetString()!, layout.GetProperty("scale").GetDouble(),
                    layout.GetProperty("offset").GetProperty("x").GetDouble(), layout.GetProperty("offset").GetProperty("y").GetDouble(),
                    TemplateRect.Create(bounds.GetProperty("left").GetDouble(), bounds.GetProperty("top").GetDouble(),
                        bounds.GetProperty("right").GetDouble(), bounds.GetProperty("bottom").GetDouble())));
        }).ToArray();
        var revision = TemplateRevision.Restore(revisionId, "group_" + attempt, 1, TemplateRevisionState.Committed,
            "candidate-template-snapshot.json", "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(bindings))), now, [page], bindings);
        var resource = new ImportedTemplateResource(templateId, revision.GroupId, revisionId, templateSource.OwnedRelativePath,
            baseline.SizeBytes, baseline.FileSha256, "sha256:" + baseline.StructureSignature.Replace("sha256:", "", StringComparison.Ordinal), baseline, now);
        var assets = new List<SourceAssetMetadata>();
        var styleId = "style_" + attempt;
        foreach (var source in candidate.Sources.Where(source => source.Purpose == "high_resolution_render_source"))
        {
            var metadata = (await new StreamingAssetMetadataReader().ReadAsync(paths.ResolveOwnedPath("inputs"),
                Path.GetRelativePath(paths.ResolveOwnedPath("inputs"), paths.ResolveOwnedPath(source.OwnedRelativePath)), default)).Metadata
                ?? throw new InvalidDataException("RENDER_PREPARATION_SOURCE_INVALID");
            var declared = original.GetProperty("resources").EnumerateArray().Single(value => value.GetProperty("resource_id").GetString() == source.ResourceId);
            if (metadata.Sha256 != declared.GetProperty("sha256").GetString()
                || metadata.Length != declared.GetProperty("byte_length").GetInt64())
                throw new InvalidDataException("RENDER_PREPARATION_SOURCE_CHANGED");
            assets.Add(SourceAssetMetadata.CreateReadable(source.ResourceId, styleId, Path.GetFileName(source.OwnedRelativePath),
                Path.GetFileName(source.OwnedRelativePath), metadata.Length, metadata.Sha256,
                metadata.PixelWidth, metadata.PixelHeight, metadata.Format, metadata.MimeType));
        }
        var secondStyleId = styleId + "_b";
        if (styleBoundaryProbe)
        {
            var originalAssets = assets.ToArray();
            assets.Clear();
            foreach (var (style, folder, suffix) in new[] { (styleId, "style-a", ""), (secondStyleId, "style-b", "_b") })
            {
                Directory.CreateDirectory(paths.ResolveOwnedPath("inputs/" + folder));
                foreach (var asset in originalAssets)
                {
                    var relative = folder + "/" + asset.FileName;
                    File.Copy(paths.ResolveOwnedPath("inputs/" + asset.SourceRelativePath), paths.ResolveOwnedPath("inputs/" + relative), overwrite: false);
                    assets.Add(SourceAssetMetadata.CreateReadable(asset.AssetId + suffix, style, relative, asset.FileName,
                        asset.Length, asset.Sha256, asset.PixelWidth, asset.PixelHeight, asset.Format, asset.MimeType));
                }
            }
        }
        var inputType = styleBoundaryProbe ? BatchInputType.StyleDirectoryBatch : BatchInputType.SingleStyle;
        var styles = assets.GroupBy(asset => asset.StyleId).Select((group, index) => StyleDraft.Create(group.Key,
            "受控验证" + (index + 1), styleBoundaryProbe ? index == 0 ? "style-a" : "style-b" : "", index,
            group.Select(asset => asset.AssetId))).ToArray();
        var batchId = "batch_" + attempt;
        var batch = BatchDraft.Create(batchId, paths.ResolveOwnedPath("inputs"), paths.ResolveOwnedPath("outputs"),
            BatchTemplateSnapshot.Create("snapshot_" + attempt, batchId, revision, [page.PageId], now),
            BatchSelectionMode.Automatic, original.GetProperty("output").GetProperty("editable_psd").GetProperty("enabled").GetBoolean(), now)
                .BeginScanning(now.AddTicks(1)).MarkScanReady(inputType,
                styles, assets,
                "candidate-source-manifest.json", "sha256:" + new string('0', 64), now.AddTicks(2));
        var selections = original.GetProperty("targets").EnumerateArray().Select(target => new AiConfirmedRenderSelection(
            target.GetProperty("frame_id").GetString()!, target.GetProperty("asset_resource_id").GetString()!, null, 1)).ToArray();
        await using var batchStore = batchProbe ? new SqliteDesktopStore(new(paths.ResolveOwnedPath("app"))) : null;
        if (batchStore is not null)
        {
            await batchStore.InitializeAsync();
            await batchStore.SaveGroupAsync(TemplateGroup.Create(revision.GroupId, "受控批次技术验证", now), default);
            await batchStore.SaveDraftRevisionAsync(TemplateRevision.CreateDraft(revisionId, revision.GroupId, 1, now, [page], bindings), default);
            await batchStore.CommitRevisionAsync(revision, null, default);
            await batchStore.SaveImportedAsync("import_" + attempt, resource, default);
            var scanning = BatchDraft.Create(batchId, batch.InputRoot, batch.OutputRoot, batch.TemplateSnapshot,
                batch.SelectionMode, batch.IncludeEditablePsd, now).BeginScanning(now.AddTicks(1));
            var files = new AtomicFileStore(paths);
            var manifests = new AssetManifestFileStore(files, new JsonSchemaContractValidator());
            var journals = new JsonInputCommitJournalStore(files, paths);
            var committed = await new SqliteBatchScanCommitter(batchStore, manifests, TimeProvider.System, journals).CommitAsync(
                new(scanning, inputType, batch.Styles, assets,
                    AssetManifestV1.Create(scanning, inputType, batch.Styles, assets,
                        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(assets))), now)), default);
            batch = committed.Draft ?? throw new InvalidDataException("RENDER_PREPARATION_INPUT_INVALID");
        }
        var variants = batchProbe ? Enumerable.Range(1, styleBoundaryProbe ? 2 : 3).Select(index => new AiOutputVariantPlan("candidate_" + index,
            "同图身份验证" + index, index == 1, true, selections.ToDictionary(item => item.FrameId, item => item.AssetId), [], [])).ToArray() : null;
        var renderStyles = new List<AiRenderStyleInput> { new(styleId, selections, VariantPlans: variants) };
        if (styleBoundaryProbe)
            renderStyles.Add(new(secondStyleId, selections.Select(item => item with { AssetId = item.AssetId + "_b" }).ToArray(),
                VariantPlans: variants!.Select(item => item with
                { RequirementAssets = item.RequirementAssets.ToDictionary(pair => pair.Key, pair => pair.Value + "_b") }).ToArray()));
        var preparation = await new AiRenderPlanFactory(new TemplateRenderPlanBuilder(new RenderPlanValidator(new JsonSchemaContractValidator())),
            new UnavailablePhotoshopAvailabilityPort()).PrepareAsync(new(batch, revision, [resource], renderStyles, now), default);
        if (batchProbe)
        {
            if (preparation.Pages.Count != (styleBoundaryProbe ? 4 : 3) || preparation.Pages.Any(item => !item.HasPlan))
                throw new InvalidDataException("RENDER_PREPARATION_INPUT_INVALID");
            // Deliberate third-page source-dimension fault; real source bytes are unchanged.
            // These variant IDs exercise isolation only and are not claimed as AI color evidence.
            var pages = preparation.Pages.ToArray();
            if (!styleBoundaryProbe)
            {
                var faulty = JsonNode.Parse(pages[2].RenderPlanJson!)!;
                foreach (var target in faulty["targets"]!.AsArray())
                {
                    var placement = target!["layout"]!["placement"]!;
                    placement["source_width"] = placement["source_width"]!.GetValue<int>() * 2;
                    placement["source_height"] = placement["source_height"]!.GetValue<int>() * 2;
                }
                faulty["plan_digest"] = RenderPlanDigest.Compute(faulty.ToJsonString());
                pages[2] = pages[2] with { RenderPlanJson = faulty.ToJsonString(), PlanDigest = faulty["plan_digest"]!.GetValue<string>() };
            }
            await using var coordinator = new RenderBatchCoordinator(batchStore!, new PreparationOnlySessionFactory(),
                new RenderPlanValidator(new JsonSchemaContractValidator()), new Desktop.Application.Tasks.TaskCoordinator(batchStore!), TimeProvider.System);
            await coordinator.PrepareAsync(preparation with { Pages = pages }, default);
            await using var batchEvidence = new FileStream(paths.ResolveOwnedPath("batch-preparation.json"), FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(batchEvidence, new { batch_id = batchId, same_source_identity_probe = true,
                expected_failed_variant = styleBoundaryProbe ? null : "candidate_3", style_boundary_probe = styleBoundaryProbe,
                plans = pages.Select(item => new { item.StyleId, item.PlanDigest, item.OutputVariantId }) });
            batchEvidence.Flush(true);
            return;
        }
        var preparedPage = preparation.Pages.Single();
        if (!preparedPage.HasPlan) throw new InvalidDataException(preparedPage.ReasonCode);
        using var plan = JsonDocument.Parse(preparedPage.RenderPlanJson!);
        async Task Create(string relative, object value)
        {
            await using var output = new FileStream(paths.ResolveOwnedPath(relative), FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(output, value, BridgeProtocol.JsonOptions);
            output.Flush(true);
        }
        await Create("candidate-template-snapshot.json", new { candidate = true, revision, resource });
        await Create("candidate-source-manifest.json", new { candidate = true, assets });
        await Create("ordinary-preparation-evidence.json", new { candidate = true, producer = nameof(AiRenderPlanFactory),
            placement_version = TemplatePlacementResolver.Version, preparedPage.ReasonCode, preparedPage.PlanDigest,
            original_plan_digest = original.GetProperty("plan_digest").GetString(),
            subject_geometry_available = false, historical_plan_modified = false });
        await Create("page-input.json", new ProbePage(plan.RootElement.Clone(), candidate.Sources, baseline));
    }

    internal sealed class PreparationOnlySessionFactory : IRenderBatchSessionFactory
    {
        public Task<IRenderBatchSession> OpenAsync(RenderBatchJournal journal, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("RENDER_PREPARATION_ONLY");
    }
}
