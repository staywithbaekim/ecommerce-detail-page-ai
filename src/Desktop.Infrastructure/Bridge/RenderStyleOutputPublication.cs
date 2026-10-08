using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Application.Rendering;
using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Bridge;

internal sealed record RenderPublicFile(string FileName, long ByteLength, string Sha256, string SourceManifestSha256);
internal sealed record RenderPublicPage(string PageId, string? OutputVariantId, int Attempt,
    string CommitManifestSha256, ImmutableArray<RenderPublicFile> Files,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] RenderInstanceIdentity? Instance = null);
internal sealed record RenderStylePublicManifest(int Version, string BatchId, string StyleId,
    string RegistrationSha256, ImmutableArray<RenderPublicPage> Pages);
internal sealed record RenderStyleOutputPublication(int Version, string BatchId, string StyleId,
    string State, string RegistrationSha256, RenderOutputDirectoryIdentity StageIdentity,
    RenderOutputDirectoryIdentity? FinalIdentity, string FinalDirectory, string ManifestSha256,
    ImmutableArray<RenderPublicPage> Pages);
internal sealed record RenderPublicFileOwnership(int Version, string BatchId, string StyleId, string FileName,
    long ByteLength, string Sha256, OwnedRenderResourceFile.Identity Identity, bool Verified);

internal static class RenderStyleOutputPublicationData
{
    internal const string Copying = "Copying", Verified = "Verified", Committed = "Committed";
    internal const string ManifestName = "publication.manifest.json", MarkerName = "committed.marker";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true, MaxDepth = 32
    };
    internal static string Encode<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Json);
        if (Encoding.UTF8.GetByteCount(json) is < 1 or > RenderStyleOutputRegistrationData.MaxBytes) throw Invalid();
        return json;
    }
    internal static T Decode<T>(string json, string hash)
    {
        if (Encoding.UTF8.GetByteCount(json) is < 1 or > RenderStyleOutputRegistrationData.MaxBytes || Hash(json) != hash) throw Invalid();
        try { return JsonSerializer.Deserialize<T>(json, Json) ?? throw Invalid(); }
        catch (JsonException) { throw Invalid(); }
    }
    internal static string Hash(string json) => Hash(Encoding.UTF8.GetBytes(json));
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static byte[] Bytes<T>(T value) => Encoding.UTF8.GetBytes(Encode(value));
    internal static bool Equal<T>(T left, T right) => Encode(left) == Encode(right);
    internal static InvalidDataException Invalid(string code = "RENDER_OUTPUT_PUBLICATION_RECOVERY_REQUIRED") => new(code);
    internal static byte[] ManifestBytes(RenderStyleOutputPublication publication) => Bytes(new RenderStylePublicManifest(publication.Version,
        publication.BatchId, publication.StyleId, publication.RegistrationSha256, publication.Pages));
    internal static byte[] MarkerBytes(RenderStyleOutputPublication publication) =>
        Encoding.UTF8.GetBytes((publication.Version == 1 ? "render-style-output/v1\n" : "render-style-output/v2\n") + publication.ManifestSha256 + "\n");
    internal static IReadOnlyList<RenderPublicFile> ExpectedFiles(RenderStyleOutputPublication publication) =>
        [.. publication.Pages.SelectMany(page => page.Files),
            new(ManifestName, ManifestBytes(publication).Length, publication.ManifestSha256, ""),
            new(MarkerName, MarkerBytes(publication).Length, Hash(MarkerBytes(publication)), "")];

    internal static IReadOnlyList<RenderBatchPage> LatestPagesForStyle(RenderBatchJournal journal, string styleId) =>
        journal.Pages.Where(page => page.StyleId == styleId && page.PlanJson is not null)
            .GroupBy(page => (page.PageId, page.OutputVariantId, page.Instance?.PageInstanceId))
            .Select(group => group.OrderByDescending(page => page.Attempt).First())
            .OrderBy(page => page.PageId, StringComparer.Ordinal)
            .ThenBy(page => page.OutputVariantId, StringComparer.Ordinal)
            .ToArray();

    internal static bool SupportsPublicationJournal(RenderBatchJournal journal) =>
        journal.Version is 1 or 4
        || journal.Version == 2 && journal.RegenerationPageIndex == journal.Pages.Count - 1
            && journal.Pages.All(page => page.SizeTableRepair is null);

    internal static void Validate(RenderStyleOutputPublication value)
    {
        if (value.Version is not (1 or 2) || value.State is not (Copying or Verified or Committed)
            || value.Pages.IsDefaultOrEmpty || value.Pages.Length > 10000
            || value.Pages.Any(page => page is null || (value.Version == 2) != (page.Instance is not null)
                || page.Instance is { } identity && (!RenderPlanInstanceIdentity.IsValid(identity)
                    || identity.BatchId != value.BatchId || identity.StyleId != value.StyleId || identity.SourcePageId != page.PageId) || page.Files.IsDefaultOrEmpty || page.Files.Length is < 1 or > 2
                || !Digest(page.CommitManifestSha256) || page.Files.Any(file => file is null || file.ByteLength is < 1 or > 2147483648
                    || !Digest(file.Sha256) || file.SourceManifestSha256 != page.CommitManifestSha256))
            || !Digest(value.RegistrationSha256) || !Digest(value.ManifestSha256)
            || value.State == Committed && value.FinalIdentity != value.StageIdentity
            || value.State != Committed && value.FinalIdentity is not null) throw Invalid();
        RenderStyleOutputRegistrationData.ValidateIdentity(value.StageIdentity);
        if (RenderStyleOutputPlanner.LocalDirectoryPath(value.FinalDirectory) != value.FinalDirectory) throw Invalid();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestName, MarkerName };
        foreach (var file in value.Pages.SelectMany(page => page.Files))
        {
            var full = RenderStyleOutputPlanner.LocalDirectoryPath(Path.Combine(value.FinalDirectory, file.FileName));
            if (Path.GetDirectoryName(full) != value.FinalDirectory || Path.GetFileName(full) != file.FileName || !names.Add(file.FileName)) throw Invalid();
        }
        if (Hash(ManifestBytes(value)) != value.ManifestSha256) throw Invalid();
    }

    internal static void ValidateAgainst(RenderStyleOutputPublication value, RenderStyleOutputRegistration registration,
        RenderBatchJournal journal, RenderStyleOutputStage stage, bool requireCertainBatch = true)
    {
        Validate(value);
        if (registration.BatchId != value.BatchId || journal.BatchId != value.BatchId || !SupportsPublicationJournal(journal)
            || stage.BatchId != value.BatchId || stage.StyleId != value.StyleId || value.StageIdentity != stage.Identity
            || value.RegistrationSha256 != RenderStyleOutputRegistrationData.Hash(RenderStyleOutputRegistrationData.Encode(registration))
            || requireCertainBatch && journal.Pages.Any(page => page.Status is RenderBatchPageStatus.Unknown or RenderBatchPageStatus.Running or RenderBatchPageStatus.Submitting)) throw Invalid();
        var style = registration.Styles.SingleOrDefault(item => item.Target.StyleId == value.StyleId) ?? throw Invalid();
        if (value.FinalDirectory != style.Target.FinalDirectory) throw Invalid();
        var required = style.Pages.Where(page => page.PlanDigest is not null).ToArray();
        var currentPages = LatestPagesForStyle(journal, value.StyleId);
        if (value.Pages.Length != required.Length || currentPages.Count != required.Length) throw Invalid();
        var seen = new HashSet<(string, string?, string?)>();
        foreach (var page in value.Pages)
        {
            if (!seen.Add((page.PageId, page.OutputVariantId, page.Instance?.PageInstanceId))) throw Invalid();
            var planned = required.SingleOrDefault(item => item.PageId == page.PageId && item.OutputVariantId == page.OutputVariantId && item.Instance == page.Instance) ?? throw Invalid();
            var current = currentPages.SingleOrDefault(item => item.PageId == page.PageId
                && item.OutputVariantId == page.OutputVariantId && item.Instance == page.Instance) ?? throw Invalid();
            var initial = journal.Pages.Where(item => RenderBatchJournalValidator.SamePage(item, current))
                .OrderBy(item => item.Attempt).FirstOrDefault() ?? throw Invalid();
            if (initial.Attempt != planned.Attempt || initial.PlanJson is null || PlanDigest(initial.PlanJson) != planned.PlanDigest)
                throw Invalid();
            if (current.Status != RenderBatchPageStatus.Committed || current.Commit is null || current.Commit.ManifestSha256 != page.CommitManifestSha256
                || current.Commit.Page.PlanDigest != PlanDigest(current.PlanJson!) || page.Attempt != current.Attempt) throw Invalid();
            var names = planned.PsdName is null ? new[] { planned.JpegName! } : new[] { planned.JpegName!, planned.PsdName };
            if (!names.Order(StringComparer.Ordinal).SequenceEqual(page.Files.Select(file => file.FileName).Order(StringComparer.Ordinal))) throw Invalid();
        }
    }

    private static string PlanDigest(string planJson)
    {
        try
        {
            using var document = JsonDocument.Parse(planJson);
            return document.RootElement.GetProperty("plan_digest").GetString() ?? throw Invalid();
        }
        catch (JsonException) { throw Invalid(); }
        catch (InvalidOperationException) { throw Invalid(); }
        catch (KeyNotFoundException) { throw Invalid(); }
    }
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

/// <summary>Publishes committed pages without rendering; permits only pre-publication recovery of failed pages.</summary>
public sealed class RenderStyleOutputPublisher(SqliteDesktopStore store, IRenderPageCommitStore commits) : IRenderStyleOutputPublisher
{
    private int _publishing;
    public async Task<bool> UsesPublicOutputAsync(string batchId, CancellationToken cancellationToken) =>
        (await store.GetAsync(batchId, cancellationToken).ConfigureAwait(false) ?? throw Invalid()).OutputSelection is not null;

    public Task<bool> CanRecoverFailedPageAsync(string batchId, string styleId, string pageId, string? outputVariantId,
        int expectedAttempt, CancellationToken cancellationToken) =>
        CanRecoverAsync(batchId, styleId, pageId, outputVariantId, null, expectedAttempt, cancellationToken);

    public Task<bool> CanRecoverFailedInstanceAsync(string batchId, string styleId, string pageInstanceId,
        int expectedAttempt, CancellationToken cancellationToken) =>
        CanRecoverAsync(batchId, styleId, null, null, pageInstanceId, expectedAttempt, cancellationToken);

    private async Task<bool> CanRecoverAsync(string batchId, string styleId, string? pageId, string? outputVariantId,
        string? pageInstanceId, int expectedAttempt, CancellationToken cancellationToken)
    {
        var batch = await store.GetAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (batch?.OutputSelection is null) return false;
        var journal = await store.LoadRenderBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        var page = journal?.Pages.SingleOrDefault(item => item.StyleId == styleId && (pageId is null || item.PageId == pageId)
            && item.Instance?.PageInstanceId == pageInstanceId && item.OutputVariantId == outputVariantId && item.Attempt == expectedAttempt);
        if (journal is null || page?.Status != RenderBatchPageStatus.Failed
            || !RenderBatchJournalValidator.CanRegenerate(journal, page)
            || !RenderStyleOutputPublicationData.SupportsPublicationJournal(journal)) return false;
        var registration = await store.LoadRenderOutputRegistrationAsync(batchId, cancellationToken).ConfigureAwait(false);
        var style = registration?.Styles.SingleOrDefault(item => item.Target.StyleId == styleId);
        if (style is null || !style.Pages.Any(item => item.PageId == page.PageId && item.OutputVariantId == outputVariantId && item.Instance == page.Instance
                && item.PlanDigest is not null)) return false;
        // Once a style has a publication intent, recovery must finish that intent rather than
        // create another execution against a public destination.
        if (await store.LoadRenderOutputPublicationAsync(batchId, styleId, cancellationToken).ConfigureAwait(false) is not null)
            return false;
        if (await store.LoadRenderOutputStageAsync(batchId, styleId, cancellationToken).ConfigureAwait(false) is null) return false;
        return !Directory.Exists(style.Target.FinalDirectory);
    }

    internal async Task<RenderStyleOutputPreparation> PrepareForRenderAsync(RenderBatchJournal journal, CancellationToken token)
    {
        var current = await store.LoadRenderBatchAsync(journal.BatchId, token).ConfigureAwait(false) ?? throw Invalid();
        if (!RenderStyleOutputPublicationData.Equal(current, journal)) throw Invalid();
        return await new RenderStyleOutputRegistry(store).OpenForRenderAsync(journal.BatchId, token).ConfigureAwait(false);
    }

    internal async Task<string?> GetVerifiedDirectoryAsync(string batchId, string styleId, CancellationToken token)
    {
        var batch = await store.GetAsync(batchId, token).ConfigureAwait(false) ?? throw Invalid();
        if (batch.OutputSelection is null) throw Invalid();
        var journal = await store.LoadRenderBatchAsync(batchId, token).ConfigureAwait(false);
        if (journal is null) return null;
        var registration = await store.LoadRenderOutputRegistrationAsync(batchId, token).ConfigureAwait(false);
        if (registration is null) return null;
        var style = registration.Styles.SingleOrDefault(item => item.Target.StyleId == styleId) ?? throw Invalid();
        var publication = await store.LoadRenderOutputPublicationAsync(batchId, styleId, token).ConfigureAwait(false);
        if (publication?.State != RenderStyleOutputPublicationData.Committed) return null;
        var stage = await store.LoadRenderOutputStageAsync(batchId, styleId, token).ConfigureAwait(false) ?? throw Invalid();
        RenderStyleOutputPublicationData.ValidateAgainst(publication, registration, journal, stage, requireCertainBatch: false);
        var pins = new Dictionary<string, RenderOutputDirectoryLease>(StringComparer.OrdinalIgnoreCase);
        var originals = new List<PreparedRenderPage>();
        try
        {
            var target = style.Target;
            pins.Add(target.SourceDirectory, RenderOutputDirectoryLease.Open(target.SourceDirectory));
            if (!pins.ContainsKey(target.ParentDirectory)) pins.Add(target.ParentDirectory, RenderOutputDirectoryLease.Open(target.ParentDirectory));
            if (pins[target.SourceDirectory].Identity != style.SourceIdentity || pins[target.ParentDirectory].Identity != style.ParentIdentity) throw Invalid();
            RenderStyleOutputPlanner.ValidatePhysicalBoundaries([target], registration.EvidenceRoot, pins);
            if (Exists(target.StagingDirectory)) throw Invalid();
            using var final = RenderOutputDirectoryLease.Open(target.FinalDirectory);
            if (final.Identity != stage.Identity) throw Invalid();
            foreach (var page in publication.Pages)
            {
                var current = journal.Pages.Single(item => item.StyleId == styleId && item.PageId == page.PageId
                    && item.OutputVariantId == page.OutputVariantId && item.Instance == page.Instance && item.Attempt == page.Attempt);
                var original = await RenderPageCommitter.OpenCommittedPageAsync(new LocalAppPathPolicy(batch.OutputRoot), commits, current.Commit!, token).ConfigureAwait(false);
                originals.Add(original);
                var files = original.Manifest.Files.Select(file => new RenderPublicFile(file.FileName, file.ByteLength, file.Sha256, current.Commit!.ManifestSha256)).ToImmutableArray();
                if (!RenderStyleOutputPublicationData.Equal(page.Files, files)) throw Invalid();
            }
            using var verified = await OpenVerifiedFileSetAsync(publication, final.Path, token).ConfigureAwait(false);
            return final.Path;
        }
        finally
        {
            foreach (var original in originals) original.Dispose();
            foreach (var pin in pins.Values.Reverse()) pin.Dispose();
        }
    }

    public async Task PublishAsync(string batchId, RenderBatchJournal journal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _publishing, 1, 0) != 0) throw new InvalidOperationException("RENDER_OUTPUT_BUSY");
        try
        {
            var batch = await store.GetAsync(batchId, cancellationToken).ConfigureAwait(false) ?? throw Invalid();
            if (batch.OutputSelection is null) return;
            var current = await store.LoadRenderBatchAsync(batchId, cancellationToken).ConfigureAwait(false) ?? throw Invalid();
            if (!RenderStyleOutputPublicationData.Equal(current, journal)
                || !RenderStyleOutputPublicationData.SupportsPublicationJournal(current)) throw Invalid();
            if (journal.Pages.Any(page => page.Status is RenderBatchPageStatus.Unknown or RenderBatchPageStatus.Running or RenderBatchPageStatus.Submitting))
                throw Invalid("RENDER_OUTPUT_PUBLICATION_BLOCKED_BY_UNCERTAIN_PAGE");
            var registration = await store.LoadRenderOutputRegistrationAsync(batchId, cancellationToken).ConfigureAwait(false) ?? throw Invalid();
            foreach (var style in registration.Styles)
            {
                var required = style.Pages.Where(page => page.PlanDigest is not null).ToArray();
                if (required.Length == 0) continue;
                var pages = RenderStyleOutputPublicationData.LatestPagesForStyle(journal, style.Target.StyleId);
                if (pages.Count != required.Length || pages.Any(page => page.Status != RenderBatchPageStatus.Committed || page.Commit is null)) continue;
                await PublishStyleAsync(batch, registration, style, current, pages, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _publishing, 0); }
    }

    private async Task PublishStyleAsync(BatchDraft batch, RenderStyleOutputRegistration registration,
        RegisteredRenderStyleOutput style, RenderBatchJournal journal, IReadOnlyList<RenderBatchPage> pages, CancellationToken token)
    {
        var target = style.Target;
        var pins = new Dictionary<string, RenderOutputDirectoryLease>(StringComparer.OrdinalIgnoreCase);
        var originals = new List<PreparedRenderPage>();
        try
        {
            var parent = RenderOutputDirectoryLease.Open(target.ParentDirectory, true);
            pins.Add(target.ParentDirectory, parent);
            if (!pins.ContainsKey(target.SourceDirectory)) pins.Add(target.SourceDirectory, RenderOutputDirectoryLease.Open(target.SourceDirectory));
            if (parent.Identity != style.ParentIdentity || pins[target.SourceDirectory].Identity != style.SourceIdentity) throw Invalid();
            RenderStyleOutputPlanner.ValidatePhysicalBoundaries([target], registration.EvidenceRoot, pins);
            var stageFact = await store.LoadRenderOutputStageAsync(batch.BatchId, target.StyleId, token).ConfigureAwait(false) ?? throw Invalid();
            if (stageFact.Identity.Volume != parent.Identity.Volume) throw Invalid();
            var publicPages = ImmutableArray.CreateBuilder<RenderPublicPage>();
            foreach (var page in pages.OrderBy(item => item.PageId, StringComparer.Ordinal).ThenBy(item => item.OutputVariantId, StringComparer.Ordinal))
            {
                var original = await RenderPageCommitter.OpenCommittedPageAsync(new LocalAppPathPolicy(batch.OutputRoot), commits, page.Commit!, token).ConfigureAwait(false);
                originals.Add(original);
                publicPages.Add(new(page.PageId, page.OutputVariantId, page.Attempt, page.Commit!.ManifestSha256,
                    original.Manifest.Files.Select(file => new RenderPublicFile(file.FileName, file.ByteLength, file.Sha256, page.Commit.ManifestSha256)).ToImmutableArray(), page.Instance));
            }
            var registrationHash = RenderStyleOutputRegistrationData.Hash(RenderStyleOutputRegistrationData.Encode(registration));
            var manifest = new RenderStylePublicManifest(journal.Version == 4 ? 2 : 1, batch.BatchId, target.StyleId, registrationHash, publicPages.ToImmutable());
            var candidate = new RenderStyleOutputPublication(journal.Version == 4 ? 2 : 1, batch.BatchId, target.StyleId, RenderStyleOutputPublicationData.Copying,
                registrationHash, stageFact.Identity, null, target.FinalDirectory,
                RenderStyleOutputPublicationData.Hash(RenderStyleOutputPublicationData.Bytes(manifest)), manifest.Pages);
            RenderStyleOutputPublicationData.ValidateAgainst(candidate, registration, journal, stageFact);
            var saved = await store.LoadRenderOutputPublicationAsync(batch.BatchId, target.StyleId, token).ConfigureAwait(false);
            if (saved is not null && !RenderStyleOutputPublicationData.Equal(candidate,
                saved with { State = RenderStyleOutputPublicationData.Copying, FinalIdentity = null })) throw Invalid();
            var publication = saved ?? candidate;
            if (Exists(target.FinalDirectory))
            {
                // Only a durable Verified intent can recover a rename whose final DB write was lost.
                if (saved is null || saved.State == RenderStyleOutputPublicationData.Copying || Exists(target.StagingDirectory)) throw Invalid();
                using var final = RenderOutputDirectoryLease.Open(target.FinalDirectory);
                if (final.Identity != stageFact.Identity) throw Invalid();
                using var verified = await OpenVerifiedFileSetAsync(publication, final.Path, token).ConfigureAwait(false);
                await store.SaveRenderOutputPublicationAsync(publication with
                { State = RenderStyleOutputPublicationData.Committed, FinalIdentity = final.Identity }, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            if (publication.State == RenderStyleOutputPublicationData.Committed) throw Invalid();
            using var stage = RenderOutputDirectoryLease.Open(target.StagingDirectory, requireRenameAccess: true);
            if (stage.Identity != stageFact.Identity) throw Invalid();
            if (saved is null)
            {
                if (Directory.EnumerateFileSystemEntries(stage.Path).Any()) throw Invalid();
                await store.SaveRenderOutputPublicationAsync(publication, token).ConfigureAwait(false);
            }
            if (publication.State == RenderStyleOutputPublicationData.Copying)
            {
                await FillStageAsync(publication, stage.Path, originals, token).ConfigureAwait(false);
                using var verified = await OpenVerifiedFileSetAsync(publication, stage.Path, token).ConfigureAwait(false);
                publication = publication with { State = RenderStyleOutputPublicationData.Verified };
                await store.SaveRenderOutputPublicationAsync(publication, token).ConfigureAwait(false);
            }
            else await VerifyFileSetAsync(publication, stage.Path, token).ConfigureAwait(false);
            // All media handles have closed; source/parent/stage identity pins remain held.
            // The same source directory handle is renamed, ReplaceIfExists=false.
            token.ThrowIfCancellationRequested();
            stage.RenameToChild(parent, target.DirectoryName);
            using var finalFiles = await OpenVerifiedFileSetAsync(publication, stage.Path, CancellationToken.None).ConfigureAwait(false);
            await store.SaveRenderOutputPublicationAsync(publication with
            { State = RenderStyleOutputPublicationData.Committed, FinalIdentity = stage.Identity }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            foreach (var original in originals) original.Dispose();
            foreach (var lease in pins.Values.Reverse()) lease.Dispose();
        }
    }

    private async Task FillStageAsync(RenderStyleOutputPublication publication, string directory,
        IReadOnlyList<PreparedRenderPage> sources, CancellationToken token)
    {
        var expected = RenderStyleOutputPublicationData.ExpectedFiles(publication);
        var records = await store.LoadRenderPublicFilesAsync(publication.BatchId, publication.StyleId, token).ConfigureAwait(false);
        // Reject every unknown entry before writing any bytes, including plausible same-name files.
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            if (!records.Any(record => record.FileName == Path.GetFileName(path))) throw Invalid("RENDER_OUTPUT_UNREGISTERED_FILE");
        if (records.Any(record => expected.All(file => file.FileName != record.FileName))) throw Invalid();
        foreach (var file in expected)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, file.FileName);
            var saved = records.SingleOrDefault(record => record.FileName == file.FileName);
            if (saved is not null && (saved.ByteLength != file.ByteLength || saved.Sha256 != file.Sha256)) throw Invalid();
            if (saved?.Verified == true)
            {
                await VerifyFileAsync(path, file, saved, token).ConfigureAwait(false);
                continue;
            }
            BridgeOutputReservation.ValidatePath(path);
            // Open never recreates a missing registered file; CreateNew never adopts an existing file.
            await using var output = new FileStream(path, saved is null ? FileMode.CreateNew : FileMode.Open,
                FileAccess.ReadWrite, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            BridgeResourceLease.VerifyOpenedPath(output.SafeFileHandle, path);
            BridgeOutputReservation.ValidatePath(path);
            var identity = OwnedRenderResourceFile.ReadIdentity(output.SafeFileHandle);
            if (saved is not null && saved.Identity != identity) throw Invalid();
            var owned = saved ?? new RenderPublicFileOwnership(1, publication.BatchId, publication.StyleId,
                file.FileName, file.ByteLength, file.Sha256, identity, false);
            if (saved is null) await store.SaveRenderPublicFileAsync(owned, CancellationToken.None).ConfigureAwait(false);
            // Only this original, durably registered, unfinished private file may be truncated.
            output.SetLength(0); output.Position = 0;
            if (file.FileName == RenderStyleOutputPublicationData.ManifestName)
                await output.WriteAsync(RenderStyleOutputPublicationData.ManifestBytes(publication), token).ConfigureAwait(false);
            else if (file.FileName == RenderStyleOutputPublicationData.MarkerName)
                await output.WriteAsync(RenderStyleOutputPublicationData.MarkerBytes(publication), token).ConfigureAwait(false);
            else
            {
                var source = sources.Single(item => item.Checkpoint.ManifestSha256 == file.SourceManifestSha256);
                using var input = RenderPageCommitter.Open(Path.Combine(source.CurrentPath, file.FileName), FileShare.Read);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }
            await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); output.Position = 0;
            if (output.Length != file.ByteLength || await HashAsync(output, token).ConfigureAwait(false) != file.Sha256) throw Invalid();
            await store.SaveRenderPublicFileAsync(owned with { Verified = true }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal async Task VerifyFileSetAsync(RenderStyleOutputPublication publication, string directory, CancellationToken token)
    {
        using var verified = await OpenVerifiedFileSetAsync(publication, directory, token).ConfigureAwait(false);
    }

    private async Task<VerifiedPublicFiles> OpenVerifiedFileSetAsync(RenderStyleOutputPublication publication, string directory, CancellationToken token)
    {
        RenderStyleOutputPublicationData.Validate(publication);
        var expected = RenderStyleOutputPublicationData.ExpectedFiles(publication);
        var records = await store.LoadRenderPublicFilesAsync(publication.BatchId, publication.StyleId, token).ConfigureAwait(false);
        var names = expected.Select(file => file.FileName).ToHashSet(StringComparer.Ordinal);
        if (records.Count != expected.Count || !names.SetEquals(records.Select(file => file.FileName))
            || !names.SetEquals(Directory.EnumerateFileSystemEntries(directory).Select(path => Path.GetFileName(path)))) throw Invalid();
        var verified = new VerifiedPublicFiles();
        try
        {
            foreach (var file in expected)
                verified.Streams.Add(await OpenVerifiedFileAsync(Path.Combine(directory, file.FileName), file,
                    records.Single(record => record.FileName == file.FileName), token).ConfigureAwait(false));
            // Keep all expected bytes and identities pinned together through the database transition.
            // Windows requires child handles closed for directory rename; the final set is independently
            // reacquired and verified after that handoff, before Committed is recorded.
            if (!names.SetEquals(Directory.EnumerateFileSystemEntries(directory).Select(path => Path.GetFileName(path)))) throw Invalid();
            return verified;
        }
        catch { verified.Dispose(); throw; }
    }
    private static async Task VerifyFileAsync(string path, RenderPublicFile expected, RenderPublicFileOwnership owned, CancellationToken token)
    {
        using var stream = await OpenVerifiedFileAsync(path, expected, owned, token).ConfigureAwait(false);
    }
    private static async Task<FileStream> OpenVerifiedFileAsync(string path, RenderPublicFile expected, RenderPublicFileOwnership owned, CancellationToken token)
    {
        if (!owned.Verified || owned.ByteLength != expected.ByteLength || owned.Sha256 != expected.Sha256) throw Invalid();
        var stream = RenderPageCommitter.Open(path, FileShare.Read);
        try
        {
            if (OwnedRenderResourceFile.ReadIdentity(stream.SafeFileHandle) != owned.Identity || stream.Length != expected.ByteLength
                || await HashAsync(stream, token).ConfigureAwait(false) != expected.Sha256) throw Invalid();
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    private sealed class VerifiedPublicFiles : IDisposable
    {
        internal List<FileStream> Streams { get; } = [];
        public void Dispose() { foreach (var stream in Streams) stream.Dispose(); }
    }
    private static async Task<string> HashAsync(Stream stream, CancellationToken token) =>
        Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static InvalidDataException Invalid(string code = "RENDER_OUTPUT_PUBLICATION_RECOVERY_REQUIRED") =>
        RenderStyleOutputPublicationData.Invalid(code);
}
