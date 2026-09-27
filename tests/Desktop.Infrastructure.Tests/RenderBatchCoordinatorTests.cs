using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Ai;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;
using Desktop.Contracts.Validation;
using Desktop.Domain.Tasks;
using Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Tests;

public sealed class RenderBatchCoordinatorTests
{
    [Theory]
    [InlineData("PHOTOSHOP_HOST_SELECTION_REQUIRED", false)]
    [InlineData("PHOTOSHOP_HOST_SELECTION_INVALID", false)]
    [InlineData("PHOTOSHOP_HOST_SELECTION_REQUIRED", true)]
    [InlineData("PHOTOSHOP_HOST_SELECTION_INVALID", true)]
    public async Task HostSelectionPreconditionPreservesPreparedOrUnknownJournalWithoutSubmitting(string code, bool unknown)
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        var backend = new ScriptedSession(store) { UnknownPage = unknown ? "page_1" : null };
        await using var coordinator = Coordinator(store, backend);
        var before = await coordinator.PrepareAsync(Preparation(), default);
        if (unknown) before = await coordinator.RunAsync("batch_test", default);
        var submitted = backend.Submitted.Count;
        backend.OpenFailure = code;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync("batch_test", default));
        Assert.Equal(code, error.Message);
        var after = await coordinator.LoadAsync("batch_test", default);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        Assert.Equal(submitted, backend.Submitted.Count);
        Assert.Equal(unknown ? RenderBatchStatus.RecoveryRequired : RenderBatchStatus.Prepared, after!.Status);
    }

    [Fact]
    public async Task InvalidDataDuringSubmissionPersistsRecoveryInsteadOfLeavingSubmitting()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        var backend = new ScriptedSession(store)
        {
            OnSubmit = _ => throw new InvalidDataException("BRIDGE_RESOURCE_BINDING_INVALID")
        };
        await using var coordinator = Coordinator(store, backend);
        await coordinator.PrepareAsync(Preparation(), default);
        var result = await coordinator.RunAsync("batch_test", default);
        Assert.Equal(RenderBatchStatus.RecoveryRequired, result.Status);
        Assert.Equal(RenderBatchPageStatus.Unknown, result.Pages[0].Status);
        Assert.Equal(result.Revision, (await coordinator.LoadAsync("batch_test", default))!.Revision);
        Assert.Single(backend.Submitted);
        Assert.All(result.Pages.Skip(1), page => Assert.Equal(RenderBatchPageStatus.Planned, page.Status));
    }

    [Theory]
    [InlineData("history")]
    [InlineData("attempt")]
    [InlineData("scope")]
    [InlineData("resource")]
    [InlineData("identity")]
    [InlineData("preparation")]
    public async Task AppendRejectsRewritingHistoryFactsOrExecutionScope(string mutation)
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        await using var coordinator = Coordinator(store, new ScriptedSession(store));
        await coordinator.PrepareAsync(Preparation(), default);
        var original = await coordinator.RunAsync("batch_test", default);
        var prior = original.Pages[0];
        var plan = JsonNode.Parse(RenderPlanRegeneration.Create(prior.PlanJson!, DateTimeOffset.UtcNow))!;
        if (mutation == "resource") plan["resources"]![0]!["sha256"] = new string('a', 64);
        if (mutation == "identity") plan["plan_id"] = JsonNode.Parse(prior.PlanJson!)!["plan_id"]!.GetValue<string>();
        plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
        var added = prior with { Attempt = mutation == "attempt" ? 3 : 2, PlanJson = plan.ToJsonString(),
            Status = RenderBatchPageStatus.Planned, ExecutionId = null, Commit = null, ReasonCode = prior.PreparationReasonCode!,
            PreparationReasonCode = mutation == "preparation" ? "changed" : prior.PreparationReasonCode };
        var history = original.Pages.ToArray();
        if (mutation == "history") history[0] = prior with { ReasonCode = "changed" };
        var candidate = original with { Version = 2, Revision = original.Revision + 1, Status = RenderBatchStatus.Prepared,
            RegenerationPageIndex = mutation == "scope" ? 0 : original.Pages.Count, Pages = [.. history, added] };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveRenderBatchAsync(candidate, original.Revision, default));
        Assert.Equal(original.Revision, (await coordinator.LoadAsync("batch_test", default))!.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegenerationAppendsOneAttemptPreservesHistoryAndDoesNotDoubleCount(bool failedFirst)
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var backend = new ScriptedSession(store) { FailPage = failedFirst ? "page_1" : null };
            await using var coordinator = Coordinator(store, backend);
            await coordinator.PrepareAsync(Preparation(), default);
            var original = await coordinator.RunAsync("batch_test", default);
            var prepared = await coordinator.PrepareRegenerationAsync("batch_test", "style_a", "page_1", null, 1, default);
            Assert.Equal(2, prepared.Version);
            Assert.Equal(original.Pages, prepared.Pages.Take(4));
            Assert.Equal(2, prepared.Pages[^1].Attempt);
            Assert.Equal(Desktop.Domain.Tasks.TaskStatus.ReadyToRender, (await new TaskCoordinator(store).LoadAsync("batch_test")).Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PrepareRegenerationAsync("batch_test", "style_a", "page_1", null, 1, default));
            backend.FailPage = null;
            var result = await coordinator.RunAsync("batch_test", default);
            Assert.Equal(RenderBatchPageStatus.Committed, result.Pages[^1].Status);
            Assert.Equal(original.Pages, result.Pages.Take(4));
            Assert.Equal(5, backend.Submitted.Count);
            Assert.Equal(4, (await new TaskCoordinator(store).LoadAsync("batch_test")).OutputCount);
            await coordinator.PrepareRegenerationAsync("batch_test", "style_a", "page_1", null, 2, default);
            var again = await coordinator.RunAsync("batch_test", default);
            Assert.Equal(3, again.Pages[^1].Attempt);
            Assert.Equal(4, (await new TaskCoordinator(store).LoadAsync("batch_test")).OutputCount);
        }
        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var restoredBackend = new ScriptedSession(reopened);
        await using var restored = Coordinator(reopened, restoredBackend);
        var recovered = await restored.RunAsync("batch_test", default);
        Assert.Equal(6, recovered.Pages.Count);
        Assert.Empty(restoredBackend.Submitted);
        Assert.Equal(4, (await new TaskCoordinator(reopened).LoadAsync("batch_test")).OutputCount);
    }

    [Fact]
    public async Task CancelledBatchRegenerationNeverDispatchesOtherPlannedPages()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        var backend = new ScriptedSession(store);
        await using var coordinator = Coordinator(store, backend);
        backend.OnSubmit = _ => coordinator.RequestCancelAsync("batch_test", default);
        await coordinator.PrepareAsync(Preparation(), default);
        var original = await coordinator.RunAsync("batch_test", default);
        backend.OnSubmit = null;
        await coordinator.PrepareRegenerationAsync("batch_test", "style_a", "page_1", null, 1, default);
        var result = await coordinator.RunAsync("batch_test", default);
        Assert.Equal(original.Pages, result.Pages.Take(4));
        Assert.Equal(["page_1", "page_1"], backend.Submitted);
        Assert.Equal(RenderBatchPageStatus.Committed, result.Pages[^1].Status);
        Assert.Equal(1, (await new TaskCoordinator(store).LoadAsync("batch_test")).OutputCount);
        await coordinator.RunAsync("batch_test", default);
        Assert.Equal(2, backend.Submitted.Count);
    }

    [Fact]
    public async Task UnknownExecutionBlocksRegenerationAndLeavesJournalAndTaskUntouched()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        var backend = new ScriptedSession(store) { UnknownPage = "page_1" };
        await using var coordinator = Coordinator(store, backend);
        await coordinator.PrepareAsync(Preparation(), default);
        var original = await coordinator.RunAsync("batch_test", default);
        var events = await store.LoadEventsAsync("batch_test", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PrepareRegenerationAsync("batch_test", "style_a", "page_1", null, 1, default));
        Assert.Equal(original.Revision, (await coordinator.LoadAsync("batch_test", default))!.Revision);
        Assert.Equal(events, await store.LoadEventsAsync("batch_test", default));
    }

    [Fact]
    public async Task RegenerationTaskEventAndJournalRollbackTogetherWhenOriginCannotBeWritten()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await using var coordinator = Coordinator(store, new ScriptedSession(store));
        await coordinator.PrepareAsync(Preparation(), default);
        var original = await coordinator.RunAsync("batch_test", default);
        var events = await store.LoadEventsAsync("batch_test", default);
        await Sql(options, "CREATE TRIGGER reject_origin BEFORE INSERT ON render_batch_attempt_origins BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => coordinator.PrepareRegenerationAsync("batch_test", "style_a", "page_1", null, 1, default));
        Assert.Equal(original.Revision, (await coordinator.LoadAsync("batch_test", default))!.Revision);
        Assert.Equal(events, await store.LoadEventsAsync("batch_test", default));
        Assert.Equal(Desktop.Domain.Tasks.TaskStatus.Completed, (await new TaskCoordinator(store).LoadAsync("batch_test")).Status);
    }

    [Theory]
    [InlineData("DELETE FROM render_batch_attempt_origins;")]
    [InlineData("UPDATE render_batch_journals SET payload_sha256='bad' WHERE revision=(SELECT birth_revision FROM render_batch_attempt_origins);")]
    [InlineData("UPDATE task_events SET payload_digest='sha256:bad' WHERE event_type='RenderPageRegenerationRequested';")]
    public async Task MissingOrCorruptRegenerationOriginsBlockRecovery(string corrupt)
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await using var coordinator = Coordinator(store, new ScriptedSession(store));
        await coordinator.PrepareAsync(Preparation(), default);
        await coordinator.RunAsync("batch_test", default);
        await coordinator.PrepareRegenerationAsync("batch_test", "style_a", "page_1", null, 1, default);
        await coordinator.RunAsync("batch_test", default);
        await Sql(options, corrupt);
        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.LoadAsync("batch_test", default));
    }

    [Fact]
    public async Task PreparationReviewAndVariantNameSurviveCommitAndCannotBeRewritten()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        await using var coordinator = Coordinator(store, new ScriptedSession(store));
        var preparation = Preparation();
        preparation = preparation with { Pages = preparation.Pages.Select(page => page with
        { ReasonCode = "AI_RENDER_PLAN_VALID_LAYOUT_FALLBACK", OutputVariantName = "米白" }).ToArray() };
        await coordinator.PrepareAsync(preparation, default);
        var result = await coordinator.RunAsync("batch_test", default);
        Assert.All(result.Pages, page => Assert.Equal("RENDER_PAGE_COMMITTED", page.ReasonCode));
        var restored = (await coordinator.LoadAsync("batch_test", default))!;
        Assert.All(restored.Pages, page =>
        {
            Assert.Equal("AI_RENDER_PLAN_VALID_LAYOUT_FALLBACK", page.PreparationReasonCode);
            Assert.Equal("米白", page.OutputVariantName);
        });
        foreach (var rewritten in new[]
        {
            restored.Pages[0] with { PreparationReasonCode = "AI_RENDER_PLAN_VALID" },
            restored.Pages[0] with { OutputVariantName = "黑色" }
        })
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveRenderBatchAsync(restored with
            { Revision = restored.Revision + 1, Pages = [rewritten, .. restored.Pages.Skip(1)] }, restored.Revision, default));
    }

    [Fact]
    public async Task LegacyPreparationIsReadFromFirstRevisionWithoutChangingHistoricalPayloads()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var preparation = Preparation();
        var initial = new RenderBatchJournal(1, "batch_test", 1, RenderBatchStatus.Prepared, false, false,
            preparation.Pages.Select(page => new RenderBatchPage(page.StyleId, page.PageId, page.OutputVariantId, 1,
                page.RenderPlanJson, RenderBatchPageStatus.Planned, "AI_RENDER_PLAN_VALID_LAYOUT_FALLBACK")).ToArray());
        var latest = initial with { Revision = 2, Status = RenderBatchStatus.RecoveryRequired,
            Pages = initial.Pages.Select(page => page with { Status = RenderBatchPageStatus.Unknown, ReasonCode = "RENDER_RECOVERY_REQUIRED" }).ToArray() };
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        foreach (var journal in new[] { initial, latest })
        {
            var json = JsonSerializer.SerializeToNode(journal)!;
            foreach (var page in json["Pages"]!.AsArray())
            { page!.AsObject().Remove("PreparationReasonCode"); page.AsObject().Remove("OutputVariantName"); }
            var payload = json.ToJsonString();
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO render_batch_journals VALUES($batch,$revision,$json,$hash);";
            insert.Parameters.AddWithValue("$batch", journal.BatchId); insert.Parameters.AddWithValue("$revision", journal.Revision);
            insert.Parameters.AddWithValue("$json", payload);
            insert.Parameters.AddWithValue("$hash", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))));
            await insert.ExecuteNonQueryAsync();
        }
        async Task<string> History()
        {
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT group_concat(payload_json || payload_sha256, '') FROM (SELECT * FROM render_batch_journals WHERE revision<=2 ORDER BY revision);";
            return (string)(await query.ExecuteScalarAsync())!;
        }
        var before = await History();
        var restored = (await store.LoadRenderBatchAsync("batch_test", default))!;
        Assert.All(restored.Pages, page =>
        { Assert.Equal("AI_RENDER_PLAN_VALID_LAYOUT_FALLBACK", page.PreparationReasonCode); Assert.Null(page.OutputVariantName); });
        Assert.Equal(before, await History());
        await store.SaveRenderBatchAsync(restored with { Revision = 3 }, 2, default);
        Assert.Equal(before, await History());
        Assert.Equal(3, (await store.LoadRenderBatchAsync("batch_test", default))!.Revision);
        await Sql(options, "UPDATE render_batch_journals SET payload_sha256='corrupt' WHERE revision=1;");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadRenderBatchAsync("batch_test", default));
    }

    [Fact]
    public async Task ThirdPageFailureKeepsFirstTwoAndContinuesNextStyleThenRecoveryDoesNotSubmit()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using (var store = new SqliteDesktopStore(options))
        {
            await store.InitializeAsync();
            var backend = new ScriptedSession(store) { FailPage = "page_3" };
            await using var coordinator = Coordinator(store, backend);
            await coordinator.PrepareAsync(Preparation(), default);
            var result = await coordinator.RunAsync("batch_test", default);
            Assert.Equal(RenderBatchStatus.CompletedWithIssues, result.Status);
            Assert.Equal(3, result.Pages.Count(page => page.Status == RenderBatchPageStatus.Committed));
            Assert.Equal(RenderBatchPageStatus.Failed, result.Pages[2].Status);
            Assert.Equal(3, (await new TaskCoordinator(store).LoadAsync("batch_test")).OutputCount);
            Assert.Equal(4, backend.Submitted.Count);
        }
        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var resumedBackend = new ScriptedSession(reopened);
        await using var resumed = Coordinator(reopened, resumedBackend);
        var restored = await resumed.RunAsync("batch_test", default);
        Assert.Equal(RenderBatchStatus.CompletedWithIssues, restored.Status);
        Assert.Empty(resumedBackend.Submitted);
        Assert.Equal(4, resumedBackend.Recovered);
        Assert.Equal(3, (await new TaskCoordinator(reopened).LoadAsync("batch_test")).OutputCount);
    }

    [Fact]
    public async Task PauseFinishesCurrentStyleAndResumeSkipsItsCommittedPages()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        var backend = new ScriptedSession(store);
        await using var coordinator = Coordinator(store, backend);
        backend.OnSubmit = page => page.PageId == "page_1" ? coordinator.RequestPauseAsync("batch_test", default) : Task.CompletedTask;
        await coordinator.PrepareAsync(Preparation(), default);
        var paused = await coordinator.RunAsync("batch_test", default);
        Assert.Equal(RenderBatchStatus.Paused, paused.Status);
        Assert.Equal(3, backend.Submitted.Count);
        Assert.Equal(RenderBatchPageStatus.Planned, paused.Pages[3].Status);
        Assert.Equal(Desktop.Domain.Tasks.TaskStatus.Paused, (await new TaskCoordinator(store).LoadAsync("batch_test")).Status);
        var completed = await coordinator.RunAsync("batch_test", default);
        Assert.Equal(RenderBatchStatus.Completed, completed.Status);
        Assert.Equal(4, backend.Submitted.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousDeliveryOrSaveFailureNeverResubmitsOrStartsFollowingPage(bool saveFailure)
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        var backend = new ScriptedSession(store) { UnknownPage = "page_1" };
        await using var coordinator = Coordinator(store, backend);
        await coordinator.PrepareAsync(Preparation(), default);
        if (saveFailure)
            backend.OnSubmit = async _ =>
            {
                await Sql(options, "CREATE TRIGGER fail_batch_save BEFORE INSERT ON render_batch_journals BEGIN SELECT RAISE(ABORT, 'injected'); END;");
            };
        if (saveFailure)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync("batch_test", default));
            await Sql(options, "DROP TRIGGER fail_batch_save;");
        }
        else Assert.Equal(RenderBatchStatus.RecoveryRequired, (await coordinator.RunAsync("batch_test", default)).Status);
        var restored = await coordinator.RunAsync("batch_test", default);
        Assert.Equal(RenderBatchStatus.RecoveryRequired, restored.Status);
        Assert.Single(backend.Submitted);
        Assert.All(restored.Pages.Skip(1), page => Assert.Equal(RenderBatchPageStatus.Planned, page.Status));
    }

    [Fact]
    public async Task PauseDoesNotSplitCurrentStyleWhileNextPageWaitsForHost()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        var backend = new ScriptedSession(store);
        await using var coordinator = Coordinator(store, backend);
        var checks = 0;
        backend.OnReady = () => Task.FromResult(++checks != 2);
        backend.OnSubmit = page => page.PageId == "page_1"
            ? coordinator.RequestPauseAsync("batch_test", default) : Task.CompletedTask;
        await coordinator.PrepareAsync(Preparation(), default);

        var result = await coordinator.RunAsync("batch_test", default);

        Assert.Equal(RenderBatchStatus.Paused, result.Status);
        Assert.Equal(["page_1", "page_2", "page_3"], backend.Submitted);
        Assert.Equal(RenderBatchPageStatus.Planned, result.Pages[3].Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlRequestedDuringReadinessIsObservedBeforeFirstDelivery(bool cancel)
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        var backend = new ScriptedSession(store);
        await using var coordinator = Coordinator(store, backend);
        backend.OnReady = async () =>
        {
            if (cancel) await coordinator.RequestCancelAsync("batch_test", default);
            else await coordinator.RequestPauseAsync("batch_test", default);
            return true;
        };
        await coordinator.PrepareAsync(Preparation(), default);

        var result = await coordinator.RunAsync("batch_test", default);

        Assert.Equal(cancel ? RenderBatchStatus.Cancelled : RenderBatchStatus.Paused, result.Status);
        Assert.Empty(backend.Submitted);
        Assert.All(result.Pages, page => Assert.Equal(RenderBatchPageStatus.Planned, page.Status));
    }

    [Fact]
    public async Task CancellationWaitsForPageAcknowledgementAndLeavesFollowingPageUnsent()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new(directory.Path));
        await store.InitializeAsync();
        var backend = new ScriptedSession(store);
        await using var coordinator = Coordinator(store, backend);
        backend.OnSubmit = _ => coordinator.RequestCancelAsync("batch_test", default);
        await coordinator.PrepareAsync(Preparation(), default);
        var result = await coordinator.RunAsync("batch_test", default);
        Assert.Equal(RenderBatchStatus.Cancelled, result.Status);
        Assert.Equal(RenderBatchPageStatus.Cancelled, result.Pages[0].Status);
        Assert.Single(backend.Submitted);
        Assert.Equal(1, backend.CancelCalls);
        Assert.Equal(0, (await new TaskCoordinator(store).LoadAsync("batch_test")).OutputCount);
    }

    [Fact]
    public async Task FrozenJournalRejectsStaleWritePlanReplacementAndCorruptPayload()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await using var coordinator = Coordinator(store, new ScriptedSession(store));
        var original = await coordinator.PrepareAsync(Preparation(), default);
        var renamed = Preparation() with { Pages = Preparation().Pages.Select(page =>
        {
            var plan = JsonNode.Parse(page.RenderPlanJson!)!;
            plan["output"]!["jpg"]!["file_name"] = "01-详情页-红色.jpg";
            if (plan["output"]!["editable_psd"]!["enabled"]!.GetValue<bool>())
                plan["output"]!["editable_psd"]!["file_name"] = "01-详情页-红色.psd";
            plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
            Assert.True(new RenderPlanValidator(new JsonSchemaContractValidator()).Validate(plan.ToJsonString()).IsValid);
            return page with { RenderPlanJson = plan.ToJsonString(), PlanDigest = plan["plan_digest"]!.GetValue<string>() };
        }).ToArray() };
        var retained = await coordinator.PrepareAsync(renamed, default);
        Assert.Equal(original.Revision, retained.Revision);
        Assert.Equal(original.Pages.Select(page => page.PlanJson), retained.Pages.Select(page => page.PlanJson));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveRenderBatchAsync(original with { Revision = 2 }, 0, default));
        var changed = original with { Revision = 2, Pages = original.Pages.Select((page, i) => i == 0 ? page with { PlanJson = original.Pages[1].PlanJson } : page).ToArray() };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveRenderBatchAsync(changed, 1, default));
        await Sql(options, "UPDATE render_batch_journals SET payload_json='{}';");
        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.LoadAsync("batch_test", default));
    }

    private static RenderBatchCoordinator Coordinator(SqliteDesktopStore store, ScriptedSession backend) =>
        new(store, backend, new RenderPlanValidator(new JsonSchemaContractValidator()), new TaskCoordinator(store), TimeProvider.System);

    internal static AiRenderPlanPreparation Preparation() => new("batch_test", AiRenderPreparationStatus.WaitingForPhotoshop,
        "PHOTOSHOP_UNAVAILABLE", Enumerable.Range(1, 4).Select(index =>
        {
            var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "render-plan-v1.valid.example.json")))!;
            var style = index < 4 ? "style_a" : "style_b";
            plan["batch_id"] = "batch_test"; plan["product_id"] = style;
            plan["document"]!["page_id"] = "page_" + index;
            plan["plan_id"] = "plan_" + index; plan["idempotency_key"] = "render_batch_page_" + index;
            plan["output"]!["artifact_set_id"] = "artifact_" + index;
            plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
            return new AiRenderPagePreparation(style, "page_" + index, AiRenderPagePreparationStatus.ReadyToRender,
                "AI_RENDER_PLAN_VALID", plan.ToJsonString(), plan["plan_digest"]!.GetValue<string>());
        }).ToArray());

    private static async Task Sql(DesktopDataOptions options, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ScriptedSession(SqliteDesktopStore store) : IRenderBatchSession, IRenderBatchSessionFactory
    {
        public List<string> Submitted { get; } = [];
        public Func<RenderBatchPage, Task>? OnSubmit { get; set; }
        public Func<Task<bool>>? OnReady { get; set; }
        public string? FailPage { get; set; }
        public string? OpenFailure { get; set; }
        public string? UnknownPage { get; init; }
        public int Recovered { get; private set; }
        public int CancelCalls { get; private set; }
        private bool _cancelled;
        public Task<IRenderBatchSession> OpenAsync(RenderBatchJournal journal, CancellationToken cancellationToken)
        {
            if (OpenFailure is not null) throw new InvalidOperationException(OpenFailure);
            _cancelled = false; return Task.FromResult<IRenderBatchSession>(this);
        }
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => OnReady?.Invoke() ?? Task.FromResult(true);
        public async Task<RenderPageObservation> SubmitAsync(RenderBatchPage page, CancellationToken cancellationToken)
        {
            Assert.Equal(RenderBatchPageStatus.Submitting, (await store.LoadRenderBatchAsync("batch_test", cancellationToken))!.Pages.Single(p => p.PageId == page.PageId && p.Attempt == page.Attempt).Status);
            Submitted.Add(page.PageId);
            if (OnSubmit is not null) await OnSubmit(page);
            return new(RenderBatchPageStatus.Running, "RENDER_EXECUTING", "execution_" + page.PageId + "_" + page.Attempt);
        }
        public async Task<RenderPageObservation> ObserveAsync(RenderBatchPage page, bool recovering, CancellationToken cancellationToken)
        {
            if (recovering)
            {
                Recovered++;
                if (page.Commit is { } prior)
                    Assert.Equal(OutputCommitState.Committed, await store.ReadRenderCommitAsync(prior, cancellationToken));
                return page.Status is RenderBatchPageStatus.Committed or RenderBatchPageStatus.Failed or RenderBatchPageStatus.Cancelled
                    ? new(page.Status, page.ReasonCode, page.ExecutionId, page.Commit)
                    : new(RenderBatchPageStatus.Unknown, "RENDER_RECOVERY_REQUIRED", page.ExecutionId);
            }
            if (_cancelled) return new(RenderBatchPageStatus.Cancelled, "RENDER_CANCELLED", page.ExecutionId);
            if (page.PageId == UnknownPage) return new(RenderBatchPageStatus.Unknown, "RENDER_RECOVERY_REQUIRED", page.ExecutionId);
            if (page.PageId == FailPage) return new(RenderBatchPageStatus.Failed, "BRIDGE_PLAN_INVALID", page.ExecutionId);
            using var document = JsonDocument.Parse(page.PlanJson!);
            var digest = document.RootElement.GetProperty("plan_digest").GetString()!;
            var checkpoint = new RenderPageCommitCheckpoint(new("batch_test", page.StyleId, page.PageId, "render_output", page.Attempt,
                digest, page.ExecutionId, OutputCommitState.Verified, DateTimeOffset.UtcNow, page.OutputVariantId), "render:" + digest[7..],
                "outputs/" + page.PageId + "/" + page.Attempt + "/manifest.json", 100, new string('a', 64));
            await store.PrepareRenderCommitAsync(checkpoint, cancellationToken);
            await store.CompleteRenderCommitAsync(checkpoint, cancellationToken);
            return new(RenderBatchPageStatus.Committed, "RENDER_PAGE_COMMITTED", page.ExecutionId, checkpoint);
        }
        public Task CancelAsync(RenderBatchPage page, CancellationToken cancellationToken)
        { CancelCalls++; _cancelled = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
