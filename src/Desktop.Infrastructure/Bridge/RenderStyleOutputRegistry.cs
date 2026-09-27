using Desktop.Application.Rendering;
using Desktop.Domain.Inputs;
using Desktop.Infrastructure.Persistence;

namespace Desktop.Infrastructure.Bridge;

/// <summary>Pre-dispatch target/empty-stage registration only. Never renders, publishes, scans or deletes.</summary>
internal sealed class RenderStyleOutputRegistry(SqliteDesktopStore store)
{
    internal async Task<RenderStyleOutputPreparation> OpenForRenderAsync(string batchId, CancellationToken token)
    {
        var batch = await store.GetAsync(batchId, token).ConfigureAwait(false) ?? throw RenderStyleOutputRegistrationData.Invalid();
        var current = await store.LoadRenderBatchAsync(batchId, token).ConfigureAwait(false) ?? throw RenderStyleOutputRegistrationData.Invalid();
        if (batch.OutputSelection is null || !RenderStyleOutputPublicationData.SupportsPublicationJournal(current))
            throw RenderStyleOutputRegistrationData.Invalid();
        RenderStyleOutputPreparation preparation;
        if (current.Revision == 1) preparation = await PrepareAsync(batchId, token).ConfigureAwait(false);
        else
        {
            var registration = await store.LoadRenderOutputRegistrationAsync(batchId, token).ConfigureAwait(false)
                ?? throw RenderStyleOutputRegistrationData.Invalid();
            preparation = await OpenStages(batch, registration, token, current).ConfigureAwait(false);
        }
        try
        {
            preparation.ProbePendingStagesAndRelease(current, token);
            return preparation; // Source/parent/evidence ancestry remain pinned for the render session.
        }
        catch { preparation.Dispose(); throw; }
    }

    internal async Task<RenderStyleOutputPreparation> PrepareAsync(string batchId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var batch = await store.GetAsync(batchId, token).ConfigureAwait(false)
            ?? throw RenderStyleOutputRegistrationData.Invalid();
        var current = await store.LoadRenderBatchAsync(batchId, token).ConfigureAwait(false)
            ?? throw RenderStyleOutputRegistrationData.Invalid();
        // This slice prepares first delivery only. Never use it to reopen an uncertain execution,
        // mutate a frozen page set or retrofit public output onto a historical batch.
        if (batch.OutputSelection is null || current.Revision != 1 || current.Status != RenderBatchStatus.Prepared
            || current.PauseRequested || current.CancelRequested
            || current.Pages.Any(page => page.Status is not (RenderBatchPageStatus.Planned or RenderBatchPageStatus.Blocked)))
            throw RenderStyleOutputRegistrationData.Invalid();
        var registration = await store.LoadRenderOutputRegistrationAsync(batchId, token).ConfigureAwait(false);
        if (registration is not null) return await OpenStages(batch, registration, token).ConfigureAwait(false);

        using var preflight = RenderStyleOutputPlanner.Preflight(batch, token);
        registration = RenderStyleOutputRegistrationData.Capture(batch, current, preflight);
        await store.RegisterRenderOutputsAsync(registration, token).ConfigureAwait(false);
        // Keep the original pins through registration and acquisition of the returned lease set.
        return await OpenStages(batch, registration, token).ConfigureAwait(false);
    }

    private async Task<RenderStyleOutputPreparation> OpenStages(BatchDraft batch,
        RenderStyleOutputRegistration registration, CancellationToken token, RenderBatchJournal? recovery = null)
    {
        var leases = new Dictionary<string, RenderOutputDirectoryLease>(StringComparer.OrdinalIgnoreCase);
        var stageFacts = new List<RenderStyleOutputStage>();
        var missing = new List<RegisteredRenderStyleOutput>();
        try
        {
            // As in pure preflight, acquire every writable parent before read-only sources.
            foreach (var style in registration.Styles)
            {
                token.ThrowIfCancellationRequested();
                var target = style.Target;
                if (!leases.ContainsKey(target.ParentDirectory))
                    leases.Add(target.ParentDirectory, RenderOutputDirectoryLease.Open(target.ParentDirectory, true));
                RequireIdentity(leases[target.ParentDirectory], style.ParentIdentity);
            }
            foreach (var style in registration.Styles)
            {
                token.ThrowIfCancellationRequested();
                var target = style.Target;
                if (!leases.ContainsKey(target.SourceDirectory))
                    leases.Add(target.SourceDirectory, RenderOutputDirectoryLease.Open(target.SourceDirectory));
                RequireIdentity(leases[target.SourceDirectory], style.SourceIdentity);
                var saved = await store.LoadRenderOutputStageAsync(batch.BatchId, target.StyleId, token).ConfigureAwait(false);
                if (saved is null)
                {
                    RenderStyleOutputPlanner.RequireAbsent(target.FinalDirectory, target.StyleId);
                    RenderStyleOutputPlanner.RequireAbsent(target.StagingDirectory, target.StyleId);
                    if (style.Pages.Any(page => page.PlanDigest is not null))
                    {
                        if (recovery is not null) throw RenderStyleOutputRegistrationData.Invalid();
                        missing.Add(style);
                    }
                    continue;
                }
                if (!style.Pages.Any(page => page.PlanDigest is not null) || saved.Identity.Volume != style.ParentIdentity.Volume)
                    throw RenderStyleOutputRegistrationData.Invalid();
                var publication = recovery is null ? null
                    : await store.LoadRenderOutputPublicationAsync(batch.BatchId, target.StyleId, token).ConfigureAwait(false);
                if (publication is not null)
                    RenderStyleOutputPublicationData.ValidateAgainst(publication, registration, recovery!, saved, requireCertainBatch: false);
                if (Directory.Exists(target.FinalDirectory) && publication?.State is "Verified" or "Committed")
                {
                    RenderStyleOutputPlanner.RequireAbsent(target.StagingDirectory, target.StyleId);
                    var final = RenderOutputDirectoryLease.Open(target.FinalDirectory);
                    leases.Add(target.FinalDirectory, final); RequireIdentity(final, saved.Identity);
                }
                else
                {
                    RenderStyleOutputPlanner.RequireAbsent(target.FinalDirectory, target.StyleId);
                    if (publication?.State == "Committed") throw RenderStyleOutputRegistrationData.Invalid();
                    var stage = RenderOutputDirectoryLease.Open(target.StagingDirectory);
                    leases.Add(target.StagingDirectory, stage); RequireIdentity(stage, saved.Identity);
                    // Without an owned publication intent, non-empty staging data is an unknown conflict.
                    if (publication is null && Directory.EnumerateFileSystemEntries(stage.Path).Any()) throw RenderStyleOutputRegistrationData.Invalid();
                }
                stageFacts.Add(saved);
            }
            RenderStyleOutputPlanner.ValidatePhysicalBoundaries(registration.Styles.Select(style => style.Target), registration.EvidenceRoot, leases);
            foreach (var style in missing)
            {
                token.ThrowIfCancellationRequested();
                var target = style.Target;
                RequireIdentity(leases[target.SourceDirectory], style.SourceIdentity);
                RequireIdentity(leases[target.ParentDirectory], style.ParentIdentity);
                RenderStyleOutputPlanner.RequireAbsent(target.FinalDirectory, target.StyleId);
                var created = leases[target.ParentDirectory].CreateStagingChild(Path.GetFileName(target.StagingDirectory));
                leases.Add(target.StagingDirectory, created);
                var fact = new RenderStyleOutputStage(batch.BatchId, target.StyleId, created.Identity);
                // Once created, try to make ownership durable even if the caller cancels. A DB failure
                // leaves an unregistered conflict, never an adopted directory or a cleanup candidate.
                await store.RegisterRenderOutputStageAsync(fact, CancellationToken.None).ConfigureAwait(false);
                stageFacts.Add(fact);
            }
            token.ThrowIfCancellationRequested();
            return new(registration, stageFacts, leases);
        }
        catch
        {
            foreach (var lease in leases.Values.Reverse()) lease.Dispose();
            throw;
        }
    }

    private static void RequireIdentity(RenderOutputDirectoryLease lease, RenderOutputDirectoryIdentity identity)
    {
        lease.Verify();
        if (lease.Identity != identity) throw new RenderOutputTargetException("RENDER_OUTPUT_DIRECTORY_IDENTITY_MISMATCH");
    }
}

internal sealed class RenderStyleOutputPreparation(RenderStyleOutputRegistration registration,
    IReadOnlyList<RenderStyleOutputStage> stages, Dictionary<string, RenderOutputDirectoryLease> leases) : IDisposable
{
    internal RenderStyleOutputRegistration Registration { get; } = registration;
    internal IReadOnlyList<RenderStyleOutputStage> Stages { get; } = stages;
    internal void VerifyBeforeDispatch(RenderBatchPage page)
    {
        var style = Registration.Styles.Single(item => item.Target.StyleId == page.StyleId);
        leases[style.Target.SourceDirectory].Verify(); leases[style.Target.ParentDirectory].Verify();
        RenderStyleOutputPlanner.RequireAbsent(style.Target.FinalDirectory, style.Target.StyleId);
        using var stage = RenderOutputDirectoryLease.Open(style.Target.StagingDirectory);
        if (stage.Identity != Stages.Single(item => item.StyleId == page.StyleId).Identity
            || Directory.EnumerateFileSystemEntries(stage.Path).Any()) throw RenderStyleOutputRegistrationData.Invalid();
    }

    internal void ProbePendingStagesAndRelease(RenderBatchJournal journal, CancellationToken token)
    {
        foreach (var style in Registration.Styles)
        {
            token.ThrowIfCancellationRequested();
            var path = style.Target.StagingDirectory;
            if (!leases.TryGetValue(path, out var stage)) continue;
            stage.Verify();
            if (journal.Pages.Any(page => page.StyleId == style.Target.StyleId && page.Status == RenderBatchPageStatus.Planned))
            {
                // A real, small create/write/flush check, not just an access-bit claim. Cleanup owns
                // only the original creation handle identity. A crash before cleanup leaves a conflict;
                // a later process never guesses ownership from this temporary name.
                var probePath = Path.Combine(path, ".edp-write-check-" + Guid.NewGuid().ToString("N") + ".tmp");
                OwnedRenderResourceFile? owned = null;
                try
                {
                    using var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    BridgeResourceLease.VerifyOpenedPath(probe.SafeFileHandle, probePath);
                    owned = OwnedRenderResourceFile.Capture(probePath, probe.SafeFileHandle);
                    probe.WriteByte(0x5a); probe.Flush(true); probe.Position = 0;
                    if (probe.Length != 1 || probe.ReadByte() != 0x5a) throw RenderStyleOutputRegistrationData.Invalid();
                }
                finally { owned?.Dispose(); }
            }
            // Publication needs DELETE access to this leaf; keeping a deny-delete stage pin would
            // deadlock our own rename. Durable identity is rechecked by the publisher, while these
            // source/parent pins continue to protect all source and destination ancestors.
            stage.Dispose(); leases.Remove(path);
        }
    }

    public void Dispose()
    {
        foreach (var lease in leases.Values.Reverse()) lease.Dispose();
        leases.Clear();
    }
}
