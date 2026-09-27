using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Ai;
using Desktop.Application.Rendering;
using Desktop.Application.SizeTables;
using Desktop.Application.Tasks;
using Desktop.Contracts.Validation;
using Desktop.Domain.SizeTables;
using Desktop.Domain.Tasks;
using Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Tests;

public sealed class SizeTableRepairJournalTests
{
    private static readonly RenderPlanValidator Plans = new(new JsonSchemaContractValidator());
    private static readonly int[] AboveLayerPath = [2];

    [Fact]
    public async Task RepairIsAtomicIdempotentAndRecoveredExecutionStaysOnOnePage()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        SizeTablePageRepairCommand command;
        RenderBatchJournal initial;
        long birthRevision;
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var session = new Session(store);
            var preparer = new Preparer();
            await using var coordinator = Coordinator(store, session, preparer);
            initial = await CompleteInitial(coordinator);
            command = await Command(coordinator);
            var prepared = await coordinator.PrepareSizeTableRepairAsync(command, default);
            birthRevision = prepared.Revision;
            Assert.Equal(3, prepared.Version);
            Assert.Equal(3, prepared.Pages.Count);
            Assert.Equal(initial.Pages, prepared.Pages.Take(2));
            Assert.Equal(2, prepared.Pages[^1].Attempt);
            Assert.Equal(2, prepared.RegenerationPageIndex);
            Assert.NotEqual(initial.Pages[0].PlanJson, prepared.Pages[^1].PlanJson);
            Assert.False(RenderPlanRegeneration.PreservesFacts(initial.Pages[0].PlanJson!, prepared.Pages[^1].PlanJson!));
            Assert.Equal(birthRevision, (await coordinator.PrepareSizeTableRepairAsync(command, default)).Revision);
            Assert.Equal(1, preparer.Prepared);
            Assert.Equal(2, session.Submitted.Count);
            Assert.Equal(Desktop.Domain.Tasks.TaskStatus.ReadyToRender, (await new TaskCoordinator(store).LoadAsync("batch_test")).Status);
            var events = await store.LoadEventsAsync("batch_test", default);
            Assert.Single(events, item => item.Type == TaskEventType.RenderPageRepairRequested);
        }
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var session = new Session(store);
            var preparer = new Preparer();
            await using var coordinator = Coordinator(store, session, preparer);
            Assert.Equal(birthRevision, (await coordinator.PrepareSizeTableRepairAsync(command, default)).Revision);
            Assert.Equal(0, preparer.Prepared);
            var done = await coordinator.RunAsync("batch_test", default);
            Assert.Equal(RenderBatchPageStatus.Committed, done.Pages[^1].Status);
            Assert.Equal(initial.Pages, done.Pages.Take(2));
            Assert.Equal("page_1:2", Assert.Single(session.Submitted));
            Assert.Equal(2, (await new TaskCoordinator(store).LoadAsync("batch_test")).OutputCount);
            var table = await coordinator.LoadSizeTableRepairAsync("batch_test", "style_a", "page_1", null, default);
            Assert.Equal("66", table.Document.Rows[0].Cells[1]);
            Assert.Equal(1, table.Document.Revision);
            var regenerated = await coordinator.PrepareRegenerationAsync("batch_test", "style_a", "page_1", null, 2, default);
            Assert.Equal(3, regenerated.Version);
            Assert.Null(regenerated.Pages[^1].SizeTableRepair);
            Assert.True(RenderPlanRegeneration.PreservesFacts(done.Pages[^1].PlanJson!, regenerated.Pages[^1].PlanJson!));
            var again = await coordinator.RunAsync("batch_test", default);
            Assert.Equal(4, again.Pages.Count);
            Assert.Equal(2, (await new TaskCoordinator(store).LoadAsync("batch_test")).OutputCount);
            Assert.Equal("66", (await coordinator.LoadSizeTableRepairAsync("batch_test", "style_a", "page_1", null, default)).Document.Rows[0].Cells[1]);
        }
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("attempt")]
    [InlineData("plan")]
    [InlineData("variant")]
    [InlineData("style")]
    public async Task StaleOrDifferentPageBaselineCannotPrepare(string mutation)
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        var preparer = new Preparer();
        await using var coordinator = Coordinator(store, new Session(store), preparer);
        var original = await CompleteInitial(coordinator);
        var command = await Command(coordinator);
        var target = command.Target;
        target = mutation switch
        {
            "revision" => target with { ExpectedJournalRevision = target.ExpectedJournalRevision - 1 },
            "attempt" => target with { ExpectedAttempt = 2 },
            "plan" => target with { BasePlanDigest = "sha256:" + new string('f', 64) },
            "variant" => target with { OutputVariantId = "other" },
            _ => target with { StyleId = "other" }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PrepareSizeTableRepairAsync(command with { Target = target }, default));
        Assert.Equal(0, preparer.Prepared);
        Assert.Equal(original.Revision, (await coordinator.LoadAsync("batch_test", default))!.Revision);
    }

    [Fact]
    public async Task SameRequestWithDifferentCellsIsRejectedWithoutAnAdditionalAttempt()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        await using var coordinator = Coordinator(store, new Session(store), new Preparer());
        await CompleteInitial(coordinator);
        var command = await Command(coordinator);
        var prepared = await coordinator.PrepareSizeTableRepairAsync(command, default);
        var changed = command with { Draft = command.Draft with { Rows = [new("row_001", ["L", "67"])] } };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PrepareSizeTableRepairAsync(changed, default));
        Assert.Equal("SIZE_REPAIR_REQUEST_CONFLICT", error.Message);
        Assert.Equal(prepared.Revision, (await coordinator.LoadAsync("batch_test", default))!.Revision);
    }

    [Fact]
    public async Task RequestIndexFailureRollsBackJournalOriginEventAndTaskTogether()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await using var coordinator = Coordinator(store, new Session(store), new Preparer());
        var original = await CompleteInitial(coordinator);
        var before = await store.LoadEventsAsync("batch_test", default);
        var command = await Command(coordinator);
        await Sql(options, "CREATE TRIGGER reject_repair BEFORE INSERT ON render_page_repairs BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => coordinator.PrepareSizeTableRepairAsync(command, default));
        Assert.Equal(original.Revision, (await coordinator.LoadAsync("batch_test", default))!.Revision);
        Assert.Equal(before, await store.LoadEventsAsync("batch_test", default));
        Assert.Equal(0L, await Scalar(options, "SELECT COUNT(*) FROM render_batch_attempt_origins;"));
        Assert.Equal(0L, await Scalar(options, "SELECT COUNT(*) FROM render_page_repairs;"));
        await Sql(options, "DROP TRIGGER reject_repair;");
        Assert.Equal(3, (await coordinator.PrepareSizeTableRepairAsync(command, default)).Pages.Count);
    }

    [Theory]
    [InlineData("DELETE FROM render_page_repairs;")]
    [InlineData("UPDATE render_page_repairs SET request_digest='sha256:bad';")]
    [InlineData("UPDATE render_page_repairs SET request_id='different';")]
    [InlineData("PRAGMA foreign_keys=OFF; UPDATE render_page_repairs SET birth_revision=birth_revision+1;")]
    [InlineData("UPDATE task_events SET event_type='RenderPageRegenerationRequested' WHERE event_type='RenderPageRepairRequested';")]
    [InlineData("UPDATE task_events SET payload_digest='sha256:bad' WHERE event_type='RenderPageRepairRequested';")]
    public async Task MissingOrTamperedRepairOriginBlocksRecovery(string sql)
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await using var coordinator = Coordinator(store, new Session(store), new Preparer());
        await CompleteInitial(coordinator);
        await coordinator.PrepareSizeTableRepairAsync(await Command(coordinator), default);
        await Sql(options, sql);
        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.LoadAsync("batch_test", default));
    }

    [Fact]
    public async Task GenericTaskWriterCannotReopenATaskWithoutTheRepairTransaction()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.AppendAsync(
            TaskEvent.Create("batch_test", 1, TaskEventType.RenderPageRepairRequested, "style/page", "sha256:" + new string('a', 64)),
            TaskState.Empty("batch_test"), default));
        Assert.Empty(await store.LoadEventsAsync("batch_test", default));
    }

    [Fact]
    public async Task ControlRevisionChangedDuringPreparationLeavesCandidateUnsubmitted()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        var preparer = new Preparer();
        var session = new Session(store);
        await using var coordinator = Coordinator(store, session, preparer);
        await CompleteInitial(coordinator);
        var command = await Command(coordinator);
        preparer.BeforePrepare = () => coordinator.RequestPauseAsync("batch_test", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PrepareSizeTableRepairAsync(command, default));
        Assert.Equal(2, (await coordinator.LoadAsync("batch_test", default))!.Pages.Count);
        Assert.Equal(2, session.Submitted.Count);
    }

    [Fact]
    public async Task UnknownPageBlocksRepairOfEveryPageWithoutDispatch()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        var session = new Session(store) { UnknownPage = "page_2" };
        var preparer = new Preparer();
        await using var coordinator = Coordinator(store, session, preparer);
        await coordinator.PrepareAsync(Preparation(), default);
        var unknown = await coordinator.RunAsync("batch_test", default);
        Assert.Equal(RenderBatchStatus.RecoveryRequired, unknown.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.LoadSizeTableRepairAsync("batch_test", "style_a", "page_1", null, default));
        Assert.Equal(0, preparer.Prepared);
        Assert.Equal(2, session.Submitted.Count);
    }

    [Fact]
    public async Task MigrationFromV17PreservesExistingJournalBytesAndTaskEvents()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        string payload;
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            await using var coordinator = Coordinator(store, new Session(store), new Preparer());
            await CompleteInitial(coordinator);
            payload = JsonSerializer.Serialize(await store.LoadEventsAsync("batch_test", default));
        }
        var hash = await Scalar(options, "SELECT group_concat(payload_sha256,'|') FROM render_batch_journals ORDER BY revision;");
        await Sql(options, "DROP TABLE render_output_publication_files; DROP TABLE render_output_publications; DROP TABLE render_output_stages; DROP TABLE render_output_registrations; DROP TABLE input_output_selections; DROP TABLE render_page_repairs; DELETE FROM schema_migrations WHERE version>=18;");
        await using var migrated = new SqliteDesktopStore(options);
        await migrated.InitializeAsync();
        Assert.Equal(hash, await Scalar(options, "SELECT group_concat(payload_sha256,'|') FROM render_batch_journals ORDER BY revision;"));
        Assert.Equal(payload, JsonSerializer.Serialize(await migrated.LoadEventsAsync("batch_test", default)));
        Assert.Equal(1, (await migrated.LoadRenderBatchAsync("batch_test", default))!.Version);
        Assert.Equal(21L, await Scalar(options, "SELECT MAX(version) FROM schema_migrations;"));
    }

    private static RenderBatchCoordinator Coordinator(SqliteDesktopStore store, Session session, Preparer preparer) =>
        new(store, session, Plans, new TaskCoordinator(store), TimeProvider.System, preparer, new Admission());

    private sealed class Admission : ISizeTableRepairAdmission, IDisposable
    {
        public Task<IDisposable> AcquireAsync(string batchId, CancellationToken cancellationToken) => Task.FromResult<IDisposable>(this);
        public void Dispose() { }
    }

    private static async Task<RenderBatchJournal> CompleteInitial(RenderBatchCoordinator coordinator)
    {
        await coordinator.PrepareAsync(Preparation(), default);
        var initial = await coordinator.RunAsync("batch_test", default);
        Assert.Equal(RenderBatchStatus.Completed, initial.Status);
        return initial;
    }

    private static async Task<SizeTablePageRepairCommand> Command(RenderBatchCoordinator coordinator)
    {
        var baseline = await coordinator.LoadSizeTableRepairAsync("batch_test", "style_a", "page_1", null, default);
        return new("repair_one", baseline.Target, new(baseline.Document.Columns, [new("row_001", ["L", "66"])]));
    }

    private static SizeTable OriginalTable() => SizeTable.FromCandidate("size_repair", "batch_test", "style_a", "page_1",
        SizeTableCandidate.Create("candidate", SizeTableSource.Create("source_size", "sha256:" + new string('d', 64)),
            1m, ["尺码", "衣长"], [["L", "65"]]), DateTimeOffset.Parse("2026-09-24T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    private static AiRenderPlanPreparation Preparation()
    {
        var ordinary = RenderBatchCoordinatorTests.Preparation().Pages[1];
        var node = JsonNode.Parse(RenderBatchCoordinatorTests.Preparation().Pages[0].RenderPlanJson!)!;
        var reference = node["targets"]![0]!["expected_layer"]!["structure_signature"]!.GetValue<string>();
        node["document"]!["content_type"] = "dynamic_size_table";
        node["targets"] = new JsonArray();
        node["required_capabilities"]!.AsArray().Add(RenderPlanPlacementMigration.Capability);
        node["required_capabilities"]!.AsArray().Add(RenderPlanPlacementMigration.SizeInsertionCapability);
        node["size_table"] = new JsonObject
        {
            ["style_version"] = "style_v1", ["layout_version"] = "grid_v1", ["embed_as"] = "smart_object",
            ["insertion"] = new JsonObject
            {
                ["layer_name"] = "confirmed_size_table",
                ["above_layer"] = JsonSerializer.SerializeToNode(new { layer_path = AboveLayerPath, layer_id = 257,
                    kind = "group", name = "old_table", structure_signature = reference, layer_path_order = "psd_bottom_to_top" }),
                ["hidden_layers"] = new JsonArray(JsonSerializer.SerializeToNode(new { layer_path = AboveLayerPath, layer_id = 257,
                    kind = "group", name = "old_table", structure_signature = reference, layer_path_order = "psd_bottom_to_top" })),
                ["safe_bounds"] = new JsonObject { ["left"] = 10, ["top"] = 20, ["right"] = 780, ["bottom"] = 1200 },
                ["placement"] = new JsonObject { ["source_width"] = 1000, ["source_height"] = 400,
                    ["coordinate_space"] = "document_pixels", ["clipping"] = "preserve_template_masks_and_canvas",
                    ["left"] = 10, ["top"] = 456, ["width"] = 770, ["height"] = 308 }
            }
        };
        PutTable(node, OriginalTable());
        var json = node.ToJsonString();
        var validation = Plans.Validate(json);
        Assert.True(validation.IsValid, JsonSerializer.Serialize(validation));
        return new("batch_test", AiRenderPreparationStatus.ReadyToRender, "READY",
            [new("style_a", "page_1", AiRenderPagePreparationStatus.ReadyToRender, "AI_RENDER_PLAN_VALID", json,
                node["plan_digest"]!.GetValue<string>()), ordinary]);
    }

    private static void PutTable(JsonNode plan, SizeTable table)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(table.DataDigest)));
        var resourceId = "size_" + hash[..24];
        plan["resources"]![1] = new JsonObject { ["resource_id"] = resourceId, ["role"] = "deterministic_size_table",
            ["media_type"] = "image/png", ["sha256"] = hash, ["byte_length"] = 1024 };
        var size = plan["size_table"]!;
        size["size_table_id"] = table.TableId;
        size["grid_digest"] = "sha256:" + hash;
        size["render_resource_id"] = resourceId;
        size["structured_data_digest"] = table.DataDigest;
        size["columns"] = JsonSerializer.SerializeToNode(table.Columns.Select((column, index) => new
            { column_id = column.ColumnId, label = column.SourceText, index }));
        size["rows"] = JsonSerializer.SerializeToNode(table.Rows.Select((row, index) => new
            { row_id = row.RowId, index, cells = row.Cells.ToDictionary(cell => cell.ColumnId, cell => cell.SourceText, StringComparer.Ordinal) }));
        plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
    }

    private sealed class Preparer : ISizeTablePageRepairPreparer
    {
        public int Prepared { get; private set; }
        public Func<Task>? BeforePrepare { get; set; }
        public Task<SizeTable> LoadBaseAsync(RenderBatchJournal journal, RenderBatchPage page, CancellationToken cancellationToken)
        {
            var repair = journal.Pages.Where(item => RenderBatchJournalValidator.SamePage(item, page)
                && item.Attempt <= page.Attempt && item.SizeTableRepair is not null).MaxBy(item => item.Attempt)?.SizeTableRepair;
            return Task.FromResult(repair is null ? OriginalTable() : SizeTableRepairData.Deserialize(repair.TableJson));
        }
        public async Task<PreparedSizeTableRepair> PrepareAsync(SizeTablePageRepairCommand command,
            RenderBatchJournal journal, RenderBatchPage page, DateTimeOffset createdAt, CancellationToken cancellationToken)
        {
            Prepared++;
            if (BeforePrepare is not null) await BeforePrepare();
            var before = await LoadBaseAsync(journal, page, cancellationToken);
            var after = SizeTableRepairData.ApplyDraft(before, command.Draft, createdAt);
            var plan = JsonNode.Parse(RenderPlanRegeneration.Create(page.PlanJson!, createdAt))!;
            PutTable(plan, after);
            return new(plan.ToJsonString(), new(command.RequestId, SizeTableRepairData.RequestDigest(command),
                command.Target.ExpectedJournalRevision, page.Attempt, command.Target.BasePlanDigest,
                SizeTableRepairData.Serialize(before), SizeTableRepairData.Serialize(after)));
        }
    }

    private sealed class Session(SqliteDesktopStore store) : IRenderBatchSession, IRenderBatchSessionFactory
    {
        public List<string> Submitted { get; } = [];
        public string? UnknownPage { get; init; }
        public Task<IRenderBatchSession> OpenAsync(RenderBatchJournal journal, CancellationToken cancellationToken) => Task.FromResult<IRenderBatchSession>(this);
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<RenderPageObservation> SubmitAsync(RenderBatchPage page, CancellationToken cancellationToken)
        {
            Submitted.Add(page.PageId + ":" + page.Attempt);
            return Task.FromResult(new RenderPageObservation(RenderBatchPageStatus.Running, "RUNNING", "exec_" + page.PageId + "_" + page.Attempt));
        }
        public async Task<RenderPageObservation> ObserveAsync(RenderBatchPage page, bool recovering, CancellationToken cancellationToken)
        {
            if (recovering) return new(page.Status, page.ReasonCode, page.ExecutionId, page.Commit);
            if (page.PageId == UnknownPage) return new(RenderBatchPageStatus.Unknown, "UNKNOWN", page.ExecutionId);
            var digest = JsonNode.Parse(page.PlanJson!)!["plan_digest"]!.GetValue<string>();
            var checkpoint = new RenderPageCommitCheckpoint(new("batch_test", page.StyleId, page.PageId, "render_output", page.Attempt,
                digest, page.ExecutionId, OutputCommitState.Verified, DateTimeOffset.UtcNow, page.OutputVariantId), "render:" + digest[7..],
                "outputs/" + page.PageId + "/" + page.Attempt + "/manifest.json", 100, new string('a', 64));
            await store.PrepareRenderCommitAsync(checkpoint, cancellationToken);
            await store.CompleteRenderCommitAsync(checkpoint, cancellationToken);
            return new(RenderBatchPageStatus.Committed, "COMMITTED", page.ExecutionId, checkpoint);
        }
        public Task CancelAsync(RenderBatchPage page, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task Sql(DesktopDataOptions options, string sql) => _ = await Scalar(options, sql);
    private static async Task<object?> Scalar(DesktopDataOptions options, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
