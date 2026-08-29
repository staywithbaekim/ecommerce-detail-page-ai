using System.Globalization;
using Desktop.Application.Ai;
using Desktop.Application.Tasks;
using Desktop.Domain.Ai;
using Desktop.Domain.Tasks;
using Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Tests;

public sealed class SqliteDesktopStoreTests
{
    [Fact]
    public async Task ReopensFromLastSafeEventAndCheckpoint()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using (var firstStore = new SqliteDesktopStore(options))
        {
            await firstStore.InitializeAsync();
            var coordinator = new TaskCoordinator(firstStore);
            await coordinator.RecordAsync(Event(1, TaskEventType.TaskCreated));
            await coordinator.RecordAsync(Event(2, TaskEventType.ReadyToRender));
            await coordinator.RecordAsync(Event(3, TaskEventType.RenderingStarted));
            await coordinator.RecordAsync(Event(4, TaskEventType.PageCommitted, "page-01"));
            await firstStore.SaveAsync(
                new TaskCheckpoint(
                    "task-01",
                    "style-01",
                    "page-01",
                    "rendered",
                    1,
                    "sha256:plan",
                    "execution-01",
                    OutputCommitState.Committed,
                    DateTimeOffset.UtcNow),
                CancellationToken.None);
        }

        await using var reopenedStore = new SqliteDesktopStore(options);
        await reopenedStore.InitializeAsync();
        var reopenedCoordinator = new TaskCoordinator(reopenedStore);
        var state = await reopenedCoordinator.LoadAsync("task-01");
        var checkpoints = await reopenedStore.LoadAsync("task-01", CancellationToken.None);

        Assert.Equal(Desktop.Domain.Tasks.TaskStatus.Rendering, state.Status);
        Assert.Equal(1, state.OutputCount);
        var checkpoint = Assert.Single(checkpoints);
        Assert.Equal(OutputCommitState.Committed, checkpoint.OutputState);
        Assert.Equal("execution-01", checkpoint.BridgeExecutionId);
    }

    [Fact]
    public async Task DuplicateEventDoesNotCreateSecondRecord()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var coordinator = new TaskCoordinator(store);
        var taskEvent = Event(1, TaskEventType.TaskCreated);

        var first = await coordinator.RecordAsync(taskEvent);
        var duplicate = await coordinator.RecordAsync(taskEvent);
        var events = await store.LoadEventsAsync("task-01", CancellationToken.None);

        Assert.Equal(AppendEventOutcome.Appended, first.Outcome);
        Assert.Equal(AppendEventOutcome.Duplicate, duplicate.Outcome);
        Assert.Single(events);
    }

    [Fact]
    public async Task InitializesWalAndMigrationVersion()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = options.DatabasePath,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var journalCommand = connection.CreateCommand();
        journalCommand.CommandText = "PRAGMA journal_mode;";
        var journalMode = Convert.ToString(
            await journalCommand.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
        await using var migrationCommand = connection.CreateCommand();
        migrationCommand.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        var migrationVersion = Convert.ToInt32(
            await migrationCommand.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);

        Assert.Equal("wal", journalMode, ignoreCase: true);
        Assert.Equal(6, migrationVersion);
    }

    [Fact]
    public async Task AiLedgerIsIdempotentAndRejectsDifferentRequestForSameFingerprint()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var entry = new AiRequestLedgerEntry("request_01", "fingerprint_01", "batch_01", "style_01", "Queued", null, now, now);

        var first = await store.RecordRequestAsync(entry, CancellationToken.None);
        var duplicate = await store.RecordRequestAsync(entry with { UpdatedAt = now.AddMinutes(1) }, CancellationToken.None);
        Assert.Equal(first, duplicate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordRequestAsync(
            entry with { RequestId = "request_02" }, CancellationToken.None));
    }

    [Fact]
    public async Task AiSessionRoundTripsStateAndCacheUsageAcrossRestart()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var session = AiAnalysisSession.Create(
                "session_01", "batch_01", "style_01", "snapshot_01", "analysis_key_01",
                ["frame_01"], ["asset_01", "asset_02"])
            .AdvanceTo(AiAnalysisSessionStatus.Uploading)
            .AdvanceTo(AiAnalysisSessionStatus.Analysing)
            .AdvanceTo(AiAnalysisSessionStatus.Validated)
            .LockSelection("frame_01", "asset_02", 0);
        await using (var first = new SqliteDesktopStore(options))
        {
            await first.InitializeAsync();
            await first.SaveSessionAsync(session, CancellationToken.None);
            await first.SaveCacheReferenceAsync(new AiCacheReference(
                "cache_key_01", "results/session_01.json", "consent_01", "model/proposed", "usage_01", DateTimeOffset.UtcNow), CancellationToken.None);
            await first.RecordUsageAsync(new AiUsageEventRecord(
                "usage_01", "request_01", "model/proposed", 10, 5, false, 0, null, null, DateTimeOffset.UtcNow), CancellationToken.None);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var loaded = await reopened.LoadSessionAsync("session_01", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(AiAnalysisSessionStatus.ManualReview, loaded!.Status);
        Assert.Equal(1, loaded.SelectionRevision);
        Assert.Equal("asset_02", loaded.LockedSelections["frame_01"].AssetId);
        Assert.Contains("asset_01", loaded.AllowedAssetIds);
        Assert.True(loaded.HasVerifiedResult);
        Assert.Equal("results/session_01.json", (await reopened.FindCacheReferenceAsync("cache_key_01", CancellationToken.None))!.ResultReference);
        Assert.Single(await reopened.LoadUsageAsync("request_01", CancellationToken.None));
    }

    [Fact]
    public async Task CorruptAiSessionRecordIsRejectedOnRecovery()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
        }
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO ai_analysis_sessions(session_id,batch_id,style_id,template_snapshot_id,analysis_key,status,selection_revision,allowed_requirement_ids_json,allowed_asset_ids_json,locked_selections_json,has_verified_result,updated_at) VALUES('session_bad','batch_01','style_01','snapshot_01','key_01','AiResultUnknown',0,'not-json','[]','[]',0,'2026-08-29T00:00:00Z');";
            await command.ExecuteNonQueryAsync();
        }
        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reopened.LoadSessionAsync("session_bad", CancellationToken.None));
        Assert.Equal("AI_SESSION_RECORD_CORRUPT", exception.Message);
    }

    [Fact]
    public async Task CacheReferenceRejectsAbsolutePaths()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveCacheReferenceAsync(
            new AiCacheReference("cache_key", @"C:\\secret.json", "consent", "model", "usage", DateTimeOffset.UtcNow),
            CancellationToken.None));
    }

    [Fact]
    public async Task RejectsChangedAppliedMigrationChecksum()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
        }

        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_migrations SET checksum = 'tampered' WHERE version = 1;";
            await command.ExecuteNonQueryAsync();
        }

        await using var reopened = new SqliteDesktopStore(options);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.InitializeAsync());
        Assert.Equal("DESKTOP_MIGRATION_CHECKSUM_MISMATCH", exception.Message);
    }

    [Fact]
    public async Task FailedTemplateMigrationRollsBackAndLeavesVersionOneReadable()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_at TEXT NOT NULL,
                    checksum TEXT NOT NULL
                );
                INSERT INTO schema_migrations(version, applied_at, checksum)
                VALUES(1, '2026-08-27T00:00:00Z', 'desktop-v1-schema-001');
                CREATE TABLE tasks(task_id TEXT PRIMARY KEY);
                INSERT INTO tasks(task_id) VALUES('existing-task');
                CREATE TABLE template_groups(group_id TEXT PRIMARY KEY);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using (var store = new SqliteDesktopStore(options))
        {
            await Assert.ThrowsAsync<SqliteException>(() => store.InitializeAsync());
        }

        await using var reopened = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await reopened.OpenAsync();
        await using var versionCommand = reopened.CreateCommand();
        versionCommand.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        await using var taskCommand = reopened.CreateCommand();
        taskCommand.CommandText = "SELECT task_id FROM tasks;";
        await using var tableCommand = reopened.CreateCommand();
        tableCommand.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name = 'template_revisions';
            """;

        Assert.Equal(1L, (long)(await versionCommand.ExecuteScalarAsync())!);
        Assert.Equal("existing-task", await taskCommand.ExecuteScalarAsync());
        Assert.Equal(0L, (long)(await tableCommand.ExecuteScalarAsync())!);
    }

    private static TaskEvent Event(long sequence, TaskEventType type, string? pageId = null) =>
        new(Guid.NewGuid(), "task-01", sequence, type, DateTimeOffset.UtcNow, pageId);
}
