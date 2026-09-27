using System.Text.Json;

namespace Desktop.Application.Rendering;

public sealed partial class RenderBatchCoordinator
{
    public async Task<SizeTableRepairBaseline> LoadSizeTableRepairAsync(string batchId, string styleId,
        string pageId, string? outputVariantId, CancellationToken cancellationToken)
    {
        if (sizeRepairs is null || repairAdmission is null) throw new InvalidOperationException("SIZE_REPAIR_UNAVAILABLE");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        if (!await _run.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("RENDER_BATCH_BUSY");
        try
        {
            var journal = await Require(batchId, cancellationToken).ConfigureAwait(false);
            var page = RequireRepairPage(journal, styleId, pageId, outputVariantId);
            using var admission = await repairAdmission.AcquireAsync(batchId, cancellationToken).ConfigureAwait(false);
            var table = await sizeRepairs.LoadBaseAsync(journal, page, cancellationToken).ConfigureAwait(false);
            if (!SizeTableRepairData.MatchesPlan(table, page.PlanJson!))
                throw new InvalidOperationException("SIZE_REPAIR_BASELINE_MISMATCH");
            using var plan = JsonDocument.Parse(page.PlanJson!);
            return new(new(batchId, styleId, pageId, outputVariantId, journal.Revision, page.Attempt,
                plan.RootElement.GetProperty("plan_digest").GetString()!, table.Revision, table.DataDigest),
                SizeTableRepairData.ToEditorDocument(table));
        }
        finally { _run.Release(); }
    }

    public async Task<RenderBatchJournal> PrepareSizeTableRepairAsync(SizeTablePageRepairCommand command,
        CancellationToken cancellationToken)
    {
        if (sizeRepairs is null || repairAdmission is null) throw new InvalidOperationException("SIZE_REPAIR_UNAVAILABLE");
        var requestDigest = SizeTableRepairData.RequestDigest(command);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        if (!await _run.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("RENDER_BATCH_BUSY");
        try
        {
            var target = command.Target;
            await RequireLegacyEditing(target.BatchId, cancellationToken).ConfigureAwait(false);
            var journal = await Require(target.BatchId, cancellationToken).ConfigureAwait(false);
            var existing = journal.Pages.SingleOrDefault(page => page.SizeTableRepair?.RequestId == command.RequestId);
            if (existing is not null)
            {
                if (existing.SizeTableRepair!.RequestDigest != requestDigest)
                    throw new InvalidOperationException("SIZE_REPAIR_REQUEST_CONFLICT");
                return journal; // Lost response: do not rasterize, append or dispatch again.
            }
            var page = RequireRepairPage(journal, target.StyleId, target.PageId, target.OutputVariantId);
            RequireRepairBaseline(journal, page, target);
            using var admission = await repairAdmission.AcquireAsync(target.BatchId, cancellationToken).ConfigureAwait(false);
            var candidate = await sizeRepairs.PrepareAsync(command, journal, page, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (candidate.Origin.RequestId != command.RequestId || candidate.Origin.RequestDigest != requestDigest
                || candidate.Origin.ExpectedJournalRevision != target.ExpectedJournalRevision
                || candidate.Origin.BaseAttempt != target.ExpectedAttempt || candidate.Origin.BasePlanDigest != target.BasePlanDigest)
                throw new InvalidOperationException("SIZE_REPAIR_BASELINE_MISMATCH");
            RenderPlanSizeTableRepair.Validate(page.PlanJson!, candidate.PlanJson, candidate.Origin);
            return await Change(target.BatchId, current =>
            {
                // Control requests or other writers may have advanced the journal during resource preparation.
                var currentPage = RequireRepairPage(current, target.StyleId, target.PageId, target.OutputVariantId);
                RequireRepairBaseline(current, currentPage, target);
                if (currentPage != page) throw new InvalidOperationException("SIZE_REPAIR_BASELINE_MISMATCH");
                var added = page with { Attempt = checked(page.Attempt + 1), PlanJson = candidate.PlanJson,
                    Status = RenderBatchPageStatus.Planned, ReasonCode = page.PreparationReasonCode!,
                    ExecutionId = null, Commit = null, SizeTableRepair = candidate.Origin };
                return current with { Version = 3, Status = RenderBatchStatus.Prepared, PauseRequested = false,
                    CancelRequested = false, RegenerationPageIndex = current.Pages.Count, Pages = [.. current.Pages, added] };
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _run.Release(); }
    }

    private static RenderBatchPage RequireRepairPage(RenderBatchJournal journal, string styleId,
        string pageId, string? outputVariantId)
    {
        var page = journal.Pages.Where(item => item.StyleId == styleId && item.PageId == pageId
            && item.OutputVariantId == outputVariantId).MaxBy(item => item.Attempt);
        if (page is null || !RenderBatchJournalValidator.CanRegenerate(journal, page))
            throw new InvalidOperationException("SIZE_REPAIR_NOT_ALLOWED");
        return page;
    }

    private static void RequireRepairBaseline(RenderBatchJournal journal, RenderBatchPage page, SizeTableRepairTarget target)
    {
        using var plan = JsonDocument.Parse(page.PlanJson!);
        if (journal.Revision != target.ExpectedJournalRevision || page.Attempt != target.ExpectedAttempt
            || plan.RootElement.GetProperty("plan_digest").GetString() != target.BasePlanDigest)
            throw new InvalidOperationException("SIZE_REPAIR_REVISION_CONFLICT");
    }
}
