using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Files;
using Desktop.Application.Inputs;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
using Desktop.Application.Templates;
using Desktop.Contracts.Validation;
using Desktop.Domain.Inputs;
using Desktop.Domain.SizeTables;
using Desktop.Domain.Templates;
using NSubstitute;

namespace Desktop.Application.Tests;

public sealed class SizeTablePageRepairPreparerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("style_a", null, false)]
    [InlineData("style_a", "red", true)]
    [InlineData("style_b", "blue", false)]
    public async Task ChangedValuePreparesOnlyExactPageAndPreservesFrozenFacts(string styleId, string? variant, bool psd)
    {
        var f = new Fixture(styleId, variant, psd);
        var sibling = new Fixture(styleId == "style_a" ? "style_b" : "style_a", "sibling", psd);
        var journal = f.Journal with { Pages = [f.Page, sibling.Page] };
        foreach (var (path, bytes) in sibling.Files.Content) f.Files.Content[path] = bytes.ToArray();
        var oldPlan = f.Page.PlanJson!;
        var oldResources = f.Files.Content.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);

        var candidate = await f.Preparer.PrepareAsync(f.Command(journal: journal), journal, f.Page, Now.AddMinutes(1), default);

        var table = SizeTableRepairData.Deserialize(candidate.Origin.TableJson);
        Assert.Equal("81", table.Rows[0].Cells[1].SourceText);
        Assert.Equal("/", table.Rows[0].Cells[2].SourceText);
        Assert.Equal(f.Table.Revision + 1, table.Revision);
        Assert.Equal(f.Table.Source, table.Source);
        Assert.True(SizeTableRepairData.MatchesPlan(table, candidate.PlanJson));
        Assert.True(f.Validator.Validate(candidate.PlanJson).IsValid);
        Assert.False(RenderPlanRegeneration.PreservesFacts(oldPlan, candidate.PlanJson));
        RenderPlanSizeTableRepair.Validate(oldPlan, candidate.PlanJson, candidate.Origin);
        using var before = JsonDocument.Parse(oldPlan);
        using var after = JsonDocument.Parse(candidate.PlanJson);
        foreach (var field in new[] { "correlation_id", "batch_id", "product_id", "template_revision_id", "document", "targets", "required_capabilities" })
            Assert.True(JsonElement.DeepEquals(before.RootElement.GetProperty(field), after.RootElement.GetProperty(field)));
        Assert.True(JsonElement.DeepEquals(before.RootElement.GetProperty("resources")[0], after.RootElement.GetProperty("resources")[0]));
        Assert.True(JsonElement.DeepEquals(before.RootElement.GetProperty("output").GetProperty("jpg"), after.RootElement.GetProperty("output").GetProperty("jpg")));
        Assert.True(JsonElement.DeepEquals(before.RootElement.GetProperty("output").GetProperty("editable_psd"), after.RootElement.GetProperty("output").GetProperty("editable_psd")));
        Assert.Equal(oldPlan, f.Page.PlanJson);
        Assert.Single(f.Journal.Pages);
        Assert.Same(sibling.Page, journal.Pages[1]);
        Assert.Equal(styleId, Assert.Single(f.Rasterizer.Requests).Table.StyleId);
        Assert.Single(f.Files.Writes);
        Assert.StartsWith($"batches/batch/size-tables/{styleId}/size_page/resources/", f.Files.Writes[0], StringComparison.Ordinal);
        foreach (var (path, bytes) in oldResources) Assert.Equal(bytes, f.Files.Content[path]);
        await f.Tables.DidNotReceiveWithAnyArgs().SaveAsync(default!, default, default);
        await f.Batches.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await f.Sources.Received(2).ReadAsync("input-owned-by-port", styleId == "style_a" ? "a/size.png" : "b/size.png", Arg.Any<CancellationToken>());
        await f.Sources.DidNotReceive().ReadAsync(Arg.Any<string>(), styleId == "style_a" ? "b/size.png" : "a/size.png", Arg.Any<CancellationToken>());
        Assert.Equal("80", f.Table.Rows[0].Cells[1].SourceText);
        Assert.Equal(f.Command().Target.BasePlanDigest, candidate.Origin.BasePlanDigest);
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("style")]
    [InlineData("page")]
    [InlineData("variant")]
    [InlineData("journal")]
    [InlineData("attempt")]
    [InlineData("plan")]
    [InlineData("table-revision")]
    [InlineData("table-digest")]
    public async Task EveryCommandBaselineIsCheckedBeforeRasterization(string field)
    {
        var f = new Fixture(variant: "red");
        var command = f.Command();
        var target = command.Target;
        target = field switch
        {
            "batch" => target with { BatchId = "other" }, "style" => target with { StyleId = "style_b" },
            "page" => target with { PageId = "other" }, "variant" => target with { OutputVariantId = null },
            "journal" => target with { ExpectedJournalRevision = target.ExpectedJournalRevision + 1 },
            "attempt" => target with { ExpectedAttempt = 2 }, "plan" => target with { BasePlanDigest = "sha256:" + new string('f', 64) },
            "table-revision" => target with { ExpectedTableRevision = target.ExpectedTableRevision + 1 },
            _ => target with { ExpectedTableDataDigest = "sha256:" + new string('f', 64) }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Preparer.PrepareAsync(command with { Target = target }, f.Journal, f.Page, Now.AddMinutes(1), default));
        Assert.Empty(f.Rasterizer.Requests);
        Assert.Empty(f.Files.Writes);
    }

    [Theory]
    [InlineData("source-hash")]
    [InlineData("source-length")]
    [InlineData("source-style")]
    [InlineData("current-values")]
    [InlineData("missing-current")]
    [InlineData("template-file")]
    [InlineData("old-png")]
    [InlineData("calibration")]
    public async Task SourceCurrentTemplateAndExistingRasterMustCloseBeforeEditing(string fault)
    {
        var f = new Fixture();
        switch (fault)
        {
            case "source-hash": f.SourceMetadata = f.SourceMetadata with { Sha256 = new string('f', 64) }; break;
            case "source-length": f.SourceMetadata = f.SourceMetadata with { Length = 999 }; break;
            case "source-style":
                f.Current = Table("style_b"); break;
            case "current-values": f.Current = SizeTableRepairData.ApplyDraft(f.Table, f.Command().Draft, Now.AddSeconds(1)); break;
            case "missing-current": f.Current = null; break;
            case "template-file": f.Files.TemplateHash = new string('f', 64); break;
            case "old-png": f.Files.Content[f.BaseRaster.OwnedRelativePath][^1] ^= 1; break;
            case "calibration":
                f.Templates.GetRevisionAsync(f.Revision.RevisionId, Arg.Any<CancellationToken>()).Returns(
                    TemplateRevision.Restore(f.Revision.RevisionId, f.Revision.GroupId, 1, TemplateRevisionState.Committed,
                        f.Revision.ManifestRef, f.Revision.RuleDigest, Now,
                        [TemplatePage.Create("size_page", f.Revision.RevisionId, 0, "size", TemplatePageContentType.DynamicSizeTable,
                            TemplateGenerationMode.OncePerProduct, null, true, "template_resource")], []));
                break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Preparer.LoadBaseAsync(f.Journal, f.Page, default));
        Assert.Empty(f.Rasterizer.Requests);
        Assert.Empty(f.Files.Writes);
    }

    [Theory]
    [InlineData("unknown-sibling")]
    [InlineData("old-attempt")]
    [InlineData("foreign-page")]
    [InlineData("wrong-plan-variant")]
    [InlineData("changed-placement")]
    [InlineData("legacy-insertion-missing")]
    public async Task JournalIdentityAndFullFrozenPlanCannotBeBypassed(string fault)
    {
        var f = new Fixture(variant: "red");
        var journal = f.Journal;
        var page = f.Page;
        if (fault == "unknown-sibling") journal = journal with { Pages = [page, page with { OutputVariantId = "blue", Status = RenderBatchPageStatus.Unknown }] };
        else if (fault == "old-attempt") journal = journal with { Pages = [page, page with { Attempt = 2 }] };
        else if (fault == "foreign-page") page = page with { StyleId = "style_b" };
        else
        {
            var plan = JsonNode.Parse(page.PlanJson!)!;
            if (fault == "wrong-plan-variant") plan["document"]!["output_variant_id"] = "blue";
            else if (fault == "legacy-insertion-missing")
            {
                plan["size_table"]!.AsObject().Remove("insertion");
                var capabilities = plan["required_capabilities"]!.AsArray();
                capabilities.Remove(capabilities.Single(item => item!.GetValue<string>() == RenderPlanPlacementMigration.SizeInsertionCapability));
            }
            else plan["size_table"]!["insertion"]!["placement"]!["left"] = 11;
            plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
            page = page with { PlanJson = plan.ToJsonString() };
            journal = journal with { Pages = [page] };
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Preparer.LoadBaseAsync(journal, page, default));
        Assert.Empty(f.Files.Writes);
    }

    [Fact]
    public async Task RegenerationAfterRepairUsesLatestSameVariantSnapshotNotCurrentOrAnotherVariant()
    {
        var f = new Fixture(variant: "red");
        var repaired = await f.Preparer.PrepareAsync(f.Command(), f.Journal, f.Page, Now.AddMinutes(1), default);
        var repairPage = f.Page with { Attempt = 2, PlanJson = repaired.PlanJson, SizeTableRepair = repaired.Origin };
        var regenerated = repairPage with { Attempt = 3, SizeTableRepair = null,
            PlanJson = RenderPlanRegeneration.Create(repaired.PlanJson, Now.AddMinutes(2)) };
        var foreign = repairPage with { OutputVariantId = "blue", Attempt = 4,
            SizeTableRepair = repaired.Origin with { TableJson = "invalid foreign variant snapshot" } };
        var journal = f.Journal with { Version = 3, Revision = 5, Pages = [f.Page, repairPage, regenerated, foreign] };
        f.Current = null; // An old/missing current cannot overwrite the page's journal revision.
        f.Tables.ClearReceivedCalls();

        var baseline = await f.Preparer.LoadBaseAsync(journal, regenerated, default);
        Assert.Equal("81", baseline.Rows[0].Cells[1].SourceText);
        Assert.Equal(1, baseline.Revision);
        await f.Tables.DidNotReceiveWithAnyArgs().LoadAsync(default!, default!, default!, default);
        var next = await f.Preparer.PrepareAsync(f.Command(baseline, regenerated, journal, "82"), journal, regenerated, Now.AddMinutes(3), default);
        Assert.Equal("82", SizeTableRepairData.Deserialize(next.Origin.TableJson).Rows[0].Cells[1].SourceText);
        Assert.Equal(2, SizeTableRepairData.Deserialize(next.Origin.TableJson).Revision);
        var latestRepair = regenerated with { Attempt = 4, PlanJson = next.PlanJson, SizeTableRepair = next.Origin };
        var latestRegeneration = latestRepair with { Attempt = 5, SizeTableRepair = null,
            PlanJson = RenderPlanRegeneration.Create(next.PlanJson, Now.AddMinutes(4)) };
        var latestJournal = journal with { Revision = 7, Pages = [.. journal.Pages, latestRepair, latestRegeneration] };
        Assert.Equal("82", (await f.Preparer.LoadBaseAsync(latestJournal, latestRegeneration, default)).Rows[0].Cells[1].SourceText);
        await f.Tables.DidNotReceiveWithAnyArgs().SaveAsync(default!, default, default);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed-bytes")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-dimensions")]
    public async Task CandidateResourceMustBeReadBackFromExactOwnedAddress(string fault)
    {
        var f = new Fixture();
        f.Rasterizer.Fault = fault;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Preparer.PrepareAsync(f.Command(), f.Journal, f.Page, Now.AddMinutes(1), default));
        await f.Tables.DidNotReceiveWithAnyArgs().SaveAsync(default!, default, default);
        Assert.Equal(f.BaseRaster.Sha256, Hash(f.Files.Content[f.BaseRaster.OwnedRelativePath]));
    }

    [Fact]
    public async Task NoOpCannotMasqueradeAsRepairAndSourceChangeDuringRasterizationRejectsCandidate()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Preparer.PrepareAsync(f.Command(value: "80"), f.Journal, f.Page, Now.AddMinutes(1), default));
        Assert.Empty(f.Rasterizer.Requests);
        f.Rasterizer.AfterWrite = () => f.SourceMetadata = f.SourceMetadata with { Sha256 = new string('f', 64) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Preparer.PrepareAsync(f.Command(), f.Journal, f.Page, Now.AddMinutes(1), default));
        Assert.Single(f.Journal.Pages);
        await f.Tables.DidNotReceiveWithAnyArgs().SaveAsync(default!, default, default);
    }

    private static SizeTable Table(string styleId) => SizeTable.FromCandidate("table_" + styleId, "batch", styleId, "size_page",
        SizeTableCandidate.Create("candidate", SizeTableSource.Create("source_" + styleId, new string('e', 64)),
            0.99m, ["尺码", "腰围", "备注"], [["L", "80", "/"]]), Now);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class Fixture
    {
        public IBatchRepository Batches { get; } = Substitute.For<IBatchRepository>();
        public ITemplateRepository Templates { get; } = Substitute.For<ITemplateRepository>();
        public ITemplateResourceRepository Resources { get; } = Substitute.For<ITemplateResourceRepository>();
        public ISizeTableRepository Tables { get; } = Substitute.For<ISizeTableRepository>();
        public IAssetMetadataReader Sources { get; } = Substitute.For<IAssetMetadataReader>();
        public RenderPlanValidator Validator { get; } = new(new JsonSchemaContractValidator());
        public MemoryFiles Files { get; } = new();
        public Rasterizer Rasterizer { get; }
        public SizeTable Table { get; }
        public SizeTable? Current { get; set; }
        public AssetFileMetadata SourceMetadata { get; set; } = new(128, new string('e', 64), 100, 200, SourceImageFormat.Png, "image/png");
        public TemplateRevision Revision { get; }
        public SizeTableRasterizedResource BaseRaster { get; }
        public RenderBatchPage Page { get; }
        public RenderBatchJournal Journal { get; }
        public SizeTablePageRepairPreparer Preparer { get; }

        public Fixture(string styleId = "style_a", string? variant = null, bool psd = false)
        {
            var inspection = SizeTableInsertionTests.Fixture().Baseline;
            var calibration = SizeTableCalibrationResolverTests.Calibration();
            var page = TemplatePage.Create("size_page", "revision", 0, "size", TemplatePageContentType.DynamicSizeTable,
                TemplateGenerationMode.OncePerProduct, null, true, "template_resource", sizeTableCalibration: calibration);
            Revision = TemplateRevision.Restore("revision", "group", 1, TemplateRevisionState.Committed,
                "templates/revision.json", "sha256:" + new string('a', 64), Now, [page], []);
            var resource = new ImportedTemplateResource("template_resource", "group", "revision", "templates/frozen.psd",
                inspection.SizeBytes, inspection.FileSha256, inspection.StructureSignature, inspection, Now);
            Files.TemplateHash = resource.Sha256;
            var snapshot = BatchTemplateSnapshot.Create("snapshot", "batch", Revision, [page.PageId], Now);
            var batch = BatchDraft.Create("batch", "input-owned-by-port", "unused-output", snapshot, BatchSelectionMode.Automatic, psd, Now)
                .BeginScanning(Now).MarkScanReady(BatchInputType.StyleDirectoryBatch,
                    [StyleDraft.Create("style_a", "a", "a", 0, ["source_style_a"]), StyleDraft.Create("style_b", "b", "b", 1, ["source_style_b"])],
                    [Asset("style_a", "a"), Asset("style_b", "b")], "batches/batch/manifest.json", "sha256:" + new string('a', 64), Now);
            Table = SizeTablePageRepairPreparerTests.Table(styleId);
            Current = Table;
            Rasterizer = new(Files);
            var style = SizeTableCalibrationResolver.RasterStyle(calibration);
            BaseRaster = Rasterizer.Create(Table, style);
            var builder = new SizeTableRenderPlanBuilder(Validator);
            var plan = builder.Build(Revision, page, resource, new("plan_base", "key_base", "correlation_original", "batch", styleId,
                "artifact_base", "size.jpg", psd, psd ? "size.psd" : null, Now, Table, BaseRaster,
                CalibratedInsertion: SizeTableCalibrationResolver.Resolve(page, BaseRaster), OutputVariantId: variant));
            Page = new(styleId, "size_page", variant, 1, plan, RenderBatchPageStatus.Failed, "FAILED", PreparationReasonCode: "READY");
            Journal = new(1, "batch", 1, RenderBatchStatus.CompletedWithIssues, false, false, [Page]);
            Batches.GetAsync("batch", Arg.Any<CancellationToken>()).Returns(batch);
            Templates.GetRevisionAsync("revision", Arg.Any<CancellationToken>()).Returns(Revision);
            Resources.GetByResourceIdAsync("template_resource", Arg.Any<CancellationToken>()).Returns(resource);
            Tables.LoadAsync("batch", styleId, "size_page", Arg.Any<CancellationToken>())
                .Returns(_ => Current is null ? SizeTableLoadResult.NotFound() : SizeTableLoadResult.Loaded(Current));
            Sources.ReadAsync(batch.InputRoot, styleId == "style_a" ? "a/size.png" : "b/size.png", Arg.Any<CancellationToken>())
                .Returns(_ => AssetMetadataReadResult.Success(SourceMetadata));
            Preparer = new(Batches, Templates, Resources, Tables, Sources, Rasterizer, builder, Validator, Files);
        }

        public SizeTablePageRepairCommand Command(SizeTable? table = null, RenderBatchPage? page = null,
            RenderBatchJournal? journal = null, string value = "81")
        {
            table ??= Table; page ??= Page; journal ??= Journal;
            using var plan = JsonDocument.Parse(page.PlanJson!);
            var editor = SizeTableRepairData.ToEditorDocument(table);
            return new("request_" + value, new(journal.BatchId, page.StyleId, page.PageId, page.OutputVariantId, journal.Revision,
                page.Attempt, plan.RootElement.GetProperty("plan_digest").GetString()!, table.Revision, table.DataDigest),
                new(editor.Columns, [new(editor.Rows[0].RowId, ["L", value, "/"])]));
        }

        private static SourceAssetMetadata Asset(string style, string directory) => SourceAssetMetadata.CreateReadable(
            "source_" + style, style, directory + "/size.png", "size.png", 128, new string('e', 64), 100, 200, SourceImageFormat.Png, "image/png");
    }

    private sealed class MemoryFiles : IFileStore
    {
        public Dictionary<string, byte[]> Content { get; } = new(StringComparer.Ordinal);
        public List<string> Writes { get; } = [];
        public string TemplateHash { get; set; } = "";
        public Task WriteAtomicallyAsync(string relativePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        { Writes.Add(relativePath); Content[relativePath] = content.ToArray(); return Task.CompletedTask; }
        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken) => Task.FromResult(Content.GetValueOrDefault(relativePath)?.ToArray());
        public Task<string?> ComputeSha256Async(string relativePath, CancellationToken cancellationToken) => Task.FromResult<string?>(
            relativePath == "templates/frozen.psd" ? TemplateHash : Content.TryGetValue(relativePath, out var bytes) ? Hash(bytes) : null);
        public Task<bool> DeleteAsync(string relativePath, CancellationToken cancellationToken) => throw new InvalidOperationException("Repair must not delete resources");
    }

    private sealed class Rasterizer(MemoryFiles files) : ISizeTableRasterizer
    {
        public List<SizeTableRasterizationRequest> Requests { get; } = [];
        public string? Fault { get; set; }
        public Action? AfterWrite { get; set; }

        // A header/payload fixture for Application's byte/identity checks; real PNG encoding is tested in Infrastructure.
        public SizeTableRasterizedResource Create(SizeTable table, SizeTableRasterStyle style)
        {
            var payload = Encoding.UTF8.GetBytes(table.DataDigest);
            var bytes = new byte[24 + payload.Length];
            bytes[0] = 137; "PNG\r\n\x1a\n"u8.CopyTo(bytes.AsSpan(1)); "IHDR"u8.CopyTo(bytes.AsSpan(12));
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), style.TargetWidth);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), 113);
            payload.CopyTo(bytes, 24);
            var id = "size_raster_" + Hash(payload)[..24];
            var path = $"batches/{table.BatchId}/size-tables/{table.StyleId}/{table.PageId}/resources/{id}.png";
            files.Content[path] = bytes;
            return new(id, "deterministic_size_table", "image/png", Hash(bytes), bytes.Length, style.TargetWidth, 113,
                table.DataDigest, style.StyleVersion, style.LayoutVersion, path);
        }

        public Task<SizeTableRasterizedResource> RasterizeAsync(SizeTableRasterizationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var raster = Create(request.Table, request.Style);
            files.Writes.Add(raster.OwnedRelativePath);
            if (Fault == "missing") files.Content.Remove(raster.OwnedRelativePath);
            if (Fault == "changed-bytes") files.Content[raster.OwnedRelativePath][^1] ^= 1;
            if (Fault == "wrong-owner") raster = raster with { OwnedRelativePath = "batches/other/resource.png" };
            if (Fault == "wrong-dimensions") raster = raster with { PixelHeight = 114 };
            AfterWrite?.Invoke();
            return Task.FromResult(raster);
        }
    }
}
