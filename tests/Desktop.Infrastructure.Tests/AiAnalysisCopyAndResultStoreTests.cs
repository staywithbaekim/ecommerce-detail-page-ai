using System.Security.Cryptography;
using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Application.Files;
using Desktop.Contracts.Ai;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Tests;

public sealed class AiAnalysisCopyAndResultStoreTests
{
    [Fact]
    public async Task PreparerCreatesIndependentMetadataAndNeverDeletesSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai01-copy-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "photo.jpg");
        var bytes = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(source, bytes);
        try
        {
            var sourceDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            var files = new AtomicFileStore(policy);
            var preparer = new FileAiAnalysisCopyPreparer(policy, files);
            var metadata = await preparer.PrepareAsync(new AiAnalysisCopyPreparationRequest(
                "copy_01", input,
                Desktop.Domain.Inputs.SourceAssetMetadata.CreateReadable(
                    "asset_01", "style_01", "photo.jpg", "photo.jpg", bytes.Length,
                    sourceDigest, 10, 10, Desktop.Domain.Inputs.SourceImageFormat.Jpeg, "image/jpeg"),
                AiAnalysisCopyProfiles.BytePreservingProvisionalV1), CancellationToken.None);
            Assert.StartsWith("sha256:", metadata.Digest, StringComparison.Ordinal);
            Assert.Equal(bytes.Length, metadata.ByteLength);
            var sourceAfter = await File.ReadAllBytesAsync(source);
            Assert.Equal(bytes, sourceAfter);
            var sourceStore = new FileAiAnalysisCopySource(policy);
            var copy = await sourceStore.OpenAsync("copy_01", CancellationToken.None);
            Assert.Equal(metadata.Digest, copy.ContentDigest);
            Assert.Equal("image/jpeg", copy.MimeType);
            await preparer.ReleaseAsync("copy_01", metadata.LeaseId, CancellationToken.None);
            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_01.bin")));
            Assert.True(File.Exists(source));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ResultStoreRoundTripsAndRejectsCorruption()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai01-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var policy = new LocalAppPathPolicy(root);
            var files = new AtomicFileStore(policy);
            var store = new FileAiAnalysisResultStore(files, new Desktop.Contracts.Validation.JsonSchemaContractValidator());
            var result = new AiAnalysisResultV1(AiAnalysisProtocol.ResultSchemaVersion, "result", "request_01", null, "model/proposed", [], [], [], null, [], new AiUsageSummaryV1("usage_request_01", 1, 2, 1, null, null));
            await store.SaveAsync("results/request_01", result, CancellationToken.None);
            var roundTrip = await store.LoadAsync("results/request_01", CancellationToken.None);
            Assert.Equal(result.RequestId, roundTrip!.RequestId);
            Assert.Equal(result.ModelConfigVersion, roundTrip.ModelConfigVersion);
            Assert.Equal(result.Usage, roundTrip.Usage);
            await File.WriteAllTextAsync(Path.Combine(root, "results", "request_01.json"), "not-json");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync("results/request_01", CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CopyLeasesSurvivePreparerRestartAndSharedReferencesAreNotOverwritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai01-copy-restart-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input", "nested", "three");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "photo.jpg");
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(source, bytes);
        try
        {
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            var files = new AtomicFileStore(policy);
            var sourceMetadata = Desktop.Domain.Inputs.SourceAssetMetadata.CreateReadable(
                "asset_01", "style_01", "nested/three/photo.jpg", "photo.jpg", bytes.Length,
                digest, 10, 10, Desktop.Domain.Inputs.SourceImageFormat.Jpeg, "image/jpeg");
            var first = new FileAiAnalysisCopyPreparer(policy, files);
            var prepared = await first.PrepareAsync(
                new AiAnalysisCopyPreparationRequest(
                    "copy_shared",
                    Path.Combine(root, "input"),
                    sourceMetadata,
                    AiAnalysisCopyProfiles.BytePreservingProvisionalV1),
                CancellationToken.None);
            var restarted = new FileAiAnalysisCopyPreparer(policy, files);
            await restarted.AcquireAsync("copy_shared", "lease-second", CancellationToken.None);
            await restarted.ReleaseAsync("copy_shared", prepared.LeaseId, CancellationToken.None);
            Assert.True(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_shared.bin")));
            await restarted.ReleaseAsync("copy_shared", "lease-second", CancellationToken.None);
            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_shared.bin")));
            first.Dispose();
            restarted.Dispose();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SourceChangeAfterCopyPreparationRollsBackOwnedCopyAndMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai01-copy-source-change-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "photo.jpg");
        var original = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        var changed = original.Reverse().ToArray();
        await File.WriteAllBytesAsync(source, original);
        try
        {
            var digest = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
            var policy = new LocalAppPathPolicy(Path.Combine(root, "app"));
            var inner = new AtomicFileStore(policy);
            var files = new MutateSourceAfterMetadataWriteStore(inner, source, changed);
            using var preparer = new FileAiAnalysisCopyPreparer(policy, files);

            await Assert.ThrowsAsync<InvalidDataException>(() => preparer.PrepareAsync(
                new AiAnalysisCopyPreparationRequest(
                    "copy_changed",
                    input,
                    Desktop.Domain.Inputs.SourceAssetMetadata.CreateReadable(
                        "asset_01", "style_01", "photo.jpg", "photo.jpg", original.Length,
                        digest, 10, 10, Desktop.Domain.Inputs.SourceImageFormat.Jpeg, "image/jpeg"),
                    AiAnalysisCopyProfiles.BytePreservingProvisionalV1),
                CancellationToken.None));

            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_changed.bin")));
            Assert.False(File.Exists(policy.ResolveOwnedPath("ai-analysis/copies/copy_changed.json")));
            Assert.Equal(changed, await File.ReadAllBytesAsync(source));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class MutateSourceAfterMetadataWriteStore(
        IFileStore inner,
        string sourcePath,
        byte[] changedBytes) : IFileStore
    {
        private bool _mutated;

        public async Task WriteAtomicallyAsync(
            string relativePath,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            await inner.WriteAtomicallyAsync(relativePath, content, cancellationToken);
            if (!_mutated && relativePath.EndsWith(".json", StringComparison.Ordinal))
            {
                _mutated = true;
                await File.WriteAllBytesAsync(sourcePath, changedBytes, cancellationToken);
            }
        }

        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken) =>
            inner.ReadAsync(relativePath, cancellationToken);

        public Task<string?> ComputeSha256Async(string relativePath, CancellationToken cancellationToken) =>
            inner.ComputeSha256Async(relativePath, cancellationToken);

        public Task<bool> DeleteAsync(string relativePath, CancellationToken cancellationToken) =>
            inner.DeleteAsync(relativePath, cancellationToken);
    }
}
