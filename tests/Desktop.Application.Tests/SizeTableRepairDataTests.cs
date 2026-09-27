using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
using Desktop.Domain.SizeTables;

namespace Desktop.Application.Tests;

public sealed class SizeTableRepairDataTests
{
    internal static readonly DateTimeOffset Now = new(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);

    internal static SizeTable Table(string value = "68") => SizeTable.FromCandidate("size_01", "batch_01JEXAMPLE",
        "product_01JEXAMPLE", "page_01", SizeTableCandidate.Create("candidate_1",
            SizeTableSource.Create("asset_size", new string('a', 64)), 1m,
            ["尺码", "胸围（cm）"], [["M", value], ["L", "-"], ["XL", "/"], ["XXL", "\\"]]), Now);

    internal static SizeTableEditorDraft Draft(SizeTable table)
    {
        var document = SizeTableRepairData.ToEditorDocument(table);
        return new(document.Columns, document.Rows);
    }

    internal static SizeTableEditorDraft ChangeCell(SizeTable table, string value)
    {
        var draft = Draft(table);
        var rows = draft.Rows.ToArray();
        rows[0] = rows[0] with { Cells = [rows[0].Cells[0], value] };
        return draft with { Rows = rows };
    }

    internal static SizeTablePageRepairCommand Command(SizeTable? table = null)
    {
        table ??= Table();
        return new("repair_1", new(table.BatchId, table.StyleId, table.PageId, null, 7, 2,
            "sha256:" + new string('b', 64), table.Revision, table.DataDigest), ChangeCell(table, "69"));
    }

    [Fact]
    public void SnapshotRoundTripPreservesRawSymbolsIdentityTimesAndFrozenCollections()
    {
        var original = Table(" 68 ");
        var json = SizeTableRepairData.Serialize(original);
        var restored = SizeTableRepairData.Deserialize(json);
        Assert.Equal(json, SizeTableRepairData.Serialize(restored));
        Assert.Equal(original.Source, restored.Source);
        Assert.Equal(original.DataDigest, restored.DataDigest);
        Assert.Equal(original.CreatedAt, restored.CreatedAt);
        Assert.Equal(original.UpdatedAt, restored.UpdatedAt);
        Assert.Collection(restored.Rows, row => Assert.Equal(" 68 ", row.Cells[1].SourceText),
            row => Assert.Equal("-", row.Cells[1].SourceText), row => Assert.Equal("/", row.Cells[1].SourceText),
            row => Assert.Equal("\\", row.Cells[1].SourceText));
        var editor = SizeTableRepairData.ToEditorDocument(restored);
        Assert.Equal("asset_size", editor.SourceAssetId);
        Assert.Equal(original.Source.ContentDigest, editor.SourceContentDigest);
        Assert.Equal(SizeTableEditorStatus.Valid, editor.Status);
        Assert.False(editor.Rows is SizeTableEditorRowDocument[]);
        Assert.False(editor.Rows[0].Cells is string[]);
    }

    [Theory]
    [InlineData(" 68 ")]
    [InlineData("-")]
    [InlineData("/")]
    [InlineData("\\")]
    [InlineData("e\u0301")]
    public void ApplyingRawCellDraftCreatesOnlyOneRevisionWithoutMutatingTheBase(string value)
    {
        var original = Table();
        var before = SizeTableRepairData.Serialize(original);
        var edited = SizeTableRepairData.ApplyDraft(original, ChangeCell(original, value), Now.AddMinutes(1));
        Assert.Equal(before, SizeTableRepairData.Serialize(original));
        Assert.Equal(original.Revision + 1, edited.Revision);
        Assert.Equal(original.TableId, edited.TableId);
        Assert.Equal(original.CandidateId, edited.CandidateId);
        Assert.Equal(original.Source, edited.Source);
        Assert.Equal(original.CreatedAt, edited.CreatedAt);
        Assert.Equal(Now.AddMinutes(1), edited.UpdatedAt);
        Assert.Equal(value, edited.Rows[0].Cells[1].SourceText);
        Assert.NotEqual(original.DataDigest, edited.DataDigest);
    }

    [Fact]
    public void HeaderChangeAndCanonicallyEquivalentUnicodeAreNotNormalizedAway()
    {
        var table = Table("é");
        var columns = Draft(table).Columns.ToArray();
        columns[1] = columns[1] with { SourceText = " 胸围（cm） " };
        var edited = SizeTableRepairData.ApplyDraft(table, ChangeCell(table, "e\u0301") with { Columns = columns }, Now);
        Assert.Equal(" 胸围（cm） ", edited.Columns[1].SourceText);
        Assert.Equal("e\u0301", edited.Rows[0].Cells[1].SourceText);
        Assert.True(edited.CanSave);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoActualHeaderOrCellChangeIsRejectedEvenIfIdsWereChanged(bool changeIds)
    {
        var table = Table();
        var draft = Draft(table);
        if (changeIds) draft = draft with
        {
            Columns = draft.Columns.Select((column, index) => column with { ColumnId = "new_col_" + index }).ToArray(),
            Rows = draft.Rows.Select((row, index) => row with { RowId = "new_row_" + index }).ToArray()
        };
        Assert.Equal("SIZE_REPAIR_NO_CHANGES", Assert.Throws<InvalidOperationException>(() =>
            SizeTableRepairData.ApplyDraft(table, draft, Now.AddMinutes(1))).Message);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("control")]
    [InlineData("unicode")]
    [InlineData("too_long")]
    [InlineData("ragged")]
    [InlineData("null_cell")]
    [InlineData("null_row")]
    [InlineData("null_columns")]
    [InlineData("duplicate_column_id")]
    [InlineData("duplicate_row_id")]
    [InlineData("too_many_columns")]
    [InlineData("too_many_rows")]
    [InlineData("past_time")]
    public void InvalidDraftCannotProduceARepairSnapshot(string mutation)
    {
        var table = Table();
        var draft = ChangeCell(table, "69");
        draft = mutation switch
        {
            "empty" => ChangeCell(table, ""), "whitespace" => ChangeCell(table, " "),
            "control" => ChangeCell(table, "6\n9"), "unicode" => ChangeCell(table, "\uD800"),
            "too_long" => ChangeCell(table, new string('6', 257)),
            "ragged" => draft with { Rows = [new("row_1", ["M"])] },
            "null_cell" => draft with { Rows = [new("row_1", ["M", null!])] },
            "null_row" => draft with { Rows = [null!] },
            "null_columns" => draft with { Columns = null! },
            "duplicate_column_id" => draft with { Columns = [draft.Columns[0], draft.Columns[0]] },
            "duplicate_row_id" => draft with { Rows = [draft.Rows[0], draft.Rows[0]] },
            "too_many_columns" => draft with { Columns = Enumerable.Repeat(draft.Columns[0], 33).ToArray() },
            "too_many_rows" => draft with { Rows = Enumerable.Repeat(draft.Rows[0], 201).ToArray() },
            _ => draft
        };
        var error = Record.Exception(() => SizeTableRepairData.ApplyDraft(table, draft,
            mutation == "past_time" ? Now.AddTicks(-1) : Now));
        Assert.True(error is InvalidDataException or SizeTableRuleViolationException or EncoderFallbackException);
        Assert.Equal(0, table.Revision);
        Assert.Equal("68", table.Rows[0].Cells[1].SourceText);
    }

    [Theory]
    [InlineData("unknown_root")]
    [InlineData("unknown_source")]
    [InlineData("unknown_column")]
    [InlineData("unknown_cell")]
    [InlineData("missing_field")]
    [InlineData("null_source")]
    [InlineData("version")]
    [InlineData("status")]
    [InlineData("issue")]
    [InlineData("negative_revision")]
    [InlineData("overflow_revision")]
    [InlineData("digest")]
    [InlineData("source_digest")]
    [InlineData("changed_cell")]
    [InlineData("changed_cell_column")]
    [InlineData("default_time")]
    [InlineData("backwards_time")]
    [InlineData("no_timezone")]
    [InlineData("empty_rows")]
    public void DamagedUnknownOrIncompleteSnapshotIsRejected(string mutation)
    {
        var node = JsonNode.Parse(SizeTableRepairData.Serialize(Table()))!;
        switch (mutation)
        {
            case "unknown_root": node["unexpected"] = true; break;
            case "unknown_source": node["source"]!["path"] = "not_allowed"; break;
            case "unknown_column": node["columns"]![0]!["index"] = 0; break;
            case "unknown_cell": node["rows"]![0]!["cells"]![0]!["value"] = "M"; break;
            case "missing_field": node.AsObject().Remove("revision"); break;
            case "null_source": node["source"] = null; break;
            case "version": node["schema_version"] = "size-table/v1"; break;
            case "status": node["status"] = "ReviewRequired"; break;
            case "issue": node["issues"]!.AsArray().Add(new JsonObject()); break;
            case "negative_revision": node["revision"] = -1; break;
            case "overflow_revision": node["revision"] = long.MaxValue; break;
            case "digest": node["data_digest"] = "sha256:" + new string('0', 64); break;
            case "source_digest": node["source"]!["content_digest"] = new string('a', 64); break;
            case "changed_cell": node["rows"]![0]!["cells"]![1]!["source_text"] = "69"; break;
            case "changed_cell_column": node["rows"]![0]!["cells"]![1]!["column_id"] = "col_999"; break;
            case "default_time": node["created_at"] = default(DateTimeOffset); break;
            case "backwards_time": node["updated_at"] = Now.AddTicks(-1); break;
            case "no_timezone": node["updated_at"] = "2026-09-24T08:00:00"; break;
            case "empty_rows": node["rows"] = new JsonArray(); break;
        }
        Assert.Throws<InvalidDataException>(() => SizeTableRepairData.Deserialize(node.ToJsonString()));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("nested")]
    [InlineData("escaped_name")]
    public void DuplicateJsonPropertiesAreNeverLastWriterWins(string location)
    {
        var json = SizeTableRepairData.Serialize(Table());
        json = location switch
        {
            "root" => "{\"revision\":0," + json[1..],
            "escaped_name" => "{\"rev\\u0069sion\":0," + json[1..],
            _ => json.Replace("\"source_text\":", "\"source_text\":\"bad\",\"source_text\":", StringComparison.Ordinal)
        };
        Assert.Throws<InvalidDataException>(() => SizeTableRepairData.Deserialize(json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotSizeLimitUsesUtf8BytesNotJustCharacterCount(bool multibyte)
    {
        var json = multibyte ? new string('中', SizeTableRepairData.MaximumSnapshotBytes / 3 + 1)
            : new string(' ', SizeTableRepairData.MaximumSnapshotBytes + 1);
        Assert.Throws<InvalidDataException>(() => SizeTableRepairData.Deserialize(json));
    }

    [Theory]
    [InlineData("empty_cell")]
    [InlineData("whitespace_cell")]
    [InlineData("duplicate_header")]
    [InlineData("duplicate_size")]
    public void ValidStatusAndCorrectDomainDigestCannotConcealInvalidContents(string mutation)
    {
        var node = JsonNode.Parse(SizeTableRepairData.Serialize(Table()))!;
        switch (mutation)
        {
            case "empty_cell": node["rows"]![0]!["cells"]![1]!["source_text"] = ""; break;
            case "whitespace_cell": node["rows"]![0]!["cells"]![1]!["source_text"] = " "; break;
            case "duplicate_header": node["columns"]![1]!["source_text"] = "尺码"; break;
            case "duplicate_size": node["rows"]![1]!["cells"]![0]!["source_text"] = "M"; break;
        }
        node["data_digest"] = DomainDigest(node);
        var original = Table();
        // Prove this is a correct-digest, domain-restorable counterfeit, not merely a broken checksum fixture.
        var forged = SizeTable.Restore(original.TableId, original.BatchId, original.StyleId, original.PageId,
            original.CandidateId, original.Source, SizeTableStatus.Valid, original.Revision,
            node["columns"]!.AsArray().Select(column => SizeTableColumn.Create(
                column!["column_id"]!.GetValue<string>(), column["source_text"]!.GetValue<string>())),
            node["rows"]!.AsArray().Select(row => SizeTableRow.Create(row!["row_id"]!.GetValue<string>(),
                row["cells"]!.AsArray().Select(cell => SizeTableCell.Create(
                    cell!["column_id"]!.GetValue<string>(), cell["source_text"]!.GetValue<string>())))), [],
            node["data_digest"]!.GetValue<string>(), original.CreatedAt, original.UpdatedAt);
        Assert.True(forged.CanSave);
        Assert.Throws<InvalidDataException>(() => SizeTableRepairData.Serialize(forged));
        Assert.Throws<InvalidDataException>(() => SizeTableRepairData.Deserialize(node.ToJsonString()));
    }

    [Theory]
    [InlineData("request")]
    [InlineData("batch")]
    [InlineData("style")]
    [InlineData("page")]
    [InlineData("variant")]
    [InlineData("journal")]
    [InlineData("attempt")]
    [InlineData("plan")]
    [InlineData("table_revision")]
    [InlineData("table_digest")]
    [InlineData("column_id")]
    [InlineData("header")]
    [InlineData("row_id")]
    [InlineData("cell")]
    public void RequestDigestBindsEveryTargetAndRawDraftField(string field)
    {
        var original = Command();
        var changed = original;
        var target = original.Target;
        changed = field switch
        {
            "request" => original with { RequestId = "repair_2" },
            "batch" => original with { Target = target with { BatchId = "batch_other" } },
            "style" => original with { Target = target with { StyleId = "style_other" } },
            "page" => original with { Target = target with { PageId = "page_other" } },
            "variant" => original with { Target = target with { OutputVariantId = "color_red" } },
            "journal" => original with { Target = target with { ExpectedJournalRevision = 8 } },
            "attempt" => original with { Target = target with { ExpectedAttempt = 3 } },
            "plan" => original with { Target = target with { BasePlanDigest = "sha256:" + new string('c', 64) } },
            "table_revision" => original with { Target = target with { ExpectedTableRevision = 1 } },
            "table_digest" => original with { Target = target with { ExpectedTableDataDigest = "sha256:" + new string('d', 64) } },
            _ => original
        };
        var columns = original.Draft.Columns.ToArray();
        var rows = original.Draft.Rows.ToArray();
        if (field == "column_id") columns[0] = columns[0] with { ColumnId = "changed_column" };
        if (field == "header") columns[0] = columns[0] with { SourceText = " 尺码 " };
        if (field == "row_id") rows[0] = rows[0] with { RowId = "changed_row" };
        if (field == "cell") rows[0] = rows[0] with { Cells = ["M", " 69 "] };
        changed = changed with { Draft = new(columns, rows) };
        Assert.NotEqual(SizeTableRepairData.RequestDigest(original), SizeTableRepairData.RequestDigest(changed));
        Assert.Equal(SizeTableRepairData.RequestDigest(original), SizeTableRepairData.RequestDigest(original with
        { Draft = new(original.Draft.Columns.ToArray(), original.Draft.Rows.ToArray()) }));
    }

    [Theory]
    [InlineData("null_command")]
    [InlineData("null_target")]
    [InlineData("null_draft")]
    [InlineData("bad_id")]
    [InlineData("large_id")]
    [InlineData("null_batch")]
    [InlineData("bad_variant")]
    [InlineData("journal_zero")]
    [InlineData("journal_overflow")]
    [InlineData("attempt_zero")]
    [InlineData("attempt_overflow")]
    [InlineData("revision_negative")]
    [InlineData("digest")]
    [InlineData("draft_bounds")]
    public void InvalidRequestsAreRejectedBeforeHashing(string mutation)
    {
        var command = Command();
        command = mutation switch
        {
            "null_command" => null!, "null_target" => command with { Target = null! },
            "null_draft" => command with { Draft = null! }, "bad_id" => command with { RequestId = "../repair" },
            "large_id" => command with { RequestId = new string('x', 129) },
            "null_batch" => command with { Target = command.Target with { BatchId = null! } },
            "bad_variant" => command with { Target = command.Target with { OutputVariantId = "" } },
            "journal_zero" => command with { Target = command.Target with { ExpectedJournalRevision = 0 } },
            "journal_overflow" => command with { Target = command.Target with { ExpectedJournalRevision = long.MaxValue } },
            "attempt_zero" => command with { Target = command.Target with { ExpectedAttempt = 0 } },
            "attempt_overflow" => command with { Target = command.Target with { ExpectedAttempt = int.MaxValue } },
            "revision_negative" => command with { Target = command.Target with { ExpectedTableRevision = -1 } },
            "digest" => command with { Target = command.Target with { BasePlanDigest = new string('b', 64) } },
            "draft_bounds" => command with { Draft = new([], []) },
            _ => command
        };
        Assert.Throws<InvalidDataException>(() => SizeTableRepairData.RequestDigest(command));
    }

    // Independent preimage construction lets a negative test get past Restore's digest check to its weak Status check.
    private static string DomainDigest(JsonNode table)
    {
        var values = new List<string> { SizeTableProtocol.Version };
        foreach (var key in new[] { "table_id", "batch_id", "style_id", "page_id", "candidate_id" }) values.Add(table[key]!.GetValue<string>());
        values.Add(table["source"]!["asset_id"]!.GetValue<string>());
        values.Add(table["source"]!["content_digest"]!.GetValue<string>());
        values.Add("Valid"); values.Add(table["revision"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture));
        foreach (var column in table["columns"]!.AsArray())
        { values.Add(column!["column_id"]!.GetValue<string>()); values.Add(column["source_text"]!.GetValue<string>()); }
        foreach (var row in table["rows"]!.AsArray())
        {
            values.Add(row!["row_id"]!.GetValue<string>());
            foreach (var cell in row["cells"]!.AsArray())
            { values.Add(cell!["column_id"]!.GetValue<string>()); values.Add(cell["source_text"]!.GetValue<string>()); }
        }
        var preimage = string.Concat(values.Select(value => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preimage)));
    }
}
