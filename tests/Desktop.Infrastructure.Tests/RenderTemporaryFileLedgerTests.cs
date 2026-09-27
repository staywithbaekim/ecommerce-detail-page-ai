using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Desktop.Application.Rendering;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Tests;

public sealed class RenderTemporaryFileLedgerTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task NewOwnerReclaimsOnlyRecordedFilesAfterConfirmedTerminal(string state)
    {
        using var fixture = new Fixture();
        await fixture.Copy();
        var unrelated = Path.Combine(fixture.Root, "old-output.jpg");
        await File.WriteAllTextAsync(unrelated, "formal output");
        var parts = fixture.PartPath;
        Directory.CreateDirectory(Path.GetDirectoryName(parts)!);
        using (var file = new FileStream(parts, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await fixture.Writer.TrackAsync(fixture.PartRelative, OwnedRenderResourceFile.Capture(parts, file.SafeFileHandle), default);
            file.WriteByte(123); file.Flush(true);
        }
        await fixture.NewOwner().ReleaseTerminalAsync(fixture.Execution(state), default);
        Assert.False(File.Exists(fixture.CopyPath)); Assert.False(File.Exists(parts));
        Assert.Equal("formal output", await File.ReadAllTextAsync(unrelated));
        Assert.Equal(fixture.SourceHash, Hash(await File.ReadAllBytesAsync(fixture.Source)));
        Assert.True(JsonNode.Parse(await File.ReadAllTextAsync(fixture.Inventory))!["Released"]!.GetValue<bool>());
        // A later occupant at the old name is never claimed by a completed cleanup.
        await File.WriteAllTextAsync(fixture.CopyPath, "new owner");
        await fixture.NewOwner().ReleaseTerminalAsync(fixture.Execution(state), default);
        Assert.Equal("new owner", await File.ReadAllTextAsync(fixture.CopyPath));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("running")]
    [InlineData("authorized")]
    [InlineData("other_task")]
    public async Task UnconfirmedOrUnrelatedExecutionCannotReleaseAnything(string mutation)
    {
        using var fixture = new Fixture();
        await fixture.Copy();
        var record = mutation == "other_task" ? fixture.Execution("completed") with { TaskId = "other" } : fixture.Execution(mutation);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.NewOwner().ReleaseTerminalAsync(record, default));
        Assert.True(File.Exists(fixture.CopyPath));
    }

    [Fact]
    public async Task ReplacedFileWithSameBytesIsRetainedAndFailedCleanupCanRetry()
    {
        using var fixture = new Fixture();
        await fixture.Copy();
        var moved = fixture.CopyPath + ".moved";
        File.Move(fixture.CopyPath, moved);
        File.Copy(moved, fixture.CopyPath);
        await Assert.ThrowsAsync<IOException>(() => fixture.NewOwner().ReleaseTerminalAsync(fixture.Execution("completed"), default));
        Assert.True(File.Exists(fixture.CopyPath)); Assert.True(File.Exists(moved));
        File.Delete(fixture.CopyPath); File.Move(moved, fixture.CopyPath);
        await fixture.NewOwner().ReleaseTerminalAsync(fixture.Execution("completed"), default);
        Assert.False(File.Exists(fixture.CopyPath));
    }

    [Fact]
    public async Task LiveReadLeasePreventsRecoveryDeletionAndMissingEntryIsIdempotent()
    {
        using var fixture = new Fixture();
        await fixture.Copy();
        using (var reader = File.Open(fixture.CopyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<IOException>(() => fixture.NewOwner().ReleaseTerminalAsync(fixture.Execution("completed"), default));
        File.Delete(fixture.CopyPath); // Simulates interruption after deletion but before ledger flush.
        await fixture.NewOwner().ReleaseTerminalAsync(fixture.Execution("completed"), default);
        Assert.True(File.Exists(fixture.Source));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("digest")]
    [InlineData("extra")]
    [InlineData("null_entry")]
    public async Task InvalidInventoryIsRejectedBeforeDeletingAnyFile(string mutation)
    {
        using var fixture = new Fixture();
        await fixture.Copy();
        var inventory = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Inventory))!;
        if (mutation == "path") inventory["Files"]![0]!["RelativePath"] = "../source.jpg";
        if (mutation == "digest") inventory["PlanDigest"] = "sha256:" + new string('0', 64);
        if (mutation == "extra") inventory["Unrecognized"] = true;
        if (mutation == "null_entry") inventory["Files"]![0] = null;
        await File.WriteAllTextAsync(fixture.Inventory, inventory.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.NewOwner().ReleaseTerminalAsync(fixture.Execution("completed"), default));
        Assert.True(File.Exists(fixture.CopyPath)); Assert.True(File.Exists(fixture.Source));
    }

    [Fact]
    public async Task MissingLegacyInventoryNeverInfersOwnershipFromDirectoryNames()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.CopyPath)!);
        File.Copy(fixture.Source, fixture.CopyPath);
        await fixture.NewOwner().ReleaseTerminalAsync(fixture.Execution("completed"), default);
        Assert.True(File.Exists(fixture.CopyPath));
    }

    [Fact]
    public async Task InventoryWriteFailureStopsCopyAndExistingEvidenceIsNotOverwritten()
    {
        using var fixture = new Fixture();
        await fixture.Copy();
        var original = await File.ReadAllBytesAsync(fixture.Inventory);
        var otherRelative = fixture.CopyRelative.Replace(new string('a', 32), new string('b', 32));
        var otherPath = Path.Combine(fixture.Root, otherRelative);
        await Assert.ThrowsAsync<InvalidDataException>(() => RenderBatchResourcePreparer.CopyVerifiedAsync(fixture.Source, otherPath,
            new FileInfo(fixture.Source).Length, fixture.SourceHash, default,
            file => fixture.NewOwner().TrackAsync(otherRelative, file, default)));
        Assert.False(File.Exists(otherPath));
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Inventory));
        Assert.True(File.Exists(fixture.CopyPath));
    }

    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private sealed class Fixture : IDisposable
    {
        private readonly BridgeResourceLeaseTests.Fixture _source = new();
        private readonly LocalAppPathPolicy _paths;
        public string Root => _source.Root;
        public string Source { get; }
        public string SourceHash { get; }
        public string CopyRelative { get; }
        public string CopyPath => Path.Combine(Root, CopyRelative);
        public string PartRelative { get; }
        public string PartPath => Path.Combine(Root, PartRelative);
        public string Inventory => Path.Combine(Root, "render", "temporary-files", _source.Digest[7..] + ".json");
        public RenderTemporaryFileLedger Writer { get; }
        public Fixture()
        {
            _paths = new(Root);
            var source = _source.Sources[0];
            Source = Path.Combine(Root, source.OwnedRelativePath);
            SourceHash = Hash(File.ReadAllBytes(Source));
            CopyRelative = $".app-staging/resources/{_source.Digest[7..]}/{new string('a', 32)}/{Hash(Encoding.UTF8.GetBytes(source.ResourceId))}.psd";
            var plan = _source.Plan;
            var batch = plan["batch_id"]!.GetValue<string>();
            PartRelative = RenderPageCommitter.StagingDirectory("rendered/id-" + Hash(Encoding.UTF8.GetBytes(batch))[..32], batch,
                plan["product_id"]!.GetValue<string>(), plan["document"]!["page_id"]!.GetValue<string>(), _source.Digest) + "/jpg.part";
            Writer = NewOwner();
        }
        public RenderTemporaryFileLedger NewOwner() => new(_paths, _paths, _source.Plan.ToJsonString());
        public async Task Copy() => _ = await RenderBatchResourcePreparer.CopyVerifiedAsync(Source, CopyPath,
            new FileInfo(Source).Length, SourceHash, default, file => Writer.TrackAsync(CopyRelative, file, default));
        public BridgeExecutionRecord Execution(string state) => new(_source.Plan["batch_id"]!.GetValue<string>(), "execution_cleanup",
            _source.Plan["idempotency_key"]!.GetValue<string>(), _source.Digest,
            _source.Plan["document"]!["page_id"]!.GetValue<string>(), state, state == "cancelled", 1, []);
        public void Dispose() => _source.Dispose();
    }
}
