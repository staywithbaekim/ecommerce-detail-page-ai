using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Desktop.Application.Files;
using Desktop.Application.Rendering;
using Desktop.Application.Tasks;

namespace Desktop.Infrastructure.Bridge;

public sealed record RenderCommittedFile(string Kind, string FileName, long ByteLength, string Sha256);
public sealed record RenderPageManifest(string Version, string OutputDirectory, string ArtifactSetId,
    TaskCheckpoint Page, RenderMediaEvidence Media, IReadOnlyList<RenderCommittedFile> Files);
public sealed record RenderPageCommitResult(RenderPageCommitCheckpoint Checkpoint, bool Recovered);

/// <summary>Desktop-only page commit. Recovery never renders or overwrites an existing artifact.</summary>
public static class RenderPageCommitter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    internal static string StagingDirectory(string root, string batch, string product, string page, string digest) =>
        Path.Combine(root, ".app-staging", Component(batch), Component(product + "/" + page), digest[7..].ToLowerInvariant());

    private static string Component(string id) => "id-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..32];

    internal static FileStream AcquireGuard(IPathPolicy paths, string root, string artifact)
    {
        var directory = paths.ResolveOwnedPath(Path.Combine(root, ".app-staging", "locks", Component(artifact.ToUpperInvariant())));
        BridgeOutputReservation.ValidatePath(directory);
        Directory.CreateDirectory(directory);
        BridgeOutputReservation.ValidatePath(directory);
        var path = Path.Combine(directory, ".reservation");
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096,
            FileOptions.WriteThrough | FileOptions.DeleteOnClose);
        try { BridgeResourceLease.VerifyOpenedPath(stream.SafeFileHandle, path); return stream; }
        catch { stream.Dispose(); throw; }
    }

    internal static async Task<PreparedRenderPage> PrepareAsync(IPathPolicy paths, string outputDirectory,
        string stagingDirectory, string artifact, TaskCheckpoint page, RenderMediaEvidence media,
        IReadOnlyList<RenderCommittedFile> files, IReadOnlyDictionary<string, FileStream> sources,
        CancellationToken cancellationToken)
    {
        var manifest = new RenderPageManifest(page.Instance is null ? "render-page/v1" : "render-page/v2", outputDirectory, artifact, page, media, files);
        Validate(manifest);
        var payloadRelative = Path.Combine(stagingDirectory, "page");
        if (stagingDirectory != StagingDirectory(outputDirectory, page.TaskId, page.StyleId, page.PageId, page.PlanDigest)) throw Invalid();
        var payload = paths.ResolveOwnedPath(payloadRelative);
        BridgeOutputReservation.ValidatePath(payload);
        if (Directory.Exists(payload) || File.Exists(payload)) throw Invalid();
        Directory.CreateDirectory(payload);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources[file.Kind];
            source.Position = 0;
            var target = Path.Combine(payload, file.FileName);
            await using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            BridgeResourceLease.VerifyOpenedPath(stream.SafeFileHandle, target);
            await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        WriteNew(Path.Combine(payload, "manifest.json"), bytes);
        return await OpenPrepared(paths, payloadRelative, manifest, bytes, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<RenderPageCommitResult> RecoverAsync(IPathPolicy paths, IRenderPageCommitStore store,
        string manifestRelativePath, CancellationToken cancellationToken, BridgeExecutionRecord? expectedExecution = null)
    {
        byte[] bytes;
        using (var stream = Open(paths.ResolveOwnedPath(manifestRelativePath), FileShare.Read))
        {
            if (stream.Length is < 1 or > 1048576) throw Invalid();
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        var manifest = JsonSerializer.Deserialize<RenderPageManifest>(bytes, Json) ?? throw Invalid();
        Validate(manifest, allowLegacyVerificationReceipt: true);
        if (expectedExecution is not null && (manifest.Page.TaskId != expectedExecution.TaskId
            || manifest.Page.BridgeExecutionId != expectedExecution.ExecutionId || manifest.Page.PageId != expectedExecution.PageId
            || manifest.Page.PlanDigest != expectedExecution.PlanDigest || manifest.Page.Instance != expectedExecution.Instance)) throw Invalid();
        var stage = Path.Combine(StagingDirectory(manifest.OutputDirectory, manifest.Page.TaskId,
            manifest.Page.StyleId, manifest.Page.PageId, manifest.Page.PlanDigest), "page");
        var final = Path.Combine(manifest.OutputDirectory, manifest.ArtifactSetId);
        var location = Path.GetDirectoryName(manifestRelativePath)!;
        if (Path.GetFileName(manifestRelativePath) != "manifest.json"
            || !Same(paths.ResolveOwnedPath(location), paths.ResolveOwnedPath(stage))
               && !Same(paths.ResolveOwnedPath(location), paths.ResolveOwnedPath(final))) throw Invalid();
        using var guard = AcquireGuard(paths, manifest.OutputDirectory, manifest.ArtifactSetId);
        if (Directory.Exists(paths.ResolveOwnedPath(stage)) && Directory.Exists(paths.ResolveOwnedPath(final))) throw Invalid();
        using var prepared = await OpenPrepared(paths, location, manifest, bytes, cancellationToken).ConfigureAwait(false);
        var state = await store.ReadRenderCommitAsync(prepared.Checkpoint, cancellationToken).ConfigureAwait(false);
        // A file without its durable Verified journal cannot authorize publication.
        if (state is null || state == OutputCommitState.Committed && !Same(prepared.CurrentPath, prepared.FinalPath)) throw Invalid();
        return await prepared.CommitAsync(store, recovered: true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PreparedRenderPage> OpenPrepared(IPathPolicy paths, string location,
        RenderPageManifest manifest, byte[] bytes, CancellationToken token)
    {
        var current = paths.ResolveOwnedPath(location);
        var finalRelative = Path.Combine(manifest.OutputDirectory, manifest.ArtifactSetId);
        var checkpoint = new RenderPageCommitCheckpoint(manifest.Page, "render:" + manifest.Page.PlanDigest[7..],
            Path.Combine(finalRelative, "manifest.json").Replace('\\', '/'), bytes.Length, Hash(bytes));
        var prepared = new PreparedRenderPage(manifest, checkpoint, current, paths.ResolveOwnedPath(finalRelative), bytes);
        try { await prepared.VerifyAndLockAsync(FileShare.Read, token).ConfigureAwait(false); return prepared; }
        catch { prepared.Dispose(); throw; }
    }

    public static async Task<Stream> OpenCommittedJpegAsync(IPathPolicy paths, IRenderPageCommitStore store,
        RenderPageCommitCheckpoint expected, CancellationToken token)
    {
        using var page = await OpenCommittedPageAsync(paths, store, expected, token).ConfigureAwait(false);
        return Open(Path.Combine(page.CurrentPath, page.Manifest.Files.Single(file => file.Kind == "jpg").FileName), FileShare.Read);
    }

    internal static async Task<PreparedRenderPage> OpenCommittedPageAsync(IPathPolicy paths, IRenderPageCommitStore store,
        RenderPageCommitCheckpoint expected, CancellationToken token)
    {
        using var manifestStream = Open(paths.ResolveOwnedPath(expected.ManifestRelativePath), FileShare.Read);
        if (manifestStream.Length != expected.ManifestByteLength || manifestStream.Length is < 1 or > 1048576) throw Invalid();
        var bytes = new byte[checked((int)manifestStream.Length)];
        await manifestStream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (Hash(bytes) != expected.ManifestSha256) throw Invalid();
        var manifest = JsonSerializer.Deserialize<RenderPageManifest>(bytes, Json) ?? throw Invalid();
        Validate(manifest, allowLegacyVerificationReceipt: true);
        var final = Path.Combine(manifest.OutputDirectory, manifest.ArtifactSetId);
        if (!Same(paths.ResolveOwnedPath(Path.Combine(final, "manifest.json")), paths.ResolveOwnedPath(expected.ManifestRelativePath))) throw Invalid();
        var prepared = await OpenPrepared(paths, final, manifest, bytes, token).ConfigureAwait(false);
        try
        {
            if (prepared.Checkpoint != expected || !File.Exists(Path.Combine(prepared.FinalPath, "committed.marker"))
                || await store.ReadRenderCommitAsync(expected, token).ConfigureAwait(false) != OutputCommitState.Committed) throw Invalid();
            return prepared;
        }
        catch { prepared.Dispose(); throw; }
    }

    internal static void Validate(RenderPageManifest manifest, bool allowLegacyVerificationReceipt = false)
    {
        if (manifest.Version is not ("render-page/v1" or "render-page/v2") || manifest.Page is null || manifest.Media?.Files is null || manifest.Files is null)
            throw Invalid();
        if ((manifest.Version == "render-page/v2") != (manifest.Page.Instance is not null)
            || manifest.Page.Instance is { } identity && (!RenderPlanInstanceIdentity.IsValid(identity)
                || identity.BatchId != manifest.Page.TaskId || identity.StyleId != manifest.Page.StyleId
                || identity.SourcePageId != manifest.Page.PageId || manifest.Page.OutputVariantId is not null)) throw Invalid();
        // Historical JPG-only v1 manifests retained one verified temporary PSD
        // receipt, never a formal PSD. Read/recovery must preserve those bytes;
        // only the matching durable checkpoint can authorize their use below.
        // New preparation deliberately keeps the current JPG-only contract.
        var legacyReceipt = allowLegacyVerificationReceipt && manifest.Version == "render-page/v1" && manifest.Files.Count == 1
            && manifest.Files[0].Kind == "jpg" && manifest.Media.Files.Count == 2
            && manifest.Media.Files.Count(file => file.Kind == "verification_psd") == 1;
        if (manifest.Page.OutputVariantId is { } variant && (variant.Length is < 1 or > 128
                || variant.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.' or ':')))
            || manifest.Media.PlanDigest != manifest.Page.PlanDigest || manifest.Page.OutputState != OutputCommitState.Verified
            || manifest.Page.PlanDigest is not { Length: 71 } || !manifest.Page.PlanDigest.StartsWith("sha256:", StringComparison.Ordinal)
            || !Digest(manifest.Page.PlanDigest[7..]) || manifest.Page.Attempt < 1
            || !SafeName(manifest.ArtifactSetId) || string.IsNullOrWhiteSpace(manifest.OutputDirectory)
            || Path.IsPathRooted(manifest.OutputDirectory)
            || manifest.OutputDirectory.Split('/', '\\').Any(part => !SafeName(part))
            || manifest.Files.Count is not (1 or 2) || manifest.Media.Files.Count != manifest.Files.Count + (legacyReceipt ? 1 : 0)
            || manifest.Files.Select(file => file.Kind).Distinct().Count() != manifest.Files.Count
            || manifest.Files.Select(file => file.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count)
            throw Invalid();
        if (manifest.Media.SizeTable is { } size && (size.PageId != manifest.Page.PageId || size.EmbeddedLayerId < 1
            || string.IsNullOrWhiteSpace(size.SizeTableId) || size.StructuredDataDigest is not { Length: 71 }
            || !size.StructuredDataDigest.StartsWith("sha256:", StringComparison.Ordinal)
            || !Digest(size.StructuredDataDigest[7..]) || !Digest(size.RasterSha256) || !Digest(size.TemplateSha256))) throw Invalid();
        if (manifest.Files.Count(file => file.Kind == "jpg") != 1
            || manifest.Media.Files.Count(file => file.Kind == "jpg") != 1
            || manifest.Files.Count == 2 && (manifest.Files.Count(file => file.Kind == "editable_psd") != 1
                || manifest.Media.Files.Count(file => file.Kind == "editable_psd") != 1)
            || manifest.Files.Count == 1 && manifest.Media.Files.Any(file => file.Kind != "jpg"
                && !(legacyReceipt && file.Kind == "verification_psd"))
            || manifest.Media.Files.Any(file => file.ByteLength is < 1 or > 2147483648 || !Digest(file.Sha256))) throw Invalid();
        foreach (var file in manifest.Files)
            if (!SafeName(file.FileName) || file.Kind is not ("jpg" or "editable_psd")
                || !file.FileName.EndsWith(file.Kind == "jpg" ? ".jpg" : ".psd", StringComparison.Ordinal)
                || file.ByteLength is < 1 or > 2147483648 || !Digest(file.Sha256)
                || manifest.Media.Files.Count(receipt => receipt.Kind == file.Kind && receipt.ByteLength == file.ByteLength
                    && receipt.Sha256 == file.Sha256) != 1) throw Invalid();
    }

    private static bool SafeName(string? name) => !string.IsNullOrWhiteSpace(name) && name is not ("." or "..")
        && name.Length <= 160 && !name.EndsWith('.') && !name.EndsWith(' ') && !name.Any(char.IsControl)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !System.Text.RegularExpressions.Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static bool Digest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal static InvalidDataException Invalid() => new("RENDER_COMMIT_RECOVERY_REQUIRED");

    internal static FileStream Open(string path, FileShare share)
    {
        BridgeOutputReservation.ValidatePath(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try { BridgeResourceLease.VerifyOpenedPath(stream.SafeFileHandle, path); BridgeOutputReservation.ValidatePath(path); return stream; }
        catch { stream.Dispose(); throw; }
    }

    internal static void WriteNew(string path, byte[] bytes)
    {
        BridgeOutputReservation.ValidatePath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        OwnedRenderResourceFile? owned = null;
        Exception? failure = null;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                BridgeResourceLease.VerifyOpenedPath(stream.SafeFileHandle, temporary);
                owned = OwnedRenderResourceFile.Capture(temporary, stream.SafeFileHandle);
                stream.Write(bytes); stream.Flush(true); stream.Position = 0;
                var actual = new byte[bytes.Length]; stream.ReadExactly(actual);
                if (stream.Length != bytes.Length || !actual.AsSpan().SequenceEqual(bytes)) throw Invalid();
            }
            File.Move(temporary, path, overwrite: false);
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            try { owned?.Dispose(); }
            catch when (failure is not null) { }
        }
    }
}

internal sealed class PreparedRenderPage(RenderPageManifest manifest, RenderPageCommitCheckpoint checkpoint,
    string currentPath, string finalPath, byte[] manifestBytes) : IDisposable
{
    private readonly List<FileStream> _leases = [];
    internal RenderPageCommitCheckpoint Checkpoint => checkpoint;
    internal RenderPageManifest Manifest => manifest;
    internal string CurrentPath { get; private set; } = currentPath;
    internal string FinalPath => finalPath;

    internal async Task VerifyAndLockAsync(FileShare share, CancellationToken token)
    {
        BridgeOutputReservation.ValidatePath(CurrentPath);
        var expected = manifest.Files.Select(file => file.FileName).Append("manifest.json").ToHashSet(StringComparer.Ordinal);
        if (File.Exists(Path.Combine(CurrentPath, "committed.marker"))) expected.Add("committed.marker");
        if (!expected.SetEquals(Directory.GetFileSystemEntries(CurrentPath).Select(path => Path.GetFileName(path)))) throw RenderPageCommitter.Invalid();
        foreach (var file in manifest.Files)
        {
            var stream = RenderPageCommitter.Open(Path.Combine(CurrentPath, file.FileName), share);
            _leases.Add(stream);
            if (stream.Length != file.ByteLength
                || !string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)), file.Sha256,
                    StringComparison.OrdinalIgnoreCase)) throw RenderPageCommitter.Invalid();
        }
        VerifySmall("manifest.json", manifestBytes, share);
        if (expected.Contains("committed.marker")) VerifySmall("committed.marker", Marker(), share);
    }

    private byte[] Marker() => Encoding.UTF8.GetBytes("render-page-commit/v1\n" + checkpoint.ManifestSha256 + "\n");

    private void VerifySmall(string name, byte[] bytes, FileShare share)
    {
        var stream = RenderPageCommitter.Open(Path.Combine(CurrentPath, name), share);
        _leases.Add(stream);
        if (stream.Length != bytes.Length) throw RenderPageCommitter.Invalid();
        var actual = new byte[bytes.Length]; stream.ReadExactly(actual);
        if (!actual.AsSpan().SequenceEqual(bytes)) throw RenderPageCommitter.Invalid();
    }

    internal async Task<RenderPageCommitResult> CommitAsync(IRenderPageCommitStore store, bool recovered, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!recovered) await store.PrepareRenderCommitAsync(checkpoint, token).ConfigureAwait(false);
        // Durable Verified is the commit boundary. Cancellation after this point is
        // deferred; failures retain the same journal for query/recovery, never rerender.
        var isFinal = RenderPageCommitter.Same(CurrentPath, FinalPath);
        var marker = Path.Combine(CurrentPath, "committed.marker");
        if (isFinal && !File.Exists(marker)) throw RenderPageCommitter.Invalid();
        if (!isFinal)
        {
            if (Directory.Exists(FinalPath) || File.Exists(FinalPath)) throw RenderPageCommitter.Invalid();
            if (!File.Exists(marker)) RenderPageCommitter.WriteNew(marker, Marker());
            VerifySmall("committed.marker", Marker(), FileShare.Read);
            BridgeOutputReservation.ValidatePath(FinalPath);
            // Windows directory moves reject open child files even with delete
            // sharing. The sibling target guard remains held across this handoff.
            // Uploaded originals stay locked; final bytes must match the durable
            // manifest again before a Committed checkpoint can be written.
            Dispose();
            Directory.Move(CurrentPath, FinalPath);
            CurrentPath = FinalPath;
        }
        // Reacquire deny-write/delete handles and verify the complete final set.
        await VerifyAndLockAsync(FileShare.Read, CancellationToken.None).ConfigureAwait(false);
        await store.CompleteRenderCommitAsync(checkpoint, CancellationToken.None).ConfigureAwait(false);
        if (await store.ReadRenderCommitAsync(checkpoint, CancellationToken.None).ConfigureAwait(false) != OutputCommitState.Committed)
            throw RenderPageCommitter.Invalid();
        return new(checkpoint, recovered);
    }

    public void Dispose() { foreach (var stream in _leases) stream.Dispose(); _leases.Clear(); }
}
