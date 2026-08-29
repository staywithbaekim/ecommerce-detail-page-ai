using System.Globalization;
using Desktop.Application.Ai;
using Desktop.Application.Tasks;
using Desktop.Domain.Ai;
using Desktop.Domain.Tasks;
using Desktop.Infrastructure.Persistence;
using Desktop.Infrastructure.Files;
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
        var entry = new AiRequestLedgerEntry("request_01", "sha256:" + new string('f', 64), "batch_01", "style_01", "Queued", null, now, now);

        var first = await store.RecordRequestAsync(entry, CancellationToken.None);
        var duplicate = await store.RecordRequestAsync(entry, CancellationToken.None);
        Assert.Equal(first, duplicate);
        var sameFingerprint = await store.RecordRequestAsync(
            entry with { RequestId = "request_02" }, CancellationToken.None);
        Assert.Equal("request_01", sameFingerprint.RequestId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordRequestAsync(
            entry with { RequestId = "request_03", BatchId = "batch_02" }, CancellationToken.None));
    }

    [Fact]
    public async Task AiSessionRoundTripsStateAndCacheUsageAcrossRestart()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var initial = AiAnalysisSession.Create(
            "session_01", "batch_01", "style_01", "snapshot_01", "analysis_key_01",
            ["frame_01"], ["asset_01", "asset_02"]);
        var uploading = initial.AdvanceTo(AiAnalysisSessionStatus.Uploading);
        var analysing = uploading.AdvanceTo(AiAnalysisSessionStatus.Analysing);
        var validated = analysing.AdvanceTo(AiAnalysisSessionStatus.Validated);
        var session = validated.LockSelection("frame_01", "asset_02", 0);
        await using (var first = new SqliteDesktopStore(options))
        {
            await first.InitializeAsync();
            await first.SaveSessionAsync(initial, CancellationToken.None);
            await first.SaveSessionAsync(uploading, CancellationToken.None, expectedSelectionRevision: 0);
            await first.SaveSessionAsync(analysing, CancellationToken.None, expectedSelectionRevision: 0);
            await first.SaveSessionAsync(validated, CancellationToken.None, expectedSelectionRevision: 0);
            await first.SaveSessionAsync(session, CancellationToken.None, expectedSelectionRevision: 0);
            var requestTime = DateTimeOffset.UtcNow;
            await first.RecordRequestAsync(new AiRequestLedgerEntry(
                "request_01", "sha256:" + new string('a', 64), "batch_01", "style_01", "Queued", null, requestTime, requestTime), CancellationToken.None);
            await first.UpdateRequestStatusAsync("request_01", "Queued", "Uploading", null, DateTimeOffset.UtcNow, CancellationToken.None);
            await first.UpdateRequestStatusAsync("request_01", "Uploading", "Analysing", null, DateTimeOffset.UtcNow, CancellationToken.None);
            await first.UpdateRequestStatusAsync("request_01", "Analysing", "Validated", "results/session_01.json", DateTimeOffset.UtcNow, CancellationToken.None);
            await first.RecordUsageAsync(new AiUsageEventRecord(
                "usage_01", "request_01", "model/proposed", 10, 5, false, 0, null, null, DateTimeOffset.UtcNow), CancellationToken.None);
            await first.SaveCacheReferenceAsync(new AiCacheReference(
                "sha256:" + new string('c', 64), "results/session_01.json", "consent_01", "model/proposed", "usage_01", DateTimeOffset.UtcNow), CancellationToken.None);
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
        Assert.Equal("results/session_01.json", (await reopened.FindCacheReferenceAsync("sha256:" + new string('c', 64), CancellationToken.None))!.ResultReference);
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
            new AiCacheReference("sha256:" + new string('a', 64), @"C:\\secret.json", "consent", "model", "usage", DateTimeOffset.UtcNow),
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FindCacheReferenceAsync("cache_key", CancellationToken.None));
    }

    [Fact]
    public async Task SessionSaveUsesSelectionRevisionCasAndStatusLedgerUsesControlledCas()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var initial = AiAnalysisSession.Create("session_01", "batch_01", "style_01", "snapshot_01", "key_01", ["frame_01"], ["asset_01"]);
        await store.SaveSessionAsync(initial, CancellationToken.None);
        var uploading = initial.AdvanceTo(AiAnalysisSessionStatus.Uploading);
        await store.SaveSessionAsync(uploading, CancellationToken.None, expectedSelectionRevision: 0);
        var analysing = uploading.AdvanceTo(AiAnalysisSessionStatus.Analysing);
        await store.SaveSessionAsync(analysing, CancellationToken.None, expectedSelectionRevision: 0);
        var session = analysing.AdvanceTo(AiAnalysisSessionStatus.Validated);
        await store.SaveSessionAsync(session, CancellationToken.None, expectedSelectionRevision: 0);
        var locked = session.LockSelection("frame_01", "asset_01", 0);
        await store.SaveSessionAsync(locked, CancellationToken.None, expectedSelectionRevision: 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveSessionAsync(
            session, CancellationToken.None, expectedSelectionRevision: 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveSessionAsync(
            locked, CancellationToken.None, expectedSelectionRevision: 0));

        var requestTime = DateTimeOffset.UtcNow;
        var request = new AiRequestLedgerEntry("request_01", "sha256:" + new string('b', 64), "batch_01", "style_01", "Queued", null, requestTime, requestTime);
        await store.RecordRequestAsync(request, CancellationToken.None);
        var progressed = await store.UpdateRequestStatusAsync("request_01", "Queued", "Uploading", null, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal("Uploading", progressed.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateRequestStatusAsync(
            "request_01", "Uploading", "Analysing", "results/early.json", DateTimeOffset.UtcNow, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateRequestStatusAsync(
            "request_01", "Queued", "Analysing", null, DateTimeOffset.UtcNow, CancellationToken.None));

        var queuedJump = AiAnalysisSession.Restore(
            "session_jump", "batch_01", "style_01", "snapshot_01", "key_jump",
            AiAnalysisSessionStatus.Queued, 0, [], ["frame_01"], ["asset_01"], false);
        await store.SaveSessionAsync(queuedJump, CancellationToken.None);
        var planClosed = AiAnalysisSession.Restore(
            "session_jump", "batch_01", "style_01", "snapshot_01", "key_jump",
            AiAnalysisSessionStatus.PlanClosed, 0, [], ["frame_01"], ["asset_01"], true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveSessionAsync(
            planClosed, CancellationToken.None, expectedSelectionRevision: 0));
    }

    [Fact]
    public async Task CacheAndUsageConflictsAreRejectedWithoutDecimalLoss()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var requestTime = DateTimeOffset.UtcNow;
        var request = new AiRequestLedgerEntry("request_01", "sha256:" + new string('b', 64), "batch_01", "style_01", "Queued", null, requestTime, requestTime);
        var amount = 1234567890.1234567890123456789m;
        var usage = new AiUsageEventRecord("usage_01", "request_01", "model", 1, 2, false, 0, amount, "USD", DateTimeOffset.UtcNow);
        var cache = new AiCacheReference("sha256:" + new string('c', 64), "results/session.json", "consent", "model", "usage_01", DateTimeOffset.UtcNow);
        await store.RecordRequestAsync(request, CancellationToken.None);
        await store.UpdateRequestStatusAsync("request_01", "Queued", "Uploading", null, DateTimeOffset.UtcNow, CancellationToken.None);
        await store.UpdateRequestStatusAsync("request_01", "Uploading", "Analysing", null, DateTimeOffset.UtcNow, CancellationToken.None);
        await store.UpdateRequestStatusAsync("request_01", "Analysing", "Validated", "results/session.json", DateTimeOffset.UtcNow, CancellationToken.None);
        await store.RecordUsageAsync(usage, CancellationToken.None);
        await store.SaveCacheReferenceAsync(cache, CancellationToken.None);
        await store.SaveCacheReferenceAsync(cache, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveCacheReferenceAsync(
            cache with { ResultReference = "results/other.json" }, CancellationToken.None));
        await store.RecordUsageAsync(usage, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordUsageAsync(
            usage with { OutputUnits = 3 }, CancellationToken.None));
        Assert.Equal(amount, Assert.Single(await store.LoadUsageAsync("request_01", CancellationToken.None)).ProviderCostAmount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordRequestAsync(
            request with { Fingerprint = "not-a-digest" }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordUsageAsync(
            usage with { RequestId = "missing_request" }, CancellationToken.None));
    }

    [Fact]
    public async Task AiStateManifestStagesCommitsAndRecoversAfterRestart()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var files = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        var manifests = new AiAnalysisStateManifestStore(files);
        var manifest = new AiAnalysisStateManifestV1(
            AiAnalysisStateProtocol.Version, "manifest_01", "batch_01", ["session_01"], ["request_01"], ["cache_01"], DateTimeOffset.UtcNow);
        var staged = await manifests.StageAsync(manifest, CancellationToken.None);
        var stagedInspection = await manifests.InspectAsync("batch_01", CancellationToken.None);
        Assert.NotNull(stagedInspection);
        Assert.True(stagedInspection!.IsStaged);
        Assert.False(stagedInspection.IsCommitted);

        await manifests.CommitAsync(staged, CancellationToken.None);
        var committed = await manifests.InspectAsync("batch_01", CancellationToken.None);
        Assert.True(committed!.IsCommitted);
        Assert.False(committed.IsStaged);

        await files.WriteAtomicallyAsync(committed.ManifestReference, "tampered"u8.ToArray(), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => manifests.InspectAsync("batch_01", CancellationToken.None));
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
