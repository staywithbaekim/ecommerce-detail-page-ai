using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Desktop.Application.Rendering;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Tests;

public sealed class BridgeResourceLeaseTests
{
    [Fact]
    public async Task PlanAndSessionBoundReadHandlesKeepSourcesReadOnlyUntilReleased()
    {
        using var fixture = new Fixture();
        var before = fixture.Hashes();
        await using var lease = await fixture.Open();
        foreach (var binding in lease.Bindings)
        {
            var source = fixture.Sources.Single(s => s.ResourceId == binding.ResourceId);
            var original = File.ReadAllBytes(Path.Combine(fixture.Root, source.OwnedRelativePath));
            var bytes = await lease.ReadChunkAsync("session_test", fixture.Digest, binding.SessionHandle, 0, original.Length, CancellationToken.None);
            Assert.Equal(original, bytes);
            Assert.Throws<IOException>(() => File.Open(Path.Combine(fixture.Root, source.OwnedRelativePath), FileMode.Open, FileAccess.Write).Dispose());
            Assert.DoesNotContain(source.OwnedRelativePath, binding.ToString(), StringComparison.Ordinal);
        }
        var handle = lease.Bindings[0].SessionHandle;
        await Assert.ThrowsAsync<InvalidDataException>(() => lease.ReadChunkAsync("other", fixture.Digest, handle, 0, 1, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => lease.ReadChunkAsync("session_test", "sha256:" + new string('0', 64), handle, 0, 1, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => lease.ReadChunkAsync("session_test", fixture.Digest, handle, 0, BridgeResourceLease.ChunkByteLimit + 1, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => lease.ReadChunkAsync("session_test", fixture.Digest, handle, -1, 1, CancellationToken.None));
        await lease.DisposeAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => lease.ReadChunkAsync("session_test", fixture.Digest, handle, 0, 1, CancellationToken.None));
        Assert.Equal(before, fixture.Hashes());
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("mime")]
    [InlineData("analysis_copy")]
    [InlineData("market_size_image")]
    [InlineData("escape")]
    [InlineData("device")]
    [InlineData("duplicate")]
    public async Task UnverifiedBytesAndUnapprovedSourcesNeverProduceBindings(string mutation)
    {
        using var fixture = new Fixture();
        var originalPath = Path.Combine(fixture.Root, fixture.Sources[0].OwnedRelativePath);
        if (mutation == "hash") File.WriteAllBytes(originalPath, [56, 66, 80, 83, 0, 1, 10, 12]);
        if (mutation == "length") File.AppendAllText(originalPath, "x");
        if (mutation == "mime")
        {
            File.WriteAllBytes(originalPath, new byte[8]);
            fixture.Plan["resources"]![0]!["sha256"] = Convert.ToHexString(SHA256.HashData(new byte[8]));
            fixture.Rehash();
        }
        if (mutation is "analysis_copy" or "market_size_image") fixture.Sources[0] = fixture.Sources[0] with { Purpose = mutation };
        if (mutation == "escape") fixture.Sources[0] = fixture.Sources[0] with { OwnedRelativePath = "../outside.psd" };
        if (mutation == "device") fixture.Sources[0] = fixture.Sources[0] with { OwnedRelativePath = @"\\?\C:\outside.psd" };
        if (mutation == "duplicate") fixture.Sources[1] = fixture.Sources[0];
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Open());
        Assert.Equal("BRIDGE_RESOURCE_BINDING_INVALID", error.Message);
        using var writable = File.Open(originalPath, FileMode.Open, FileAccess.Write);
    }

    [Fact]
    public async Task LaterResourceValidationFailureReleasesPreviouslyOpenedSources()
    {
        using var fixture = new Fixture();
        var laterPath = Path.Combine(fixture.Root, fixture.Sources[1].OwnedRelativePath);
        File.AppendAllText(laterPath, "invalid");

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Open());

        foreach (var source in fixture.Sources)
        {
            using var exclusive = File.Open(Path.Combine(fixture.Root, source.OwnedRelativePath),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "render-lease-test-" + Guid.NewGuid().ToString("N"));
        public JsonNode Plan { get; }
        public List<BridgeResourceSource> Sources { get; } = [];
        public string Digest => Plan["plan_digest"]!.GetValue<string>();

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Plan = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "render-plan-v1.valid.example.json")))!;
            foreach (var resource in Plan["resources"]!.AsArray())
            {
                var id = resource!["resource_id"]!.GetValue<string>();
                var role = resource["role"]!.GetValue<string>();
                var media = resource["media_type"]!.GetValue<string>();
                byte[] bytes = role == "template_copy" ? [56, 66, 80, 83, 0, 1, 0, 0] : [255, 216, 255, 0, 1, 2, 3, 4];
                File.WriteAllBytes(Path.Combine(Root, id), bytes);
                resource["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
                resource["byte_length"] = bytes.Length;
                Sources.Add(new(id, role, media, id));
            }
            Rehash();
        }

        public void Rehash() => Plan["plan_digest"] = RenderPlanDigest.Compute(Plan.ToJsonString());
        public Task<BridgeResourceLease> Open(string session = "session_test") => BridgeResourceLease.CreateAsync(Plan.ToJsonString(), session,
            Sources, new LocalAppPathPolicy(Root), new RenderPlanValidator(new JsonSchemaContractValidator()), CancellationToken.None);
        public string[] Hashes() => Sources.Select(s => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, s.OwnedRelativePath))))).ToArray();
        public void Dispose() => Directory.Delete(Root, true);
    }
}
