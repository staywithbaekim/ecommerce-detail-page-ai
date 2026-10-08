using Desktop.Application.Rendering;
using Desktop.Application.Tasks;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Tests;

public sealed partial class BridgeExecutionBrokerTests
{
    [Fact]
    public async Task E3RealBrokerPersistsDistinctExecutionsForSameSourcePageAndRejectsCrossInstanceReceipt()
    {
        using var directory = new TestDirectory();
        var journal = new FileBridgeExecutionJournalStore(new LocalAppPathPolicy(directory.Path));
        using var broker = Create(journal);
        var (first, executionA) = await ValidatedPage(broker, instanceTag: "a");
        await using var store = await RenderStore(first.Root);
        var a = await broker.CommitOutputAsync(executionA, 1, store, default);
        var (second, executionB) = await ValidatedPage(broker, attempt: 2, instanceTag: "e");
        var b = await broker.CommitOutputAsync(executionB, 1, store, default);
        Assert.NotEqual(executionA, executionB);
        Assert.Equal(a.Checkpoint.Page.PageId, b.Checkpoint.Page.PageId);
        Assert.NotEqual(a.Checkpoint.Page.Instance, b.Checkpoint.Page.Instance);
        Assert.Null(a.Checkpoint.Page.OutputVariantId); Assert.Null(b.Checkpoint.Page.OutputVariantId);
        Assert.Equal(2, (await new TaskCoordinator(store).LoadAsync("batch_01JEXAMPLE")).OutputCount);
        Assert.Equal(2, (await store.LoadAsync("batch_01JEXAMPLE", default)).Count);
        Assert.Equal(2, (await journal.LoadAsync(default))!.Version);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadRenderCommitAsync(a.Checkpoint with
            { Page = a.Checkpoint.Page with { Instance = b.Checkpoint.Page.Instance } }, default));
        var wrongExecution = (await broker.QueryAsync(executionB, default))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => RenderPageCommitter.RecoverAsync(new LocalAppPathPolicy(first.Root), store,
            a.Checkpoint.ManifestRelativePath, default, wrongExecution));
        using var restarted = Create(journal); await restarted.InitializeAsync(default);
        Assert.Equal("completed", (await restarted.QueryAsync(executionA, default))!.State);
        Assert.Equal("completed", (await restarted.QueryAsync(executionB, default))!.State);
        Assert.False((await restarted.ClaimAsync("session_test", default)).Value!.HasWork);
        var recovered = await RenderPageCommitter.RecoverAsync(new LocalAppPathPolicy(second.Root), store,
            b.Checkpoint.ManifestRelativePath, default, (await restarted.QueryAsync(executionB, default))!);
        Assert.True(recovered.Recovered);
        Assert.Equal(2, (await new TaskCoordinator(store).LoadAsync("batch_01JEXAMPLE")).OutputCount);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("identity_version")]
    [InlineData("wrong_source")]
    [InlineData("journal_version")]
    public async Task E3InstanceJournalRestartQueriesOnlyAndUnknownVersionsFailClosed(string mutation)
    {
        using var directory = new TestDirectory();
        var journal = new FileBridgeExecutionJournalStore(new LocalAppPathPolicy(directory.Path));
        using var broker = Create(journal);
        var (_, _, claim) = await PrepareOutput(broker, instanceTag: "a");
        var saved = (await journal.LoadAsync(default))!;
        if (mutation != "unknown")
        {
            saved = mutation == "journal_version" ? saved with { Version = 77 } : saved with
            { Executions = saved.Executions.Select(record => record with { Instance = mutation == "identity_version"
                ? record.Instance! with { Version = 77 } : record.Instance! with { SourcePageId = "wrong" } }).ToArray() };
            await journal.SaveAsync(saved, default);
        }
        using var restarted = Create(journal);
        if (mutation != "unknown") { await Assert.ThrowsAsync<InvalidDataException>(() => restarted.InitializeAsync(default)); return; }
        await restarted.InitializeAsync(default);
        var recovered = (await restarted.QueryAsync(claim.ExecutionId, default))!;
        Assert.Equal("unknown", recovered.State); Assert.Equal(saved.Executions[0].Instance, recovered.Instance);
        Assert.Equal("BRIDGE_EXECUTION_UNKNOWN", (await restarted.ClaimAsync("session_test", default)).Error!.Code);
        Assert.Single((await journal.LoadAsync(default))!.Executions);
    }
}
