using Desktop.Application.SizeTables;
using Desktop.Domain.SizeTables;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.SizeTables;

namespace Desktop.Infrastructure.Tests;

public sealed class SizeTableFileRepositoryTests
{
    [Fact]
    public async Task RoundTripAndRevisionCasPreserveVerbatimSymbols()
    {
        using var directory = new TestDirectory();
        using var repository = Repository(directory);
        var table = Table("/");

        var firstSave = await repository.SaveAsync(table, null, CancellationToken.None);
        var loaded = await repository.LoadAsync("batch_01", "style_01", "page_01", CancellationToken.None);
        var edited = loaded.Table!.Edit(
            loaded.Table.Revision,
            loaded.Table.Columns,
            loaded.Table.Rows,
            Now.AddMinutes(1));
        var stale = await repository.SaveAsync(edited, expectedRevision: 1, CancellationToken.None);
        var saved = await repository.SaveAsync(edited, expectedRevision: 0, CancellationToken.None);
        var reloaded = await repository.LoadAsync("batch_01", "style_01", "page_01", CancellationToken.None);

        Assert.Equal(SizeTableSaveStatus.Saved, firstSave.Status);
        Assert.Equal(SizeTableLoadStatus.Loaded, loaded.Status);
        Assert.Equal("/", loaded.Table.Rows[0].Cells[1].SourceText);
        Assert.Equal(SizeTableSaveStatus.RevisionConflict, stale.Status);
        Assert.Equal(SizeTableSaveStatus.Saved, saved.Status);
        Assert.Equal(1, reloaded.Table!.Revision);
    }

    [Fact]
    public async Task CorruptStateMapsToRecoveryWithoutLeakingPath()
    {
        using var directory = new TestDirectory();
        using var repository = Repository(directory);
        await repository.SaveAsync(Table("\\"), null, CancellationToken.None);
        var statePath = Path.Combine(
            directory.Path,
            "batches",
            "batch_01",
            "size-tables",
            "style_01",
            "page_01",
            "current.json");
        await File.WriteAllTextAsync(statePath, "{corrupt");

        var loaded = await repository.LoadAsync("batch_01", "style_01", "page_01", CancellationToken.None);
        var save = await repository.SaveAsync(Table("-"), expectedRevision: 0, CancellationToken.None);

        Assert.Equal(SizeTableLoadStatus.RecoveryRequired, loaded.Status);
        Assert.Equal("SIZE_TABLE_STATE_CORRUPT", loaded.ReasonCode);
        Assert.DoesNotContain(directory.Path, loaded.ReasonCode!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SizeTableSaveStatus.RecoveryRequired, save.Status);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private static SizeTableFileRepository Repository(TestDirectory directory) => new(
        new AtomicFileStore(new LocalAppPathPolicy(directory.Path)));

    private static SizeTable Table(string symbol)
    {
        var source = SizeTableSource.Create("asset_size", new string('a', 64));
        var candidate = SizeTableCandidate.Create(
            "candidate_01",
            source,
            0.99m,
            ["尺码", "袖长"],
            [["L", symbol], ["XL", symbol]]);
        return SizeTable.FromCandidate(
            "size_01", "batch_01", "style_01", "page_01", candidate, Now);
    }
}
