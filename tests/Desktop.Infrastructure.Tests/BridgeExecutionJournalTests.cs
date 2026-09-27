using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Rendering;
using Desktop.Contracts.Bridge;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Tests;

public sealed class BridgeExecutionJournalTests
{
    [Fact]
    public async Task ExistingV1FormatRoundTripsAndUnfinishedExecutionBecomesUnknown()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var files = new AtomicFileStore(paths);
        var original = new BridgeExecutionJournal(1, [Accepted() with
        {
            IdempotencyKey = "attempt/" + new string('x', 192), PlanDigest = "sha256:" + new string('A', 64)
        }]);
        // Exactly the serialization used by the previous implementation.
        await files.WriteAtomicallyAsync("render/bridge-executions-v1.json", JsonSerializer.SerializeToUtf8Bytes(original), CancellationToken.None);
        var store = new FileBridgeExecutionJournalStore(paths);
        using var broker = BridgeExecutionBrokerTests.Create(store);
        await broker.InitializeAsync(CancellationToken.None);
        Assert.Equal("unknown", (await broker.QueryAsync("execution_test", CancellationToken.None))!.State);
        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(original.Executions[0].IdempotencyKey, loaded!.Executions[0].IdempotencyKey);
        Assert.Equal(original.Executions[0].PlanDigest, loaded.Executions[0].PlanDigest);
        Assert.Equal("BRIDGE_EXECUTION_UNKNOWN", (await broker.ClaimAsync("session_test", CancellationToken.None)).Error!.Code);
    }

    [Theory]
    [InlineData("duplicate_root")]
    [InlineData("duplicate_nested")]
    [InlineData("escaped_duplicate")]
    [InlineData("unknown_root")]
    [InlineData("unknown_event")]
    [InlineData("missing_field")]
    [InlineData("missing_false_field")]
    [InlineData("null_field")]
    [InlineData("truncated")]
    [InlineData("invalid_utf8")]
    public async Task InvalidJsonIsRejectedWithoutRewritingOrExposingItsContent(string mutation)
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var files = new AtomicFileStore(paths);
        var root = JsonSerializer.SerializeToNode(new BridgeExecutionJournal(1, [Accepted()]))!;
        var record = root["Executions"]![0]!;
        if (mutation == "unknown_root") root["PrivateMarker"] = "private-test-marker";
        if (mutation == "unknown_event") record["Events"]![0]!["Extra"] = "private-test-marker";
        if (mutation == "missing_field") record.AsObject().Remove("PageId");
        if (mutation == "missing_false_field") record.AsObject().Remove("CancelRequested");
        if (mutation == "null_field") record["TaskId"] = null;
        var json = root.ToJsonString();
        if (mutation == "duplicate_root") json = json.Insert(1, "\"Version\":1,");
        if (mutation == "duplicate_nested") json = json.Replace("\"State\":", "\"State\":\"private-test-marker\",\"State\":", StringComparison.Ordinal);
        if (mutation == "escaped_duplicate") json = json.Insert(1, "\"\\u0056ersion\":1,");
        if (mutation == "truncated") json = json[..^1];
        var bytes = mutation == "invalid_utf8" ? new byte[] { 0xff, 0xfe, 0xfd } : Encoding.UTF8.GetBytes(json);
        await files.WriteAtomicallyAsync("render/bridge-executions-v1.json", bytes, CancellationToken.None);
        var store = new FileBridgeExecutionJournalStore(paths);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));
        Assert.Equal("BRIDGE_JOURNAL_INVALID", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(bytes, await files.ReadAsync("render/bridge-executions-v1.json", CancellationToken.None));
    }

    [Fact]
    public async Task ReadLimitAllowsExactBoundaryAndRejectsOversizeBeforeParsing()
    {
        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var path = paths.ResolveOwnedPath("render/bridge-executions-v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[16 * 1024 * 1024];
        Array.Fill(bytes, (byte)' ');
        "{\"Version\":1,\"Executions\":[]}"u8.CopyTo(bytes);
        await File.WriteAllBytesAsync(path, bytes);
        var store = new FileBridgeExecutionJournalStore(paths);
        Assert.Empty((await store.LoadAsync(CancellationToken.None))!.Executions);
        await using (var file = File.Open(path, FileMode.Open, FileAccess.Write)) file.SetLength(bytes.Length + 1L);
        Assert.Equal("BRIDGE_JOURNAL_INVALID", (await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadAsync(CancellationToken.None))).Message);
        Assert.Equal(bytes.Length + 1L, new FileInfo(path).Length);
    }

    [Fact]
    public async Task MissingFileAndCancellationDoNotCreateRecoveryData()
    {
        using var directory = new TestDirectory();
        var store = new FileBridgeExecutionJournalStore(new LocalAppPathPolicy(directory.Path));
        Assert.Null(await store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(new CancellationToken(true)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("task")]
    [InlineData("key")]
    [InlineData("digest")]
    [InlineData("page")]
    [InlineData("null_record")]
    [InlineData("null_events")]
    [InlineData("null_event")]
    [InlineData("empty_events")]
    [InlineData("sequence")]
    [InlineData("event_execution")]
    [InlineData("event_page")]
    [InlineData("event_schema")]
    [InlineData("duplicate_event")]
    [InlineData("forged_completion")]
    [InlineData("state_mismatch")]
    [InlineData("cancel_without_request")]
    [InlineData("duplicate_execution")]
    [InlineData("duplicate_key")]
    [InlineData("duplicate_event_across_executions")]
    [InlineData("version")]
    [InlineData("null_executions")]
    public async Task InvalidRecoveryIsRejectedBeforeAnyRecordIsPublishedOrSaved(string mutation)
    {
        var valid = Completed();
        var bad = Accepted() with { ExecutionId = "execution_second", IdempotencyKey = "attempt_second",
            Events = [Accepted().Events[0] with { EventId = "event_second", ExecutionId = "execution_second" }] };
        bad = mutation switch
        {
            "state" => bad with { State = "unrecognized" },
            "task" => bad with { TaskId = "../private-test-marker" },
            "key" => bad with { IdempotencyKey = "short" },
            "digest" => bad with { PlanDigest = "sha256:" + new string('z', 64) },
            "page" => bad with { PageId = null! },
            "null_record" => null!,
            "null_events" => bad with { Events = null! },
            "null_event" => bad with { Events = [null!] },
            "empty_events" => bad with { Events = [], EventSeq = 0 },
            "sequence" => bad with { Events = [bad.Events[0] with { EventSeq = 2 }] },
            "event_execution" => bad with { Events = [bad.Events[0] with { ExecutionId = "wrong_execution" }] },
            "event_page" => bad with { Events = [bad.Events[0] with { PageId = "wrong_page" }] },
            "event_schema" => bad with { Events = [bad.Events[0] with { EventId = "invalid/event" }] },
            "duplicate_event" => bad with { EventSeq = 2, Events = [bad.Events[0], bad.Events[0] with
                { EventSeq = 2, EventType = "page_started", Status = "running", PageId = bad.PageId }], State = "running" },
            "forged_completion" => bad with { EventSeq = 2, Events = [bad.Events[0], bad.Events[0] with
                { EventId = "event_forged", EventSeq = 2, EventType = "execution_completed", Status = "completed" }], State = "completed" },
            "state_mismatch" => bad with { State = "completed" },
            "cancel_without_request" => bad with { EventSeq = 2, Events = [bad.Events[0], bad.Events[0] with
                { EventId = "event_cancel", EventSeq = 2, EventType = "execution_cancelled", Status = "cancelled" }], State = "cancelled" },
            "duplicate_execution" => bad with { ExecutionId = valid.ExecutionId },
            "duplicate_key" => bad with { IdempotencyKey = valid.IdempotencyKey },
            "duplicate_event_across_executions" => bad with { Events = [bad.Events[0] with { EventId = valid.Events[0].EventId }] },
            _ => bad
        };
        var store = new MutableJournal { Value = new(mutation == "version" ? 2 : 1,
            mutation == "null_executions" ? null! : [valid, bad]) };
        using var broker = BridgeExecutionBrokerTests.Create(store);
        Assert.Equal("BRIDGE_JOURNAL_INVALID", (await Assert.ThrowsAsync<InvalidDataException>(
            () => broker.InitializeAsync(CancellationToken.None))).Message);
        Assert.Equal(0, store.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.QueryAsync(valid.ExecutionId, CancellationToken.None));
        store.Value = new(1, [valid]);
        await broker.InitializeAsync(CancellationToken.None);
        Assert.Equal("completed", (await broker.QueryAsync(valid.ExecutionId, CancellationToken.None))!.State);
        Assert.Equal(1, store.Writes);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("unknown")]
    [InlineData("cancelled")]
    [InlineData("failed")]
    [InlineData("completed")]
    public async Task RestartPreservesValidTerminalFactsAndNeverRequeuesInterruptedPage(string state)
    {
        var complete = Completed();
        var events = complete.Events.Take(2).ToList();
        if (state is "failed" or "cancelled") events.Add(new("event_end", 3, DateTimeOffset.UnixEpoch,
            "execution_" + state, complete.ExecutionId, state,
            Error: state == "failed" ? BridgeWireErrors.Create("BRIDGE_HOST_BLOCKED") : null));
        var record = state == "completed" ? complete : complete with
            { State = state, CancelRequested = true, Events = events, EventSeq = events.Count };
        var store = new MutableJournal { Value = new(1, [record]) };
        using var broker = BridgeExecutionBrokerTests.Create(store);
        await broker.InitializeAsync(CancellationToken.None);
        var restored = await broker.QueryAsync(record.ExecutionId, CancellationToken.None);
        Assert.Equal(state is "running" or "unknown" ? "unknown" : state, restored!.State);
        Assert.Equal(record.Events, restored.Events);
        var claim = await broker.ClaimAsync("session_test", CancellationToken.None);
        Assert.True(!claim.IsSuccess || !claim.Value!.HasWork);
    }

    private static BridgeExecutionRecord Accepted() => new("batch_01JEXAMPLE", "execution_test", "attempt_test",
        "sha256:" + new string('a', 64), "page_01", "accepted", false, 1,
        [new("event_accepted", 1, DateTimeOffset.UnixEpoch, "execution_accepted", "execution_test", "accepted")]);

    private static BridgeExecutionRecord Completed()
    {
        var record = Accepted();
        return record with { State = "completed", EventSeq = 4, Events = [record.Events[0],
            new("event_started", 2, DateTimeOffset.UnixEpoch, "page_started", record.ExecutionId, "running", record.PageId),
            new("event_page_done", 3, DateTimeOffset.UnixEpoch, "page_completed", record.ExecutionId, "running", record.PageId),
            new("event_done", 4, DateTimeOffset.UnixEpoch, "execution_completed", record.ExecutionId, "completed")] };
    }

    private sealed class MutableJournal : IBridgeExecutionJournalStore
    {
        public BridgeExecutionJournal? Value { get; set; }
        public int Writes { get; private set; }
        public Task<BridgeExecutionJournal?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
        public Task SaveAsync(BridgeExecutionJournal journal, CancellationToken cancellationToken)
        {
            Value = journal;
            Writes++;
            return Task.CompletedTask;
        }
    }
}
