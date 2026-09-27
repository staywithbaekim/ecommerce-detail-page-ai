using System.Globalization;
using Desktop.Application.Ai;
using Desktop.Application.Files;
using Desktop.Application.Tasks;
using Desktop.Contracts.Ai;
using Desktop.Domain.Ai;
using Desktop.Domain.Tasks;
using Desktop.Infrastructure.Persistence;
using Desktop.Infrastructure.Files;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Tests;

public sealed class SqliteDesktopStoreTests
{
    [Fact]
    public async Task DisposeAsyncIsIdempotentForSharedServiceRegistrations()
    {
        using var directory = new TestDirectory();
        var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();

        await store.DisposeAsync();
        await store.DisposeAsync();
    }

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
        Assert.Equal(21, migrationVersion);
    }

    [Fact]
    public async Task V10CacheDataUpgradesToLogicalInvalidationAndDefaultsReusable()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = DateTimeOffset.UtcNow;
        var fingerprint = "sha256:" + new string('a', 64);
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await store.RecordRequestAsync(new AiRequestLedgerEntry(
                "request_v10", fingerprint, "batch_01", "style_01", "Queued", null, now, now), CancellationToken.None);
            await store.UpdateRequestStatusAsync("request_v10", "Queued", "Uploading", null, now.AddTicks(1), CancellationToken.None);
            await store.UpdateRequestStatusAsync("request_v10", "Uploading", "Analysing", null, now.AddTicks(2), CancellationToken.None);
            await store.UpdateRequestStatusAsync(
                "request_v10", "Analysing", "Validated", "results/request_v10", now.AddTicks(3), CancellationToken.None);
            await store.RecordUsageAsync(new AiUsageEventRecord(
                "usage_v10", "request_v10", "model/v10", 1, 1, false, 0, null, null, now.AddTicks(4)), CancellationToken.None);
            await store.SaveCacheReferenceAsync(new AiCacheReference(
                fingerprint, "results/request_v10", "consent_v10", "model/v10", "usage_v10", now.AddTicks(5)), CancellationToken.None);
        }

        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "DELETE FROM schema_migrations WHERE version=11; ALTER TABLE ai_cache_references DROP COLUMN is_reusable;";
            await downgrade.ExecuteNonQueryAsync();
        }

        await using var upgraded = new SqliteDesktopStore(options);
        await upgraded.InitializeAsync();
        var cache = await upgraded.FindCacheReferenceAsync(fingerprint, CancellationToken.None);
        Assert.NotNull(cache);
        Assert.True(cache!.IsReusable);
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
    public async Task AiSchedulerStatePersistsAndRestoresAfterStoreRestart()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = DateTimeOffset.UtcNow;
        var request = new AiAnalysisRequestV1(
            AiAnalysisProtocol.RequestSchemaVersion,
            "request",
            "request_01",
            "correlation_01",
            "product_01",
            "batch_01",
            "style_01",
            "snapshot_01",
            "revision_01",
            AiAnalysisKind.ProductJointUnderstanding,
            "model/proposed",
            "prompt/v1",
            AiAnalysisProtocol.ResultSchemaVersion,
            "compression/v1",
            "consent_01",
            "sha256:" + new string('a', 64),
            [new AiAssetInputV1(
                "asset_01", "sha256:" + new string('b', 64), "image/jpeg", 10, 10, "copies/asset_01")],
            [new AiRequirementInputV1("frame_01", "商品图", "居中", true)]);
        var item = new AiAnalysisWorkItem(
            "request_01-productjointunderstanding", request,
            AiAnalysisWorkStage.ProductJointUnderstanding, 0, now);
        var state = new AiAnalysisSchedulingState(
            [item], new AiAnalysisBudgetSnapshot(1, 12, 8, 0.0005m, false, false), now);

        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await store.RecordRequestAsync(
                new AiRequestLedgerEntry(
                    request.RequestId,
                    AiAnalysisCacheKeyFactory.Create(request),
                    request.BatchId,
                    request.StyleId,
                    "Queued",
                    null,
                    now,
                    now),
                CancellationToken.None);
            await store.SaveSchedulingStateAsync("ai-s3-default", state, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveSchedulingStateAsync(
                "ai-s3-default", state with { UpdatedAt = now.AddTicks(-1) }, CancellationToken.None));
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var restored = await reopened.LoadSchedulingStateAsync("ai-s3-default", CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(state.Budget, restored!.Budget);
        var restoredItem = Assert.Single(restored.PendingItems);
        Assert.Equal(item.WorkItemId, restoredItem.WorkItemId);
        Assert.Equal(item.Request.RequestId, restoredItem.Request.RequestId);
    }

    [Fact]
    public async Task AiSchedulerTerminalErrorCodePersistsAndRestoresWithHistoricalRequest()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = DateTimeOffset.UtcNow;
        var request = new AiAnalysisRequestV1(
            AiAnalysisProtocol.RequestSchemaVersion,
            "request",
            "request_terminal",
            "correlation_terminal",
            "product_terminal",
            "batch_terminal",
            "style_terminal",
            "snapshot_terminal",
            "revision_terminal",
            AiAnalysisKind.ProductJointUnderstanding,
            "model/proposed",
            "prompt/v1",
            AiAnalysisProtocol.ResultSchemaVersion,
            AiAnalysisCopyProfiles.WicBoundedV2,
            "consent_terminal",
            "sha256:" + new string('a', 64),
            [new AiAssetInputV1(
                "asset_terminal",
                "sha256:" + new string('b', 64),
                "image/jpeg",
                10,
                10,
                "copy_terminal")],
            [new AiRequirementInputV1("frame_terminal", "商品图", "居中", true)]);
        var state = new AiAnalysisSchedulingState(
            [],
            AiAnalysisBudgetSnapshot.Empty,
            now,
            HistoricalRequests: [request],
            TerminalErrors: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [request.RequestId] = "AI_PROVIDER_REQUEST_TOO_LARGE"
            });

        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await store.SaveSchedulingStateAsync("ai-terminal-error", state, CancellationToken.None);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var restored = await reopened.LoadSchedulingStateAsync("ai-terminal-error", CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal("AI_PROVIDER_REQUEST_TOO_LARGE", Assert.Single(restored!.TerminalErrors!).Value);
        Assert.Equal(request.RequestId, Assert.Single(restored.HistoricalRequests!).RequestId);
    }

    [Fact]
    public async Task AiSchedulerStateRejectsCorruptJson()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = DateTimeOffset.UtcNow;
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var request = new AiAnalysisRequestV1(
                AiAnalysisProtocol.RequestSchemaVersion, "request", "request_01", "correlation_01",
                "product_01", "batch_01", "style_01", "snapshot_01", "revision_01",
                AiAnalysisKind.ProductJointUnderstanding, "model/proposed", "prompt/v1",
                AiAnalysisProtocol.ResultSchemaVersion, "compression/v1", "consent_01",
                "sha256:" + new string('a', 64),
                [new AiAssetInputV1("asset_01", "sha256:" + new string('b', 64), "image/jpeg", 10, 10, "copies/asset_01")],
                [new AiRequirementInputV1("frame_01", "商品图", "居中", true)]);
            var item = new AiAnalysisWorkItem(
                "request_01-productjointunderstanding", request,
                AiAnalysisWorkStage.ProductJointUnderstanding, 0, now);
            await store.RecordRequestAsync(
                new AiRequestLedgerEntry(request.RequestId, AiAnalysisCacheKeyFactory.Create(request), request.BatchId,
                    request.StyleId, "Queued", null, now, now), CancellationToken.None);
            await store.SaveSchedulingStateAsync(
                "ai-s3-default",
                new AiAnalysisSchedulingState([item], new AiAnalysisBudgetSnapshot(1, 0, 0, 0m, false, false), now),
                CancellationToken.None);
        }

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ai_analysis_scheduler_state SET pending_items_json = '{';";
            await command.ExecuteNonQueryAsync();
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.LoadSchedulingStateAsync(
            "ai-s3-default", CancellationToken.None));
    }

    [Fact]
    public async Task AiSchedulerControlStatePersistsAcrossRestart()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        var now = DateTimeOffset.UtcNow;
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await store.SaveSchedulingStateAsync(
                "ai-s3-default",
                new AiAnalysisSchedulingState(
                    [],
                    AiAnalysisBudgetSnapshot.Empty,
                    now,
                    AiAnalysisSchedulingControlState.WaitingForNetwork,
                    "网络暂不可用"),
                CancellationToken.None);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var state = await reopened.LoadSchedulingStateAsync("ai-s3-default", CancellationToken.None);
        Assert.NotNull(state);
        Assert.Equal(AiAnalysisSchedulingControlState.WaitingForNetwork, state!.ControlState);
        Assert.Equal("网络暂不可用", state.ControlReason);
    }

    [Fact]
    public async Task AiLedgerAcceptsOnlyCanonicalInitialEntriesAndSha256Fingerprints()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordRequestAsync(
            new AiRequestLedgerEntry("request_bad_digest", "opaque-fingerprint", "batch_01", "style_01", "Queued", null, now, now),
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordRequestAsync(
            new AiRequestLedgerEntry("request_bad_status", "sha256:" + new string('a', 64), "batch_01", "style_01", "Validated", "results/result.json", now, now),
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordRequestAsync(
            new AiRequestLedgerEntry("request_bad_result", "sha256:" + new string('b', 64), "batch_01", "style_01", "Queued", "results/result.json", now, now),
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordRequestAsync(
            new AiRequestLedgerEntry("request_bad_time", "sha256:" + new string('c', 64), "batch_01", "style_01", "Queued", null, now, now.AddTicks(1)),
            CancellationToken.None));
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
    public async Task CorruptValidatedSessionWithLockIsRejectedOnRecovery()
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
            command.CommandText = "INSERT INTO ai_analysis_sessions(session_id,batch_id,style_id,template_snapshot_id,analysis_key,status,selection_revision,allowed_requirement_ids_json,allowed_asset_ids_json,locked_selections_json,has_verified_result,updated_at) VALUES('session_bad_validated','batch_01','style_01','snapshot_01','key_01','Validated',1,'[\"frame_01\"]','[\"asset_01\"]','[{\"requirementId\":\"frame_01\",\"assetId\":\"asset_01\",\"selectionRevision\":1}]',1,$updated);";
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        await using var reopened = new SqliteDesktopStore(options);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reopened.LoadSessionAsync("session_bad_validated", CancellationToken.None));
        Assert.Equal("AI_SESSION_RECORD_CORRUPT", exception.Message);
    }

    [Fact]
    public async Task CorruptSessionBooleanIsRejectedOnRecovery()
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
            command.CommandText = "PRAGMA ignore_check_constraints=ON; INSERT INTO ai_analysis_sessions(session_id,batch_id,style_id,template_snapshot_id,analysis_key,status,selection_revision,allowed_requirement_ids_json,allowed_asset_ids_json,locked_selections_json,has_verified_result,updated_at) VALUES('session_bad_boolean','batch_01','style_01','snapshot_01','key_01','Queued',0,'[\"frame_01\"]','[\"asset_01\"]','[]',2,$updated);";
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await using var reopened = new SqliteDesktopStore(options);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reopened.LoadSessionAsync("session_bad_boolean", CancellationToken.None));
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
        var initial = AiAnalysisSession.Create("session_01", "batch_01", "style_01", "snapshot_01", "key_01", ["frame_01"], ["asset_01", "asset_02"]);
        await store.SaveSessionAsync(initial, CancellationToken.None);
        var uploading = initial.AdvanceTo(AiAnalysisSessionStatus.Uploading);
        await store.SaveSessionAsync(uploading, CancellationToken.None, expectedSelectionRevision: 0);
        var analysing = uploading.AdvanceTo(AiAnalysisSessionStatus.Analysing);
        await store.SaveSessionAsync(analysing, CancellationToken.None, expectedSelectionRevision: 0);
        var session = analysing.AdvanceTo(AiAnalysisSessionStatus.Validated);
        await store.SaveSessionAsync(session, CancellationToken.None, expectedSelectionRevision: 0);
        var locked = session.LockSelection("frame_01", "asset_01", 0);
        await store.SaveSessionAsync(locked, CancellationToken.None, expectedSelectionRevision: 0);
        var replaced = locked.LockSelection("frame_01", "asset_02", 1);
        await store.SaveSessionAsync(replaced, CancellationToken.None, expectedSelectionRevision: 1);
        var stolenAtSameRevision = AiAnalysisSession.Restore(
            "session_01", "batch_01", "style_01", "snapshot_01", "key_01",
            AiAnalysisSessionStatus.ManualReview, 2,
            [new AiLockedSelection("frame_01", "asset_01", 2)], ["frame_01"], ["asset_01", "asset_02"], true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveSessionAsync(
            stolenAtSameRevision, CancellationToken.None, expectedSelectionRevision: 2));
        var closed = replaced.AdvanceTo(AiAnalysisSessionStatus.PlanClosed);
        await store.SaveSessionAsync(closed, CancellationToken.None, expectedSelectionRevision: 2);
        var closedRollback = AiAnalysisSession.Restore(
            "session_01", "batch_01", "style_01", "snapshot_01", "key_01",
            AiAnalysisSessionStatus.ManualReview, 2,
            [new AiLockedSelection("frame_01", "asset_02", 2)], ["frame_01"], ["asset_01", "asset_02"], true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveSessionAsync(
            closedRollback, CancellationToken.None, expectedSelectionRevision: 2));
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

        var terminal = AiAnalysisSession.Create("session_terminal", "batch_01", "style_01", "snapshot_01", "key_terminal", ["frame_01"], ["asset_01"]);
        await store.SaveSessionAsync(terminal, CancellationToken.None);
        terminal = terminal.AdvanceTo(AiAnalysisSessionStatus.Uploading);
        await store.SaveSessionAsync(terminal, CancellationToken.None, expectedSelectionRevision: 0);
        terminal = terminal.AdvanceTo(AiAnalysisSessionStatus.TerminalError);
        await store.SaveSessionAsync(terminal, CancellationToken.None, expectedSelectionRevision: 0);
        var terminalRollback = AiAnalysisSession.Restore(
            "session_terminal", "batch_01", "style_01", "snapshot_01", "key_terminal",
            AiAnalysisSessionStatus.Uploading, 0, [], ["frame_01"], ["asset_01"], false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveSessionAsync(
            terminalRollback, CancellationToken.None, expectedSelectionRevision: 0));
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordUsageAsync(
            usage with { RecordedAt = requestTime.AddTicks(-1) }, CancellationToken.None));
        await store.RecordUsageAsync(usage, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveCacheReferenceAsync(
            cache with { IsReusable = false }, CancellationToken.None));
        await store.SaveCacheReferenceAsync(cache, CancellationToken.None);
        await store.SaveCacheReferenceAsync(cache, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveCacheReferenceAsync(
            cache with { ResultReference = "results/other.json" }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveCacheReferenceAsync(
            cache with { ModelConfigVersion = "model/other" }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveCacheReferenceAsync(
            cache with { CreatedAt = usage.RecordedAt.AddTicks(-1) }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateRequestStatusAsync(
            "request_01", "Validated", "ManualReview", "results/other.json", DateTimeOffset.UtcNow, CancellationToken.None));
        await store.UpdateRequestStatusAsync(
            "request_01", "Validated", "ManualReview", "results/session.json", DateTimeOffset.UtcNow, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateRequestStatusAsync(
            "request_01", "ManualReview", "PlanClosed", "results/other.json", DateTimeOffset.UtcNow, CancellationToken.None));
        await store.RecordUsageAsync(usage, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordUsageAsync(
            usage with { OutputUnits = 3 }, CancellationToken.None));
        Assert.Equal(amount, Assert.Single(await store.LoadUsageAsync("request_01", CancellationToken.None)).ProviderCostAmount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordRequestAsync(
            request with { Fingerprint = "not-a-digest" }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordUsageAsync(
            usage with { RequestId = "missing_request" }, CancellationToken.None));

        await store.RecordRequestAsync(request with { RequestId = "request_02", Fingerprint = "sha256:" + new string('d', 64) }, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordUsageAsync(
            usage with { RequestId = "request_02" }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveCacheReferenceAsync(
            cache with { CacheKey = "sha256:" + new string('e', 64), UsageEventId = "usage_missing" }, CancellationToken.None));
        Assert.True(await store.InvalidateCacheReferenceAsync(cache.CacheKey, CancellationToken.None));
        Assert.False((await store.FindCacheReferenceAsync(cache.CacheKey, CancellationToken.None))!.IsReusable);
        Assert.NotNull(await store.FindUsageEventAsync(cache.UsageEventId, CancellationToken.None));
        Assert.False(await store.InvalidateCacheReferenceAsync(cache.CacheKey, CancellationToken.None));
    }

    [Fact]
    public async Task CacheReadRejectsDanglingUsageAndResultReferences()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO ai_cache_references(cache_key,result_reference,consent_id,model_config_version,usage_event_id,created_at) VALUES($key,'results/result.json','consent','model','usage_missing',$created);";
            command.Parameters.AddWithValue("$key", "sha256:" + new string('f', 64));
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => store.FindCacheReferenceAsync(
            "sha256:" + new string('f', 64), CancellationToken.None));
    }

    [Fact]
    public async Task CorruptLedgerAndUsageRecordsMapToStableRecoveryErrors()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var fingerprint = "sha256:" + new string('a', 64);
        await store.RecordRequestAsync(new AiRequestLedgerEntry(
            "request_01", fingerprint, "batch_01", "style_01", "Queued", null, now, now), CancellationToken.None);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ai_request_ledger SET status='not-a-status' WHERE request_id='request_01';";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => store.FindRequestByFingerprintAsync(fingerprint, CancellationToken.None));

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ai_request_ledger SET status='Queued' WHERE request_id='request_01'; INSERT INTO ai_usage_events(usage_event_id,request_id,model_config_version,input_units,output_units,cache_hit,retry_count,provider_cost_amount_text,provider_cost_currency,recorded_at) VALUES('usage_bad','request_01','model',1,1,0,0,'not-decimal','USD',$recorded);";
            command.Parameters.AddWithValue("$recorded", now.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadUsageAsync("request_01", CancellationToken.None));
    }

    [Fact]
    public async Task AiStateManifestStagesCommitsAndRecoversAfterRestart()
    {
        using var directory = new TestDirectory();
        var manifest = new AiAnalysisStateManifestV1(
            AiAnalysisStateProtocol.Version, "manifest_01", "batch_01", ["session_01"], ["request_01"], ["sha256:" + new string('a', 64)], DateTimeOffset.UtcNow);
        {
            var files = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
            var manifests = new AiAnalysisStateManifestStore(files);
            await manifests.StageAsync(manifest, CancellationToken.None);
            var stagedInspection = await manifests.InspectAsync("batch_01", CancellationToken.None);
            Assert.NotNull(stagedInspection);
            Assert.True(stagedInspection!.IsStaged);
            Assert.False(stagedInspection.IsCommitted);
        }

        AiAnalysisStateManifestInspection? committed;
        {
            var files = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
            var manifests = new AiAnalysisStateManifestStore(files);
            var recoveredStage = (await manifests.InspectAsync("batch_01", CancellationToken.None))!.StagedManifest;
            Assert.NotNull(recoveredStage);
            await manifests.CommitAsync(recoveredStage!, CancellationToken.None);
            committed = await manifests.InspectAsync("batch_01", CancellationToken.None);
        }
        Assert.True(committed!.IsCommitted);
        Assert.False(committed.IsStaged);

        var filesAfterRestart = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        await filesAfterRestart.WriteAtomicallyAsync(committed.CommittedManifestReference!, "tampered"u8.ToArray(), CancellationToken.None);
        var manifestsAfterRestart = new AiAnalysisStateManifestStore(filesAfterRestart);
        await Assert.ThrowsAsync<InvalidDataException>(() => manifestsAfterRestart.InspectAsync("batch_01", CancellationToken.None));
    }

    [Fact]
    public async Task ManifestUpdateKeepsPreviousCommitReadableAndResumesAfterMarkerFailure()
    {
        using var directory = new TestDirectory();
        var backing = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        var manifests = new AiAnalysisStateManifestStore(backing);
        var first = await manifests.StageAsync(new AiAnalysisStateManifestV1(
            AiAnalysisStateProtocol.Version, "manifest_a", "batch_01", ["session_a"], ["request_a"], ["sha256:" + new string('a', 64)], DateTimeOffset.UtcNow), CancellationToken.None);
        await manifests.CommitAsync(first, CancellationToken.None);
        var firstCommit = await manifests.InspectAsync("batch_01", CancellationToken.None);

        var second = await manifests.StageAsync(new AiAnalysisStateManifestV1(
            AiAnalysisStateProtocol.Version, "manifest_b", "batch_01", ["session_b"], ["request_b"], ["sha256:" + new string('b', 64)], DateTimeOffset.UtcNow), CancellationToken.None);
        var failingCommit = new AiAnalysisStateManifestStore(
            new FailingFileStore(backing, path => path.EndsWith("committed.marker", StringComparison.Ordinal)));
        await Assert.ThrowsAsync<IOException>(() => failingCommit.CommitAsync(second, CancellationToken.None));

        var afterFailure = await new AiAnalysisStateManifestStore(backing).InspectAsync("batch_01", CancellationToken.None);
        Assert.NotNull(afterFailure);
        Assert.True(afterFailure!.IsCommitted);
        Assert.True(afterFailure.IsStaged);
        Assert.Equal(firstCommit!.CommittedManifestDigest, afterFailure.CommittedManifestDigest);
        Assert.Equal(second.ManifestDigest, afterFailure.StagedManifest!.ManifestDigest);

        var restarted = new AiAnalysisStateManifestStore(new AtomicFileStore(new LocalAppPathPolicy(directory.Path)));
        var recoveredStage = (await restarted.InspectAsync("batch_01", CancellationToken.None))!.StagedManifest;
        await restarted.CommitAsync(recoveredStage!, CancellationToken.None);
        await restarted.CommitAsync(recoveredStage!, CancellationToken.None);
        var recovered = await restarted.InspectAsync("batch_01", CancellationToken.None);
        Assert.True(recovered!.IsCommitted);
        Assert.False(recovered.IsStaged);
        Assert.Equal(second.ManifestDigest, recovered.CommittedManifestDigest);
        Assert.Null(await backing.ReadAsync(firstCommit.CommittedManifestReference!, CancellationToken.None));
    }

    [Fact]
    public async Task ManifestRollbackDoesNotDeleteNewerStageAndRejectsForgedStagedRecord()
    {
        using var directory = new TestDirectory();
        var files = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        var manifests = new AiAnalysisStateManifestStore(files);
        var first = await manifests.StageAsync(new AiAnalysisStateManifestV1(
            AiAnalysisStateProtocol.Version, "manifest_a", "batch_01", ["session_a"], ["request_a"], ["sha256:" + new string('a', 64)], DateTimeOffset.UtcNow), CancellationToken.None);
        var second = await manifests.StageAsync(new AiAnalysisStateManifestV1(
            AiAnalysisStateProtocol.Version, "manifest_b", "batch_01", ["session_b"], ["request_b"], ["sha256:" + new string('b', 64)], DateTimeOffset.UtcNow), CancellationToken.None);

        await manifests.RollbackAsync(first, CancellationToken.None);
        var inspection = await manifests.InspectAsync("batch_01", CancellationToken.None);
        Assert.NotNull(inspection);
        Assert.True(inspection!.IsStaged);
        Assert.Equal(second.ManifestDigest, inspection.StagedManifest!.ManifestDigest);

        await Assert.ThrowsAsync<InvalidDataException>(() => manifests.CommitAsync(
            second with { ManifestReference = "batches/other/ai-analysis/state/manifest.json" }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => manifests.RollbackAsync(
            second with { StageToken = "../escape" }, CancellationToken.None));
    }

    [Fact]
    public async Task ManifestStageWriteFailureRollsBackOwnedStage()
    {
        using var directory = new TestDirectory();
        var backing = new AtomicFileStore(new LocalAppPathPolicy(directory.Path));
        var failing = new FailingFileStore(backing, path => path.EndsWith("staged.marker", StringComparison.Ordinal));
        var manifests = new AiAnalysisStateManifestStore(failing);
        var manifest = new AiAnalysisStateManifestV1(
            AiAnalysisStateProtocol.Version, "manifest_fail", "batch_01", ["session_01"], ["request_01"], ["sha256:" + new string('c', 64)], DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<IOException>(() => manifests.StageAsync(manifest, CancellationToken.None));
        var recovered = new AiAnalysisStateManifestStore(backing);
        Assert.Null(await recovered.InspectAsync("batch_01", CancellationToken.None));

        await Assert.ThrowsAsync<InvalidDataException>(() => recovered.StageAsync(
            manifest with { CreatedAt = default }, CancellationToken.None));
    }

    [Fact]
    public async Task V5DataUpgradesSuccessfullyThroughV14AndPreservesSentinel()
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
            command.CommandText = "DROP TABLE render_output_publication_files; DROP TABLE render_output_publications; DROP TABLE render_output_stages; DROP TABLE render_output_registrations; DROP TABLE input_output_selections; DROP TABLE render_page_repairs; DROP TABLE render_batch_attempt_origins; DROP TABLE render_batch_journals; DROP TABLE render_page_checkpoints; ALTER TABLE template_pages DROP COLUMN size_table_calibration_json; DELETE FROM schema_migrations WHERE version>=6; DROP TABLE IF EXISTS ai_analysis_scheduler_state; DROP TABLE ai_analysis_checkpoint_items; DROP TABLE ai_analysis_checkpoint_journal; DROP TABLE ai_analysis_sessions; DROP TABLE ai_request_ledger; DROP TABLE ai_cache_references; DROP TABLE ai_usage_events; CREATE TABLE v5_sentinel(value TEXT NOT NULL); INSERT INTO v5_sentinel(value) VALUES('preserve-me');";
            await command.ExecuteNonQueryAsync();
        }

        await using (var upgraded = new SqliteDesktopStore(options))
        {
            await upgraded.InitializeAsync();
        }

        await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await verify.OpenAsync();
        await using var versionCommand = verify.CreateCommand();
        versionCommand.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        await using var sentinelCommand = verify.CreateCommand();
        sentinelCommand.CommandText = "SELECT value FROM v5_sentinel;";
        await using var tablesCommand = verify.CreateCommand();
        tablesCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('ai_analysis_sessions','ai_request_ledger','ai_cache_references','ai_usage_events');";
        await using var columnCommand = verify.CreateCommand();
        columnCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ai_usage_events') WHERE name='provider_cost_amount_text';";
        Assert.Equal(21L, (long)(await versionCommand.ExecuteScalarAsync())!);
        Assert.Equal("preserve-me", await sentinelCommand.ExecuteScalarAsync());
        Assert.Equal(4L, (long)(await tablesCommand.ExecuteScalarAsync())!);
        Assert.Equal(1L, (long)(await columnCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task V5UpgradeFailureRollsBackVersionSixAndPreservesSentinel()
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
            command.CommandText = "DROP TABLE render_output_publication_files; DROP TABLE render_output_publications; DROP TABLE render_output_stages; DROP TABLE render_output_registrations; DROP TABLE input_output_selections; DROP TABLE render_page_repairs; DROP TABLE render_batch_attempt_origins; DROP TABLE render_batch_journals; DROP TABLE render_page_checkpoints; ALTER TABLE template_pages DROP COLUMN size_table_calibration_json; DELETE FROM schema_migrations WHERE version>=6; DROP TABLE IF EXISTS ai_analysis_scheduler_state; DROP TABLE ai_analysis_checkpoint_items; DROP TABLE ai_analysis_checkpoint_journal; DROP TABLE ai_analysis_sessions; DROP TABLE ai_request_ledger; DROP TABLE ai_cache_references; DROP TABLE ai_usage_events; CREATE TABLE v5_sentinel(value TEXT NOT NULL); INSERT INTO v5_sentinel(value) VALUES('preserve-me'); CREATE TRIGGER fail_v6 BEFORE INSERT ON schema_migrations WHEN NEW.version=6 BEGIN SELECT RAISE(ABORT, 'TEST_V6_FAILURE'); END;";
            await command.ExecuteNonQueryAsync();
        }

        await using (var failed = new SqliteDesktopStore(options))
        {
            await Assert.ThrowsAsync<SqliteException>(() => failed.InitializeAsync());
        }

        await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await verify.OpenAsync();
        await using var version = verify.CreateCommand();
        version.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        await using var sentinel = verify.CreateCommand();
        sentinel.CommandText = "SELECT value FROM v5_sentinel;";
        Assert.Equal(5L, (long)(await version.ExecuteScalarAsync())!);
        Assert.Equal("preserve-me", await sentinel.ExecuteScalarAsync());
    }

    [Fact]
    public async Task V6DataUpgradesSuccessfullyToV12AndPreservesSentinel()
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
            command.CommandText = "DROP TABLE render_output_publication_files; DROP TABLE render_output_publications; DROP TABLE render_output_stages; DROP TABLE render_output_registrations; DROP TABLE input_output_selections; DROP TABLE render_page_repairs; DROP TABLE render_batch_attempt_origins; DROP TABLE render_batch_journals; DROP TABLE render_page_checkpoints; ALTER TABLE template_pages DROP COLUMN size_table_calibration_json; DELETE FROM schema_migrations WHERE version>=7; DROP TABLE ai_analysis_checkpoint_items; DROP TABLE ai_analysis_checkpoint_journal; DROP TABLE IF EXISTS ai_analysis_scheduler_state; ALTER TABLE ai_cache_references DROP COLUMN is_reusable; CREATE TABLE v6_sentinel(value TEXT NOT NULL); INSERT INTO v6_sentinel(value) VALUES('preserve-v6');";
            await command.ExecuteNonQueryAsync();
        }

        await using (var upgraded = new SqliteDesktopStore(options))
        {
            await upgraded.InitializeAsync();
        }

        await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await verify.OpenAsync();
        await using var version = verify.CreateCommand();
        version.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        await using var sentinel = verify.CreateCommand();
        sentinel.CommandText = "SELECT value FROM v6_sentinel;";
        await using var tables = verify.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('ai_analysis_checkpoint_journal','ai_analysis_checkpoint_items');";
        Assert.Equal(21L, (long)(await version.ExecuteScalarAsync())!);
        Assert.Equal("preserve-v6", await sentinel.ExecuteScalarAsync());
        Assert.Equal(2L, (long)(await tables.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task V7MigrationFailureRollsBackAndLeavesV6Readable()
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
            command.CommandText = "DELETE FROM schema_migrations WHERE version>=7; DROP TABLE IF EXISTS ai_analysis_scheduler_state; DROP TABLE ai_analysis_checkpoint_items; DROP TABLE ai_analysis_checkpoint_journal; CREATE TABLE v6_sentinel(value TEXT NOT NULL); INSERT INTO v6_sentinel(value) VALUES('preserve-v6'); CREATE TRIGGER fail_v7 BEFORE INSERT ON schema_migrations WHEN NEW.version=7 BEGIN SELECT RAISE(ABORT, 'TEST_V7_FAILURE'); END;";
            await command.ExecuteNonQueryAsync();
        }

        await using (var failed = new SqliteDesktopStore(options))
        {
            await Assert.ThrowsAsync<SqliteException>(() => failed.InitializeAsync());
        }

        await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await verify.OpenAsync();
        await using var version = verify.CreateCommand();
        version.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        await using var sentinel = verify.CreateCommand();
        sentinel.CommandText = "SELECT value FROM v6_sentinel;";
        await using var tables = verify.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('ai_analysis_checkpoint_journal','ai_analysis_checkpoint_items');";
        Assert.Equal(6L, (long)(await version.ExecuteScalarAsync())!);
        Assert.Equal("preserve-v6", await sentinel.ExecuteScalarAsync());
        Assert.Equal(0L, (long)(await tables.ExecuteScalarAsync())!);
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

    private sealed class FailingFileStore(IFileStore inner, Func<string, bool> shouldFail) : IFileStore
    {
        public Task WriteAtomicallyAsync(string relativePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            if (shouldFail(relativePath))
            {
                throw new IOException("TEST_ATOMIC_WRITE_FAILURE");
            }

            return inner.WriteAtomicallyAsync(relativePath, content, cancellationToken);
        }

        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken) =>
            inner.ReadAsync(relativePath, cancellationToken);

        public Task<string?> ComputeSha256Async(string relativePath, CancellationToken cancellationToken) =>
            inner.ComputeSha256Async(relativePath, cancellationToken);

        public Task<bool> DeleteAsync(string relativePath, CancellationToken cancellationToken) =>
            inner.DeleteAsync(relativePath, cancellationToken);
    }
}
