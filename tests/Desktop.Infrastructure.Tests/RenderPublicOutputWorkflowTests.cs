using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;
using Desktop.Contracts.Validation;
using Desktop.Domain.Tasks;
using Desktop.Infrastructure.Bridge;

namespace Desktop.Infrastructure.Tests;

public sealed class RenderPublicOutputWorkflowTests
{
    [Fact]
    public async Task PublicationFailureAndRenameRecoveryNeverOpenPhotoshopOrChangeCommittedPages()
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        var original = await RenderStyleOutputPublicationTests.Prepare(fixture, store);
        await fixture.Sql("CREATE TRIGGER fail BEFORE UPDATE ON render_output_publications WHEN NEW.state='Committed' BEGIN SELECT RAISE(ABORT,'TEST_FAIL'); END;");
        var sessions = new NoSubmissionSession();
        await using var coordinator = new RenderBatchCoordinator(store, sessions, new RenderPlanValidator(new JsonSchemaContractValidator()),
            new TaskCoordinator(store), TimeProvider.System, publicOutputs: new RenderStyleOutputPublisher(store, store));
        var pending = await coordinator.RunAsync("batch_test", default);
        Assert.Equal(RenderBatchStatus.RecoveryRequired, pending.Status);
        Assert.Equal(original.Pages, pending.Pages);
        Assert.Equal(0, sessions.Opens);
        await fixture.Sql("DROP TRIGGER fail;");
        var ready = await coordinator.RunAsync("batch_test", default);
        Assert.Equal(RenderBatchStatus.CompletedWithIssues, ready.Status); // Original blocked placeholder is retained.
        Assert.Equal(original.Pages, ready.Pages);
        Assert.Equal(0, sessions.Opens);
        Assert.Equal("2", await fixture.Scalar("SELECT count(*) FROM render_output_publications WHERE state='Committed';"));
        var before = JsonSerializer.Serialize(await store.LoadRenderBatchAsync("batch_test", default));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PrepareRegenerationAsync("batch_test", "style_a", "page_1", null, 1, default));
        Assert.Equal("RENDER_PUBLIC_OUTPUT_EDITING_NOT_AVAILABLE", error.Message);
        Assert.Equal(before, JsonSerializer.Serialize(await store.LoadRenderBatchAsync("batch_test", default)));
    }

    [Fact]
    public async Task UnknownRemainsAnObservationOnlyAndCannotUsePublicationRecoveryAsARetry()
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        var original = await RenderStyleOutputPublicationTests.Prepare(fixture, store, skipFirstCommit: true);
        var unknown = original.Pages[0] with { Status = RenderBatchPageStatus.Unknown, ExecutionId = "execution_unknown", ReasonCode = "RENDER_RECOVERY_REQUIRED" };
        original = original with { Revision = original.Revision + 1, Status = RenderBatchStatus.RecoveryRequired, Pages = [unknown, .. original.Pages.Skip(1)] };
        await store.SaveRenderBatchAsync(original, original.Revision - 1, default);
        var sessions = new NoSubmissionSession();
        await using var coordinator = new RenderBatchCoordinator(store, sessions, new RenderPlanValidator(new JsonSchemaContractValidator()),
            new TaskCoordinator(store), TimeProvider.System, publicOutputs: new RenderStyleOutputPublisher(store, store));
        var result = await coordinator.RunAsync("batch_test", default);
        Assert.Equal(RenderBatchStatus.RecoveryRequired, result.Status);
        Assert.Equal(unknown, result.Pages[0]);
        Assert.Equal(1, sessions.Opens);
        Assert.Equal(1, sessions.Observations);
        Assert.Equal("0", await fixture.Scalar("SELECT count(*) FROM render_output_publications;"));
    }

    [Fact]
    public async Task PublicOutputAllowsOnlyFailedPageRecoveryAndReopensVersionTwoPreflight()
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        var running = await RenderStyleOutputPublicationTests.Prepare(fixture, store, skipFirstCommit: true);
        var pages = running.Pages.ToArray();
        pages[0] = pages[0] with
        {
            Status = RenderBatchPageStatus.Failed,
            ReasonCode = "BRIDGE_PLAN_INVALID",
            ExecutionId = "execution_failed"
        };
        var failed = running with
        {
            Revision = running.Revision + 1,
            Status = RenderBatchStatus.CompletedWithIssues,
            Pages = pages
        };
        await store.SaveRenderBatchAsync(failed, running.Revision, default);
        var tasks = new TaskCoordinator(store);
        var task = await tasks.LoadAsync("batch_test", default);
        await tasks.RecordAsync(TaskEvent.Create("batch_test", task.LastSequence + 1, TaskEventType.Failed), default);

        var publisher = new RenderStyleOutputPublisher(store, store);
        Assert.True(await publisher.CanRecoverFailedPageAsync("batch_test", "style_a", "page_1", null, 1, default));

        await using var coordinator = new RenderBatchCoordinator(store, new NoSubmissionSession(),
            new RenderPlanValidator(new JsonSchemaContractValidator()), tasks, TimeProvider.System, publicOutputs: publisher);
        var prepared = await coordinator.PrepareRegenerationAsync("batch_test", "style_a", "page_1", null, 1, default);
        Assert.Equal(2, prepared.Version);
        Assert.Equal(RenderBatchPageStatus.Planned, prepared.Pages[^1].Status);
        Assert.False(await publisher.CanRecoverFailedPageAsync("batch_test", "style_a", "page_1", null, 1, default));

        using var outputPreparation = await publisher.PrepareForRenderAsync(prepared, default);
        Assert.Equal(2, outputPreparation.Registration.Styles.Length);
    }

    [Fact]
    public async Task FactoryPreflightConflictHasNoHostOrResourceSideEffectsAndPreservesInitialRetryPoint()
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        var batch = await fixture.Seed(store);
        var journal = (await store.LoadRenderBatchAsync("batch_test", default))!;
        var target = RenderStyleOutputPlanner.Plan(batch)[0];
        Directory.CreateDirectory(target.FinalDirectory);
        File.WriteAllText(Path.Combine(target.FinalDirectory, "keep.jpg"), "existing formal output");
        var publisher = new RenderStyleOutputPublisher(store, store);
        // Null host/resource dependencies make any accidental call a test failure rather than a mock success.
        var factory = new BridgeRenderBatchSessionFactory(store, null!, null!, null!, null!, store, publisher);
        await using var coordinator = new RenderBatchCoordinator(store, factory, new RenderPlanValidator(new JsonSchemaContractValidator()),
            new TaskCoordinator(store), TimeProvider.System, publicOutputs: publisher);
        Assert.Equal("RENDER_OUTPUT_PREFLIGHT_REQUIRED", (await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync("batch_test", default))).Message);
        Assert.Equal(JsonSerializer.Serialize(journal), JsonSerializer.Serialize(await store.LoadRenderBatchAsync("batch_test", default)));
        Assert.False(Directory.Exists(fixture.Evidence));
        Assert.False(Directory.Exists(target.StagingDirectory));
        Assert.Equal("existing formal output", File.ReadAllText(Path.Combine(target.FinalDirectory, "keep.jpg")));
    }

    [Fact]
    public async Task SessionPreflightWritesAndCleansOnlyItsProbeAndRetainsSourceParentPins()
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        await fixture.Seed(store);
        var journal = (await store.LoadRenderBatchAsync("batch_test", default))!;
        using var preparation = await new RenderStyleOutputPublisher(store, store).PrepareForRenderAsync(journal, default);
        foreach (var style in preparation.Registration.Styles)
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(style.Target.StagingDirectory));
            Assert.Throws<IOException>(() => Directory.Move(style.Target.SourceDirectory, style.Target.SourceDirectory + "-swapped"));
        }
        preparation.VerifyBeforeDispatch(journal.Pages[0]);
        var stage = preparation.Registration.Styles[0].Target.StagingDirectory;
        Directory.Move(stage, stage + "-old"); Directory.CreateDirectory(stage);
        Assert.Throws<InvalidDataException>(() => preparation.VerifyBeforeDispatch(journal.Pages[0]));
    }

    [Fact]
    public async Task RealAddFileDenialBlocksDispatchAlthoughEmptyStageAlreadyExistsAndRestoresTestAcl()
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        await fixture.Seed(store);
        using (await new RenderStyleOutputRegistry(store).PrepareAsync("batch_test", default)) { }
        var target = (await store.LoadRenderOutputRegistrationAsync("batch_test", default))!.Styles[0].Target;
        var directory = new DirectoryInfo(target.StagingDirectory);
        var original = directory.GetAccessControl(AccessControlSections.Access);
        var denied = directory.GetAccessControl(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        denied.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.CreateFiles, AccessControlType.Deny));
        directory.SetAccessControl(denied);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new RenderStyleOutputRegistry(store).OpenForRenderAsync("batch_test", default));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target.StagingDirectory));
            Assert.Equal(1, (await store.LoadRenderBatchAsync("batch_test", default))!.Revision);
        }
        finally
        {
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            directory.SetAccessControl(restored);
            var actual = directory.GetAccessControl(AccessControlSections.Access);
            Assert.Equal(original.AreAccessRulesProtected, actual.AreAccessRulesProtected);
            Assert.Equal(DaclBytes(original), DaclBytes(actual));
        }
        using var recovered = await new RenderStyleOutputRegistry(store).OpenForRenderAsync("batch_test", default);

        static byte[] DaclBytes(DirectorySecurity security)
        {
            // Windows may set SE_DACL_AUTO_INHERITED while restoring via SetNamedSecurityInfo.
            // Compare the exact ACL/ACEs and protection, not that OS bookkeeping flag.
            var acl = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl!;
            var bytes = new byte[acl.BinaryLength]; acl.GetBinaryForm(bytes, 0); return bytes;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryNeverRecreatesMissingStagingOwnershipAfterAnExecutionCouldHaveStarted(bool missingFact)
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        await fixture.Seed(store);
        using (await new RenderStyleOutputRegistry(store).PrepareAsync("batch_test", default)) { }
        var journal = (await store.LoadRenderBatchAsync("batch_test", default))!;
        await store.SaveRenderBatchAsync(journal with { Revision = 2, Status = RenderBatchStatus.Running }, 1, default);
        var target = (await store.LoadRenderOutputRegistrationAsync("batch_test", default))!.Styles[0].Target;
        Directory.Move(target.StagingDirectory, target.StagingDirectory + "-original");
        if (missingFact) await fixture.Sql("DELETE FROM render_output_stages WHERE style_id='style_a';");
        if (missingFact) await Assert.ThrowsAsync<InvalidDataException>(() => new RenderStyleOutputRegistry(store).OpenForRenderAsync("batch_test", default));
        else Assert.Equal("RENDER_OUTPUT_DIRECTORY_UNAVAILABLE", (await Assert.ThrowsAsync<RenderOutputTargetException>(() =>
            new RenderStyleOutputRegistry(store).OpenForRenderAsync("batch_test", default))).Code);
        Assert.False(Directory.Exists(target.StagingDirectory));
        Assert.Equal(2, (await store.LoadRenderBatchAsync("batch_test", default))!.Revision);
    }

    [Theory]
    [InlineData("Copying")]
    [InlineData("Verified")]
    [InlineData("Committed")]
    public async Task RecoveryCanRetainOtherPendingStylePinsWhilePublishingAlreadyCommittedStyle(string state)
    {
        using var fixture = new RenderStyleOutputRegistryTests.Fixture();
        await using var store = await fixture.Open();
        var journal = await RenderStyleOutputPublicationTests.Prepare(fixture, store, skipFirstCommit: true);
        var publisher = new RenderStyleOutputPublisher(store, store);
        if (state == "Copying") await fixture.Sql("CREATE TRIGGER fail BEFORE UPDATE ON render_output_publications WHEN NEW.state='Verified' BEGIN SELECT RAISE(ABORT,'TEST_FAIL'); END;");
        if (state == "Verified") await fixture.Sql("CREATE TRIGGER fail BEFORE UPDATE ON render_output_publications WHEN NEW.state='Committed' BEGIN SELECT RAISE(ABORT,'TEST_FAIL'); END;");
        if (state != "Committed")
        {
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => publisher.PublishAsync("batch_test", journal, default));
            await fixture.Sql("DROP TRIGGER fail;");
        }
        else await publisher.PublishAsync("batch_test", journal, default);
        using var preparation = await publisher.PrepareForRenderAsync(journal, default);
        preparation.VerifyBeforeDispatch(journal.Pages[0]);
        await publisher.PublishAsync("batch_test", journal, default);
        Assert.Equal("Committed", (await store.LoadRenderOutputPublicationAsync("batch_test", "style_b", default))!.State);
        Assert.Null(await store.LoadRenderOutputPublicationAsync("batch_test", "style_a", default));
        Assert.Equal(journal.Pages, (await store.LoadRenderBatchAsync("batch_test", default))!.Pages);
    }

    private sealed class NoSubmissionSession : IRenderBatchSessionFactory, IRenderBatchSession
    {
        internal int Opens, Observations;
        public Task<IRenderBatchSession> OpenAsync(RenderBatchJournal journal, CancellationToken cancellationToken)
        { Opens++; return Task.FromResult<IRenderBatchSession>(this); }
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => throw new NotSupportedException("No renderer permitted");
        public Task<RenderPageObservation> SubmitAsync(RenderBatchPage page, CancellationToken cancellationToken) => throw new NotSupportedException("No submission permitted");
        public Task<RenderPageObservation> ObserveAsync(RenderBatchPage page, bool recovering, CancellationToken cancellationToken)
        { Observations++; return Task.FromResult(new RenderPageObservation(page.Status, page.ReasonCode, page.ExecutionId, page.Commit)); }
        public Task CancelAsync(RenderBatchPage page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
