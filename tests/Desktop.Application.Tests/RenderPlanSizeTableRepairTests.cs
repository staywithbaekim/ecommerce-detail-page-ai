using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
using Desktop.Contracts.Validation;
using Desktop.Domain.SizeTables;

namespace Desktop.Application.Tests;

public sealed class RenderPlanSizeTableRepairTests
{
    private static readonly RenderPlanValidator Validator = new(new JsonSchemaContractValidator());
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Theory]
    [InlineData("cell")]
    [InlineData("header")]
    [InlineData("shape")]
    public void OnlyTheConfirmedSizeRevisionResourceAndDeterministicPlacementCanChange(string change)
    {
        var fixture = Fixture(change);
        var original = fixture.Before;
        RenderPlanSizeTableRepair.ValidateOrigin(fixture.Origin);
        RenderPlanSizeTableRepair.Validate(fixture.Before, fixture.After, fixture.Origin);
        Assert.True(SizeTableRepairData.MatchesPlan(fixture.BaseTable, fixture.Before));
        Assert.True(SizeTableRepairData.MatchesPlan(fixture.Table, fixture.After));
        Assert.False(SizeTableRepairData.MatchesPlan(fixture.BaseTable, fixture.After));
        Assert.False(RenderPlanRegeneration.PreservesFacts(fixture.Before, fixture.After));
        Assert.Equal(original, fixture.Before);
        Assert.Equal("68", fixture.BaseTable.Rows[0].Cells[1].SourceText);
    }

    [Fact]
    public void OriginalPlanRegenerationStillCannotModifySizeFacts()
    {
        var fixture = Fixture();
        var replay = RenderPlanRegeneration.Create(fixture.Before, SizeTableRepairDataTests.Now.AddMinutes(2));
        Assert.True(RenderPlanRegeneration.PreservesFacts(fixture.Before, replay));
        var changed = JsonNode.Parse(replay)!;
        changed["size_table"]!["rows"]![0]!["cells"]!["col_002"] = "69";
        Assert.False(RenderPlanRegeneration.PreservesFacts(fixture.Before, Seal(changed)));
        Assert.Throws<InvalidDataException>(() => RenderPlanSizeTableRepair.Validate(fixture.Before, replay, fixture.Origin));
    }

    [Theory]
    [InlineData("correlation")]
    [InlineData("batch")]
    [InlineData("style")]
    [InlineData("page")]
    [InlineData("table")]
    [InlineData("template_revision")]
    [InlineData("canvas")]
    [InlineData("capabilities")]
    [InlineData("jpg_name")]
    [InlineData("psd_intent")]
    [InlineData("psd_name")]
    [InlineData("variant")]
    [InlineData("template_hash")]
    [InlineData("template_length")]
    [InlineData("another_resource")]
    [InlineData("style_version")]
    [InlineData("layout_version")]
    [InlineData("anchor")]
    [InlineData("hidden_layers")]
    [InlineData("layer_name")]
    [InlineData("safe_bounds")]
    [InlineData("unknown_metadata")]
    public void RepairWhitelistRejectsChangesOutsideTheSizeContent(string mutation)
    {
        var fixture = Fixture();
        var plan = JsonNode.Parse(fixture.After)!;
        var size = plan["size_table"]!;
        switch (mutation)
        {
            case "correlation": plan["correlation_id"] = "corr_other"; break;
            case "batch": plan["batch_id"] = "batch_other"; break;
            case "style": plan["product_id"] = "product_other"; break;
            case "page": plan["document"]!["page_id"] = "page_other"; break;
            case "table": size["size_table_id"] = "size_other"; break;
            case "template_revision": plan["template_revision_id"] = "tplrev_other"; break;
            case "canvas": plan["document"]!["canvas"]!["height"] = 1300; break;
            case "capabilities": plan["required_capabilities"]!.AsArray().Add("test/other-v1"); break;
            case "jpg_name": plan["output"]!["jpg"]!["file_name"] = "other.jpg"; break;
            case "psd_intent": plan["output"]!["editable_psd"]!["enabled"] = false;
                plan["output"]!["editable_psd"]!["file_name"] = null; break;
            case "psd_name": plan["output"]!["editable_psd"]!["file_name"] = "other.psd"; break;
            case "variant": plan["document"]!["output_variant_id"] = "color_red";
                plan["required_capabilities"]!.AsArray().Add(RenderPlanVariantIdentity.Capability); break;
            case "template_hash": plan["resources"]![0]!["sha256"] = new string('e', 64); break;
            case "template_length": plan["resources"]![0]!["byte_length"] = 1234; break;
            case "another_resource": plan["resources"]!.AsArray().Add(plan["resources"]![0]!.DeepClone()); break;
            case "style_version": size["style_version"] = "style_other"; break;
            case "layout_version": size["layout_version"] = "layout_other"; break;
            case "anchor": size["insertion"]!["above_layer"]!["layer_id"] = 99; break;
            case "hidden_layers": size["insertion"]!["hidden_layers"] = new JsonArray(); break;
            case "layer_name": size["insertion"]!["layer_name"] = "unapproved_name"; break;
            case "safe_bounds": size["insertion"]!["safe_bounds"]!["bottom"] = 1201; break;
            case "unknown_metadata": plan["repair_metadata"] = "not part of the contract"; break;
        }
        Assert.Throws<InvalidDataException>(() => RenderPlanSizeTableRepair.Validate(fixture.Before, Seal(plan), fixture.Origin));
    }

    [Theory]
    [InlineData("plan_id")]
    [InlineData("idempotency_key")]
    [InlineData("artifact")]
    [InlineData("artifact_case_alias")]
    [InlineData("resource_alias")]
    [InlineData("resource_case_alias")]
    [InlineData("declared_digest")]
    public void NewExecutionAndResourceIdentitiesCannotAliasTheFrozenOriginal(string mutation)
    {
        var fixture = Fixture();
        var before = JsonNode.Parse(fixture.Before)!;
        var after = JsonNode.Parse(fixture.After)!;
        switch (mutation)
        {
            case "plan_id": case "idempotency_key": after[mutation] = before[mutation]!.DeepClone(); break;
            case "artifact": after["output"]!["artifact_set_id"] = before["output"]!["artifact_set_id"]!.DeepClone(); break;
            case "artifact_case_alias": after["output"]!["artifact_set_id"] = before["output"]!["artifact_set_id"]!.GetValue<string>().ToUpperInvariant(); break;
            case "resource_alias": case "resource_case_alias":
                var id = before["resources"]![1]!["resource_id"]!.GetValue<string>();
                if (mutation == "resource_case_alias") id = id.ToUpperInvariant();
                after["resources"]![1]!["resource_id"] = id; after["size_table"]!["render_resource_id"] = id; break;
        }
        var json = Seal(after);
        if (mutation == "declared_digest")
        {
            after["plan_digest"] = before["plan_digest"]!.DeepClone();
            json = after.ToJsonString();
        }
        Assert.Throws<InvalidDataException>(() => RenderPlanSizeTableRepair.Validate(fixture.Before, json, fixture.Origin));
    }

    [Theory]
    [InlineData("off_center")]
    [InlineData("too_small")]
    [InlineData("stretched")]
    [InlineData("wrong_source_size")]
    [InlineData("subpixel_move")]
    [InlineData("clipping")]
    public void NewPlacementMustBeExactlyContainAndCenterNotJustInsideSafeBounds(string mutation)
    {
        var fixture = Fixture();
        var after = JsonNode.Parse(fixture.After)!;
        var placement = after["size_table"]!["insertion"]!["placement"]!;
        switch (mutation)
        {
            case "off_center": placement["top"] = 418.5m; break;
            case "too_small": placement["width"] = 700; placement["height"] = 350;
                placement["left"] = 45; placement["top"] = 435; break;
            case "stretched": placement["height"] = 400; break;
            case "wrong_source_size": placement["source_height"] = 400; break;
            case "subpixel_move": placement["top"] = 417.5000000001m; break;
            case "clipping": placement["clipping"] = "none"; break;
        }
        var json = Seal(after);
        if (mutation is "off_center" or "too_small" or "subpixel_move") Assert.True(Validator.Validate(json).IsValid);
        Assert.Throws<InvalidDataException>(() => RenderPlanSizeTableRepair.Validate(fixture.Before, json, fixture.Origin));
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("style")]
    [InlineData("page")]
    [InlineData("table")]
    [InlineData("header")]
    [InlineData("column_id")]
    [InlineData("column_index")]
    [InlineData("row_id")]
    [InlineData("row_index")]
    [InlineData("cell")]
    [InlineData("missing_cell")]
    [InlineData("extra_cell")]
    [InlineData("digest")]
    public void MatchesPlanRequiresAllIdentitiesAndEveryCellNotOnlyTheClaimedDataDigest(string mutation)
    {
        var fixture = Fixture();
        var plan = JsonNode.Parse(fixture.Before)!;
        var table = plan["size_table"]!;
        switch (mutation)
        {
            case "batch": plan["batch_id"] = "other_batch"; break;
            case "style": plan["product_id"] = "other_style"; break;
            case "page": plan["document"]!["page_id"] = "other_page"; break;
            case "table": table["size_table_id"] = "other_table"; break;
            case "header": table["columns"]![1]!["label"] = "other_header"; break;
            case "column_id": table["columns"]![1]!["column_id"] = "other_column"; break;
            case "column_index": table["columns"]![1]!["index"] = 0; break;
            case "row_id": table["rows"]![0]!["row_id"] = "other_row"; break;
            case "row_index": table["rows"]![0]!["index"] = 1; break;
            case "cell": table["rows"]![0]!["cells"]!["col_002"] = "69"; break;
            case "missing_cell": table["rows"]![0]!["cells"]!.AsObject().Remove("col_002"); break;
            case "extra_cell": table["rows"]![0]!["cells"]!["unexpected"] = "69"; break;
            case "digest": table["structured_data_digest"] = "sha256:" + new string('f', 64); break;
        }
        var json = Seal(plan);
        Assert.True(Validator.Validate(json).IsValid);
        Assert.False(SizeTableRepairData.MatchesPlan(fixture.BaseTable, json));
        var origin = fixture.Origin with { BasePlanDigest = JsonNode.Parse(json)!["plan_digest"]!.GetValue<string>() };
        Assert.Throws<InvalidDataException>(() => RenderPlanSizeTableRepair.Validate(json, fixture.After, origin));
    }

    [Theory]
    [InlineData("table")]
    [InlineData("batch")]
    [InlineData("style")]
    [InlineData("page")]
    [InlineData("candidate")]
    [InlineData("source_asset")]
    [InlineData("source_hash")]
    [InlineData("created_at")]
    public void BothValidSnapshotsMustRetainTheirOriginalIdentityAndProvenance(string mutation)
    {
        var fixture = Fixture();
        var table = fixture.Table;
        var candidate = SizeTableCandidate.Create(mutation == "candidate" ? "other_candidate" : table.CandidateId,
            SizeTableSource.Create(mutation == "source_asset" ? "other_asset" : table.Source.AssetId,
                mutation == "source_hash" ? new string('e', 64) : table.Source.ContentDigest), 1m,
            table.Columns.Select(column => column.SourceText), table.Rows.Select(row => row.Cells.Select(cell => cell.SourceText)));
        var other = SizeTable.FromCandidate(mutation == "table" ? "other_table" : table.TableId,
            mutation == "batch" ? "other_batch" : table.BatchId, mutation == "style" ? "other_style" : table.StyleId,
            mutation == "page" ? "other_page" : table.PageId, candidate,
            mutation == "created_at" ? table.CreatedAt.AddTicks(1) : table.CreatedAt);
        other = other.Edit(0, other.Columns, other.Rows, table.UpdatedAt);
        Assert.True(other.CanSave);
        Assert.Throws<InvalidDataException>(() => RenderPlanSizeTableRepair.ValidateOrigin(fixture.Origin with
        { TableJson = SizeTableRepairData.Serialize(other) }));
    }

    [Theory]
    [InlineData("null_origin")]
    [InlineData("request_id")]
    [InlineData("request_digest")]
    [InlineData("base_digest_format")]
    [InlineData("base_digest_mismatch")]
    [InlineData("journal_revision")]
    [InlineData("journal_overflow")]
    [InlineData("attempt")]
    [InlineData("attempt_overflow")]
    [InlineData("corrupt_base")]
    [InlineData("corrupt_after")]
    [InlineData("unchanged_revision")]
    [InlineData("revision_only")]
    [InlineData("skipped_revision")]
    [InlineData("backwards_update")]
    public void OriginRequiresValidBoundedBaselineAndOneActualRevision(string mutation)
    {
        var fixture = Fixture();
        var origin = fixture.Origin;
        switch (mutation)
        {
            case "null_origin": origin = null!; break;
            case "request_id": origin = origin with { RequestId = "../request" }; break;
            case "request_digest": origin = origin with { RequestDigest = "wrong" }; break;
            case "base_digest_format": origin = origin with { BasePlanDigest = "wrong" }; break;
            case "base_digest_mismatch": origin = origin with { BasePlanDigest = "sha256:" + new string('e', 64) }; break;
            case "journal_revision": origin = origin with { ExpectedJournalRevision = 0 }; break;
            case "journal_overflow": origin = origin with { ExpectedJournalRevision = long.MaxValue }; break;
            case "attempt": origin = origin with { BaseAttempt = 0 }; break;
            case "attempt_overflow": origin = origin with { BaseAttempt = int.MaxValue }; break;
            case "corrupt_base": origin = origin with { BaseTableJson = "{}" }; break;
            case "corrupt_after": origin = origin with { TableJson = "{}" }; break;
            case "unchanged_revision": origin = origin with { TableJson = origin.BaseTableJson }; break;
            case "revision_only": origin = origin with { TableJson = SizeTableRepairData.Serialize(fixture.BaseTable.Edit(0,
                fixture.BaseTable.Columns, fixture.BaseTable.Rows, fixture.Table.UpdatedAt)) }; break;
            case "skipped_revision": origin = origin with { TableJson = SizeTableRepairData.Serialize(fixture.Table.Edit(1,
                fixture.Table.Columns, fixture.Table.Rows, fixture.Table.UpdatedAt)) }; break;
            case "backwards_update":
                var older = fixture.BaseTable.Edit(0, fixture.BaseTable.Columns, fixture.BaseTable.Rows, SizeTableRepairDataTests.Now.AddMinutes(2));
                var newer = fixture.Table.Edit(1, fixture.Table.Columns, fixture.Table.Rows, SizeTableRepairDataTests.Now.AddMinutes(1));
                origin = origin with { BaseTableJson = SizeTableRepairData.Serialize(older), TableJson = SizeTableRepairData.Serialize(newer) };
                break;
        }
        Assert.Throws<InvalidDataException>(() => RenderPlanSizeTableRepair.Validate(fixture.Before, fixture.After, origin));
        if (mutation != "base_digest_mismatch") Assert.Throws<InvalidDataException>(() => RenderPlanSizeTableRepair.ValidateOrigin(origin));
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("target")]
    [InlineData("duplicate_json")]
    [InlineData("wrong_grid_hash")]
    [InlineData("wrong_resource_ref")]
    [InlineData("wrong_resource_media")]
    public void OnlyExplicitInsertionWithClosedRasterResourceIsSupported(string mutation)
    {
        var fixture = Fixture();
        var before = JsonNode.Parse(fixture.Before)!;
        var after = JsonNode.Parse(fixture.After)!;
        switch (mutation)
        {
            case "legacy":
                foreach (var plan in new[] { before, after })
                {
                    plan["size_table"]!.AsObject().Remove("insertion");
                    var capabilities = plan["required_capabilities"]!.AsArray();
                    for (var i = capabilities.Count - 1; i >= 0; i--)
                        if (capabilities[i]!.GetValue<string>() is RenderPlanPlacementMigration.Capability or RenderPlanPlacementMigration.SizeInsertionCapability)
                            capabilities.RemoveAt(i);
                }
                break;
            case "target":
                var ordinary = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "render-plan-v1.valid.example.json")))!;
                after["targets"]!.AsArray().Add(ordinary["targets"]![0]!.DeepClone()); break;
            case "wrong_grid_hash": after["size_table"]!["grid_digest"] = "sha256:" + new string('e', 64); break;
            case "wrong_resource_ref": after["size_table"]!["render_resource_id"] = after["resources"]![0]!["resource_id"]!.DeepClone(); break;
            case "wrong_resource_media": after["resources"]![1]!["media_type"] = "image/jpeg"; break;
        }
        var oldJson = Seal(before);
        var json = Seal(after);
        if (mutation == "duplicate_json") json = "{\"correlation_id\":\"injected\"," + json[1..];
        var origin = fixture.Origin with { BasePlanDigest = before["plan_digest"]!.GetValue<string>() };
        Assert.Throws<InvalidDataException>(() => RenderPlanSizeTableRepair.Validate(oldJson, json, origin));
    }

    private sealed record RepairFixture(string Before, string After, SizeTable BaseTable, SizeTable Table, RenderSizeTableRepairOrigin Origin);

    private static RepairFixture Fixture(string change = "cell")
    {
        var source = SizeTableInsertionTests.Fixture();
        var before = JsonNode.Parse(new RenderPlanPlacementMigration(Validator).MigrateSizeTableInsertion(source.Plan,
            source.Target, source.Baseline, "plan_original", "attempt_original", "artifact_original", SizeTableRepairDataTests.Now))!;
        // Keep the fixture's frozen PSD output intention explicit for the negative tests.
        before["output"]!["editable_psd"]!["enabled"] = true;
        before["output"]!["editable_psd"]!["file_name"] = "size.psd";
        var baseTable = SizeTableRepairDataTests.Table();
        PutTable(before, baseTable);
        var oldJson = Seal(before);
        var draft = SizeTableRepairDataTests.ChangeCell(baseTable, "69");
        if (change == "header") draft = SizeTableRepairDataTests.Draft(baseTable) with
        { Columns = [draft.Columns[0], draft.Columns[1] with { SourceText = " 胸围（cm） " }] };
        if (change == "shape") draft = draft with
        {
            Columns = draft.Columns.Append(new SizeTableEditorColumnDocument("col_003", "衣长")).ToArray(),
            Rows = draft.Rows.Select(row => row with { Cells = row.Cells.Append("/").ToArray() })
                .Append(new SizeTableEditorRowDocument("row_new", ["XXXL", "-", "\\"])).ToArray()
        };
        var table = SizeTableRepairData.ApplyDraft(baseTable, draft, SizeTableRepairDataTests.Now.AddMinutes(1));
        var after = JsonNode.Parse(RenderPlanRegeneration.Create(oldJson, table.UpdatedAt))!;
        PutTable(after, table);
        after["resources"]![1]!["resource_id"] = "size_raster_new";
        after["resources"]![1]!["sha256"] = new string('d', 64);
        after["resources"]![1]!["byte_length"] = 2048;
        after["size_table"]!["render_resource_id"] = "size_raster_new";
        after["size_table"]!["grid_digest"] = "sha256:" + new string('d', 64);
        after["size_table"]!["insertion"]!["placement"] = JsonSerializer.SerializeToNode(
            new RenderTargetPlacement(1000, 500, 10m, 417.5m, 770m, 385m), Options);
        var command = SizeTableRepairDataTests.Command(baseTable) with
        {
            Target = SizeTableRepairDataTests.Command(baseTable).Target with { BasePlanDigest = before["plan_digest"]!.GetValue<string>() },
            Draft = draft
        };
        var origin = new RenderSizeTableRepairOrigin(command.RequestId, SizeTableRepairData.RequestDigest(command),
            command.Target.ExpectedJournalRevision, command.Target.ExpectedAttempt, command.Target.BasePlanDigest,
            SizeTableRepairData.Serialize(baseTable), SizeTableRepairData.Serialize(table));
        var newJson = Seal(after);
        Assert.True(Validator.Validate(oldJson).IsValid);
        Assert.True(Validator.Validate(newJson).IsValid);
        return new(oldJson, newJson, baseTable, table, origin);
    }

    private static void PutTable(JsonNode plan, SizeTable table)
    {
        var size = plan["size_table"]!;
        size["size_table_id"] = table.TableId;
        size["structured_data_digest"] = table.DataDigest;
        size["columns"] = JsonSerializer.SerializeToNode(table.Columns.Select((column, index) => new
        { column_id = column.ColumnId, label = column.SourceText, index }));
        size["rows"] = JsonSerializer.SerializeToNode(table.Rows.Select((row, index) => new
        { row_id = row.RowId, index, cells = row.Cells.ToDictionary(cell => cell.ColumnId, cell => cell.SourceText) }));
    }

    private static string Seal(JsonNode plan)
    {
        plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
        return plan.ToJsonString();
    }
}
