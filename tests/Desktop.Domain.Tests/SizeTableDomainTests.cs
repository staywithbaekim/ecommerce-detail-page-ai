using Desktop.Domain.SizeTables;

namespace Desktop.Domain.Tests;

public sealed class SizeTableDomainTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("\\")]
    [InlineData("-")]
    public void LegalSourceSymbolsRemainVerbatimAndAreNotMissing(string symbol)
    {
        var table = Table(0.99m, [["L", symbol], ["XL", symbol]]);

        Assert.Equal(SizeTableStatus.Valid, table.Status);
        Assert.True(table.CanSave);
        Assert.All(table.Rows, row => Assert.Equal(symbol, row.Cells[1].SourceText));
        Assert.DoesNotContain(table.Issues, issue => issue.Code == "SIZE_TABLE_VALUE_MISSING");
    }

    [Fact]
    public void TrulyEmptyCellIsBlockingButSourceTextIsPreserved()
    {
        var table = Table(0.99m, [["L", ""]]);

        Assert.Equal(SizeTableStatus.Invalid, table.Status);
        Assert.False(table.CanSave);
        Assert.Equal(string.Empty, table.Rows[0].Cells[1].SourceText);
        Assert.Contains(table.Issues, issue => issue.Code == "SIZE_TABLE_VALUE_MISSING");
    }

    [Fact]
    public void LowConfidenceRequiresReviewWithoutChangingCells()
    {
        var table = Table(0.5m, [["L", "39.5"]]);

        Assert.Equal(SizeTableStatus.ReviewRequired, table.Status);
        Assert.False(table.CanSave);
        Assert.Equal("39.5", table.Rows[0].Cells[1].SourceText);
        Assert.Contains(table.Issues, issue => issue.Code == "SIZE_TABLE_CONFIDENCE_LOW");
    }

    [Fact]
    public void RaggedCandidateFailsClosedAsInvalid()
    {
        var candidate = SizeTableCandidate.Create(
            "candidate_01",
            Source(),
            0.99m,
            ["尺码", "袖长"],
            [["L"]]);

        var table = SizeTable.FromCandidate(
            "size_01", "batch_01", "style_01", "page_01", candidate, Now);

        Assert.Equal(SizeTableStatus.Invalid, table.Status);
        Assert.Empty(table.Columns);
        Assert.Empty(table.Rows);
        Assert.Contains(table.Issues, issue => issue.Code == "SIZE_TABLE_STRUCTURE_INVALID");
    }

    [Fact]
    public void EditUsesRevisionCasAndClearsCandidateConfidenceAfterUserCorrection()
    {
        var table = Table(0.5m, [["L", "-"]]);
        var edited = table.Edit(table.Revision, table.Columns, table.Rows, Now.AddMinutes(1));

        Assert.Equal(1, edited.Revision);
        Assert.Equal(SizeTableStatus.Valid, edited.Status);
        Assert.NotEqual(table.DataDigest, edited.DataDigest);
        var exception = Assert.Throws<SizeTableRuleViolationException>(() => table.Edit(
            expectedRevision: 1,
            table.Columns,
            table.Rows,
            Now.AddMinutes(1)));
        Assert.Equal("SIZE_TABLE_REVISION_CONFLICT", exception.Code);
    }

    [Fact]
    public void RestoreRejectsDigestMutation()
    {
        var table = Table(0.99m, [["L", "/"]]);

        var exception = Assert.Throws<SizeTableRuleViolationException>(() => SizeTable.Restore(
            table.TableId,
            table.BatchId,
            table.StyleId,
            table.PageId,
            table.CandidateId,
            table.Source,
            table.Status,
            table.Revision,
            table.Columns,
            table.Rows,
            table.Issues,
            "sha256:" + new string('f', 64),
            table.CreatedAt,
            table.UpdatedAt));

        Assert.Equal("SIZE_TABLE_DATA_DIGEST_MISMATCH", exception.Code);
    }

    [Fact]
    public void ExtractionStateContainsNoInventedGrid()
    {
        var table = SizeTable.BeginExtraction(
            "size_01", "batch_01", "style_01", "page_01", Source(), Now);

        Assert.Equal(SizeTableStatus.Extracting, table.Status);
        Assert.Empty(table.Columns);
        Assert.Empty(table.Rows);
        Assert.False(table.CanSave);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private static SizeTable Table(decimal confidence, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var candidate = SizeTableCandidate.Create(
            "candidate_01",
            Source(),
            confidence,
            ["尺码", "袖长"],
            rows);
        return SizeTable.FromCandidate(
            "size_01", "batch_01", "style_01", "page_01", candidate, Now);
    }

    private static SizeTableSource Source() =>
        SizeTableSource.Create("asset_size", new string('a', 64));
}
