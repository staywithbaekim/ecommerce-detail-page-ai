using Desktop.Application.Templates;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Tests;

public sealed class TemplateInstanceProjectionStoreTests
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);
    [Fact]
    public async Task FreezeRoundTripsAfterClosingAndReopeningSqlite()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var snapshot = Snapshot();
        var command = new FreezeTemplateInstanceProjectionCommand("freeze_01", 0, snapshot);

        await using (var first = new SqliteDesktopStore(options))
        {
            await first.InitializeAsync();
            var result = await first.FreezeAsync(command, CancellationToken.None);
            Assert.Equal(FreezeTemplateInstanceProjectionOutcome.Created, result.Outcome);
            Assert.Equal(snapshot.ProjectionDigest, result.ProjectionDigest);
            var duplicate = await first.FreezeAsync(command, CancellationToken.None);
            Assert.Equal(FreezeTemplateInstanceProjectionOutcome.AlreadyApplied, duplicate.Outcome);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        Assert.Equal(TemplateInstanceProjectionSnapshotCodec.Serialize(snapshot),
            TemplateInstanceProjectionSnapshotCodec.Serialize((await reopened.ReadAsync("batch", "style", CancellationToken.None))!));
    }

    [Fact]
    public async Task SameCommandWithDifferentSnapshotIsRejectedAndExistingSnapshotSurvives()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var original = Snapshot();
        await store.FreezeAsync(new("freeze_01", 0, original), CancellationToken.None);
        var changed = original with { InputFactsDigest = "sha256:" + new string('b', 64) };
        changed = ReDigest(changed);
        await Assert.ThrowsAsync<TemplateRuleViolationException>(() =>
            store.FreezeAsync(new("freeze_01", 0, changed), CancellationToken.None));
        Assert.Equal(TemplateInstanceProjectionSnapshotCodec.Serialize(original),
            TemplateInstanceProjectionSnapshotCodec.Serialize((await store.ReadAsync("batch", "style", CancellationToken.None))!));
    }

    [Fact]
    public async Task ExpectedVersionConflictCannotReplaceFrozenSnapshot()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var original = Snapshot();
        await store.FreezeAsync(new("freeze_01", 0, original), CancellationToken.None);
        await Assert.ThrowsAsync<TemplateRuleViolationException>(() =>
            store.FreezeAsync(new("freeze_02", 1, original), CancellationToken.None));
        Assert.Equal(TemplateInstanceProjectionSnapshotCodec.Serialize(original),
            TemplateInstanceProjectionSnapshotCodec.Serialize((await store.ReadAsync("batch", "style", CancellationToken.None))!));
    }

    [Fact]
    public async Task CorruptStoredPayloadFailsClosed()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var snapshot = Snapshot();
        await store.FreezeAsync(new("freeze_01", 0, snapshot), CancellationToken.None);
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE template_instance_projection_snapshots SET payload_json='{}' WHERE batch_id='batch' AND style_id='style';";
        await update.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<TemplateRuleViolationException>(() => store.ReadAsync("batch", "style", CancellationToken.None));
    }

    private static TemplateInstanceProjectionSnapshot Snapshot()
    {
        var instance = new TemplateInstanceProjection("instance_" + new string('a', 64), "batch", "style", "snapshot", "revision",
            "sha256:" + new string('b', 64), "sha256:" + new string('c', 64), "page", "resource", "branch", "recipe", 0, 0,
            [new("slot", "color")], [new("frame", "slot", "color", "asset", 1)],
            TemplateInstancePreflightStatus.AssetBindingsSatisfied, []);
        var value = new TemplateInstanceProjectionSnapshot(1, 1, "batch", "style", "snapshot", "revision",
            "sha256:" + new string('b', 64), "sha256:" + new string('c', 64), "", [instance]);
        return ReDigest(value);
    }

    private static TemplateInstanceProjectionSnapshot ReDigest(TemplateInstanceProjectionSnapshot snapshot)
    {
        var placeholder = snapshot with { ProjectionDigest = "" };
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            schema = "template-instance-projection-snapshot/v1", placeholder.SchemaVersion, placeholder.SnapshotVersion,
            placeholder.BatchId, placeholder.StyleId, placeholder.SnapshotId, placeholder.RevisionId, placeholder.RuleDigest,
            placeholder.InputFactsDigest, placeholder.Instances
        }, JsonOptions);
        return placeholder with { ProjectionDigest = "sha256:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))) };
    }
}
