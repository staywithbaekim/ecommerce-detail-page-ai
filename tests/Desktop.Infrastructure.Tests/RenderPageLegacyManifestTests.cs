using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;
using Desktop.Domain.Tasks;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Tests;

public sealed class RenderPageLegacyManifestTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Theory]
    [InlineData("prepared")]
    [InlineData("moved")]
    [InlineData("committed")]
    public async Task LegacyVerifiedReceiptRecoversAndPreviewsWithoutRewritingEvidence(string phase)
    {
        using var root = new TestDirectory();
        await using var store = await CreateStore(root.Path);
        var (manifest, jpeg) = LegacyManifest();
        var (checkpoint, path) = await WriteOutput(root.Path, store, manifest, jpeg, phase);
        var before = await File.ReadAllBytesAsync(path);

        var result = await RenderPageCommitter.RecoverAsync(new LocalAppPathPolicy(root.Path), store,
            Path.GetRelativePath(root.Path, path), default);

        Assert.Equal(checkpoint, result.Checkpoint);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(root.Path, checkpoint.ManifestRelativePath)));
        Assert.Equal(OutputCommitState.Committed, await store.ReadRenderCommitAsync(checkpoint, default));
        Assert.Equal(1, (await new TaskCoordinator(store).LoadAsync("legacy_batch")).OutputCount);
        await using var preview = await RenderPageCommitter.OpenCommittedJpegAsync(new LocalAppPathPolicy(root.Path),
            store, checkpoint, default);
        using var copy = new MemoryStream();
        await preview.CopyToAsync(copy);
        Assert.Equal(jpeg, copy.ToArray());
        Assert.Empty(Directory.GetFiles(root.Path, "*.psd", SearchOption.AllDirectories));
        Assert.Throws<IOException>(() => File.Open(Path.Combine(root.Path, "outputs/legacy_page/01-detail.jpg"),
            FileMode.Open, FileAccess.Write, FileShare.Read));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("editable_mixed")]
    [InlineData("missing_jpg")]
    [InlineData("wrong_jpg")]
    [InlineData("invalid_digest")]
    [InlineData("invalid_length")]
    public async Task LegacyCompatibilityStillRejectsUnrecognizedReceiptShapes(string mutation)
    {
        using var root = new TestDirectory();
        await using var store = await CreateStore(root.Path);
        var (manifest, jpeg) = LegacyManifest();
        var legacy = manifest.Media.Files[0];
        var jpg = manifest.Media.Files[1];
        IReadOnlyList<RenderMediaFile> receipts = mutation switch
        {
            "duplicate" => [legacy, legacy, jpg],
            "unknown" => [legacy with { Kind = "unknown" }, jpg],
            "editable_mixed" => [legacy with { Kind = "editable_psd" }, jpg],
            "missing_jpg" => [legacy, legacy],
            "wrong_jpg" => [legacy, jpg with { Sha256 = new string('f', 64) }],
            "invalid_digest" => [legacy with { Sha256 = "invalid" }, jpg],
            _ => [legacy with { ByteLength = 0 }, jpg]
        };
        manifest = manifest with { Media = manifest.Media with { Files = receipts } };
        var (checkpoint, _) = await WriteOutput(root.Path, store, manifest, jpeg, "committed");
        await Assert.ThrowsAsync<InvalidDataException>(() => RenderPageCommitter.OpenCommittedJpegAsync(
            new LocalAppPathPolicy(root.Path), store, checkpoint, default));
    }

    [Theory]
    [InlineData("unprepared")]
    [InlineData("jpg")]
    [InlineData("marker")]
    [InlineData("manifest")]
    [InlineData("extra")]
    public async Task LegacyReceiptCannotBypassCheckpointOrOutputIntegrity(string mutation)
    {
        using var root = new TestDirectory();
        await using var store = await CreateStore(root.Path);
        var (manifest, jpeg) = LegacyManifest();
        var (checkpoint, path) = await WriteOutput(root.Path, store, manifest, jpeg,
            mutation == "unprepared" ? "unprepared" : "committed");
        var directory = Path.GetDirectoryName(path)!;
        if (mutation == "jpg") await File.AppendAllTextAsync(Path.Combine(directory, "01-detail.jpg"), "changed");
        if (mutation == "marker") await File.WriteAllTextAsync(Path.Combine(directory, "committed.marker"), "invalid");
        if (mutation == "manifest") await File.AppendAllTextAsync(path, " ");
        if (mutation == "extra") await File.WriteAllTextAsync(Path.Combine(directory, "unrelated.txt"), "keep");
        var paths = new LocalAppPathPolicy(root.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => RenderPageCommitter.OpenCommittedJpegAsync(paths, store, checkpoint, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => RenderPageCommitter.RecoverAsync(paths, store,
            Path.GetRelativePath(root.Path, path), default));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void NewPreparationDoesNotAcceptLegacyVerificationReceipt()
    {
        var (manifest, _) = LegacyManifest();
        Assert.Throws<InvalidDataException>(() => RenderPageCommitter.Validate(manifest));
    }

    private static (RenderPageManifest Manifest, byte[] Jpeg) LegacyManifest()
    {
        var jpeg = RenderJpegValidatorTests.Jpeg();
        var jpgHash = Hash(jpeg);
        var planDigest = "sha256:" + new string('a', 64);
        var page = new TaskCheckpoint("legacy_batch", "style_01", "page_01", "render_output", 1,
            planDigest, "execution_01", OutputCommitState.Verified, DateTimeOffset.UnixEpoch);
        var media = new RenderMediaEvidence(planDigest,
            new(3, 2, RenderJpegValidator.SrgbProfileSha256, 3144), 37, 14,
            [new("verification_psd", 100, new string('b', 64)), new("jpg", jpeg.Length, jpgHash)]);
        return (new("render-page/v1", "outputs", "legacy_page", page, media,
            [new("jpg", "01-detail.jpg", jpeg.Length, jpgHash)]), jpeg);
    }

    private static async Task<(RenderPageCommitCheckpoint Checkpoint, string ManifestPath)> WriteOutput(
        string root, SqliteDesktopStore store, RenderPageManifest manifest, byte[] jpeg, string phase)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        var checkpoint = new RenderPageCommitCheckpoint(manifest.Page, "render:" + manifest.Page.PlanDigest[7..],
            "outputs/legacy_page/manifest.json", bytes.Length, Hash(bytes));
        var relative = phase == "prepared"
            ? Path.Combine(RenderPageCommitter.StagingDirectory("outputs", manifest.Page.TaskId,
                manifest.Page.StyleId, manifest.Page.PageId, manifest.Page.PlanDigest), "page")
            : "outputs/legacy_page";
        var directory = Path.Combine(root, relative);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "manifest.json");
        await File.WriteAllBytesAsync(path, bytes);
        await File.WriteAllBytesAsync(Path.Combine(directory, "01-detail.jpg"), jpeg);
        if (phase != "prepared")
            await File.WriteAllTextAsync(Path.Combine(directory, "committed.marker"),
                "render-page-commit/v1\n" + checkpoint.ManifestSha256 + "\n");
        if (phase != "unprepared") await store.PrepareRenderCommitAsync(checkpoint, default);
        if (phase == "committed") await store.CompleteRenderCommitAsync(checkpoint, default);
        return (checkpoint, path);
    }

    private static async Task<SqliteDesktopStore> CreateStore(string root)
    {
        var store = new SqliteDesktopStore(new(Path.Combine(root, "database")));
        await store.InitializeAsync();
        var coordinator = new TaskCoordinator(store);
        await coordinator.RecordAsync(TaskEvent.Create("legacy_batch", 1, TaskEventType.TaskCreated));
        await coordinator.RecordAsync(TaskEvent.Create("legacy_batch", 2, TaskEventType.ReadyToRender));
        await coordinator.RecordAsync(TaskEvent.Create("legacy_batch", 3, TaskEventType.RenderingStarted));
        return store;
    }

    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
