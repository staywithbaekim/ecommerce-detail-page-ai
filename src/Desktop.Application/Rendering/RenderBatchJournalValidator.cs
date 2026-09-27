using System.Text;
using System.Text.Json;

namespace Desktop.Application.Rendering;

public static class RenderBatchJournalValidator
{
    public const int MaxBytes = 64 * 1024 * 1024;

    public static void Validate(RenderBatchJournal journal, IRenderPlanValidator plans)
    {
        if (journal.Version is not (1 or 2 or 3) || !Id(journal.BatchId) || journal.Revision < 1
            || !Enum.IsDefined(journal.Status) || journal.Pages is null || journal.Pages.Count is < 1 or > 10000)
            throw Invalid();
        if (journal.Version == 1 && journal.RegenerationPageIndex is not null
            || journal.Version >= 2 && journal.RegenerationPageIndex != journal.Pages.Count - 1
            || journal.Version < 3 && journal.Pages.Any(page => page?.SizeTableRepair is not null)
            || journal.Version == 3 && !journal.Pages.Any(page => page?.SizeTableRepair is not null)) throw Invalid();
        var identities = new HashSet<(string, string, string?, int)>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var planIds = new HashSet<string>(StringComparer.Ordinal);
        var artifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repairRequests = new HashSet<string>(StringComparer.Ordinal);
        var totalBytes = 0L;
        foreach (var page in journal.Pages)
        {
            if (page is null || !Id(page.StyleId) || !Id(page.PageId) || page.OutputVariantId is { } variant && !Id(variant)
                || page.Attempt < 1 || !Enum.IsDefined(page.Status) || !Id(page.ReasonCode)
                || page.ExecutionId is { } execution && !Id(execution)
                || page.PreparationReasonCode is { } preparationReason && !Id(preparationReason)
                || page.OutputVariantName is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl))
                || !identities.Add((page.StyleId, page.PageId, page.OutputVariantId, page.Attempt))) throw Invalid();
            if (page.SizeTableRepair is { } repair)
            {
                if (!repairRequests.Add(repair.RequestId) || repair.ExpectedJournalRevision >= journal.Revision) throw Invalid();
                var prior = journal.Pages.FirstOrDefault(item => item is not null && SamePage(item, page) && item.Attempt == repair.BaseAttempt);
                if (prior is null || page.Attempt != prior.Attempt + 1) throw Invalid();
                ValidateSizeRepair(journal.BatchId, prior, page);
            }
            if (page.PlanJson is null)
            {
                if (page.Status != RenderBatchPageStatus.Blocked || page.ExecutionId is not null || page.Commit is not null) throw Invalid();
                continue;
            }
            totalBytes += Encoding.UTF8.GetByteCount(page.PlanJson);
            if (totalBytes > MaxBytes || !plans.Validate(page.PlanJson).IsValid) throw Invalid();
            using var document = JsonDocument.Parse(page.PlanJson);
            var plan = document.RootElement;
            if (plan.GetProperty("batch_id").GetString() != journal.BatchId
                || plan.GetProperty("product_id").GetString() != page.StyleId
                || plan.GetProperty("document").GetProperty("page_id").GetString() != page.PageId
                || RenderPlanVariantIdentity.Read(plan) != page.OutputVariantId
                || !planIds.Add(plan.GetProperty("plan_id").GetString()!)
                || !keys.Add(plan.GetProperty("idempotency_key").GetString()!)
                || !artifacts.Add(plan.GetProperty("output").GetProperty("artifact_set_id").GetString()!)) throw Invalid();
            if (page.Status == RenderBatchPageStatus.Blocked
                || page.Status is RenderBatchPageStatus.Planned or RenderBatchPageStatus.Submitting
                    && (page.ExecutionId is not null || page.Commit is not null)
                || page.Status == RenderBatchPageStatus.Running && page.ExecutionId is null
                || (page.Status == RenderBatchPageStatus.Committed) != (page.Commit is not null)) throw Invalid();
            if (page.Commit is { } commit && (commit.Page.TaskId != journal.BatchId
                || commit.Page.StyleId != page.StyleId || commit.Page.PageId != page.PageId
                || commit.Page.OutputVariantId != page.OutputVariantId || commit.Page.Attempt != page.Attempt
                || commit.Page.BridgeExecutionId != page.ExecutionId
                || commit.Page.PlanDigest != plan.GetProperty("plan_digest").GetString())) throw Invalid();
        }
    }

    public static void ValidateUpdate(RenderBatchJournal? previous, RenderBatchJournal next)
    {
        if (previous is null)
        {
            if (next.Version != 1 || next.Revision != 1 || next.Status != RenderBatchStatus.Prepared
                || next.Pages.Any(page => page.Status is not (RenderBatchPageStatus.Planned or RenderBatchPageStatus.Blocked)
                    || page.PreparationReasonCode is not null && page.PreparationReasonCode != page.ReasonCode)) throw Invalid();
            return;
        }
        if (previous.BatchId != next.BatchId || next.Revision != previous.Revision + 1)
            throw Invalid();
        if (next.Pages.Count != previous.Pages.Count)
        {
            ValidateRegeneration(previous, next);
            return;
        }
        if (previous.Version != next.Version || previous.RegenerationPageIndex != next.RegenerationPageIndex) throw Invalid();
        for (var i = 0; i < previous.Pages.Count; i++)
        {
            var before = previous.Pages[i]; var after = next.Pages[i];
            if (previous.Version >= 2 && previous.RegenerationPageIndex != i && before != after) throw Invalid();
            if (before.StyleId != after.StyleId || before.PageId != after.PageId || before.OutputVariantId != after.OutputVariantId
                || before.Attempt != after.Attempt || before.PlanJson != after.PlanJson
                || before.PreparationReasonCode != after.PreparationReasonCode || before.OutputVariantName != after.OutputVariantName
                || before.SizeTableRepair != after.SizeTableRepair
                || before.ExecutionId is not null && before.ExecutionId != after.ExecutionId
                || before.Commit is not null && before.Commit != after.Commit
                || before.Status is RenderBatchPageStatus.Committed or RenderBatchPageStatus.Failed or RenderBatchPageStatus.Cancelled && after.Status != before.Status
                || before.Status is RenderBatchPageStatus.Submitting or RenderBatchPageStatus.Running or RenderBatchPageStatus.Unknown
                    && after.Status == RenderBatchPageStatus.Planned) throw Invalid();
        }
    }

    public static bool CanRegenerate(RenderBatchJournal journal, RenderBatchPage page) =>
        journal.Status is RenderBatchStatus.Completed or RenderBatchStatus.CompletedWithIssues or RenderBatchStatus.Cancelled or RenderBatchStatus.Paused
        && journal.Pages.All(item => item.Status is not (RenderBatchPageStatus.Submitting or RenderBatchPageStatus.Running or RenderBatchPageStatus.Unknown))
        && page.PlanJson is not null && page.Status is RenderBatchPageStatus.Committed or RenderBatchPageStatus.Failed or RenderBatchPageStatus.Cancelled
        && journal.Pages.Where(item => SamePage(item, page)).Max(item => item.Attempt) == page.Attempt;

    private static void ValidateRegeneration(RenderBatchJournal previous, RenderBatchJournal next)
    {
        if (next.Pages.Count != previous.Pages.Count + 1
            || next.RegenerationPageIndex != previous.Pages.Count || next.Status != RenderBatchStatus.Prepared
            || next.PauseRequested || next.CancelRequested || !previous.Pages.SequenceEqual(next.Pages.Take(previous.Pages.Count))) throw Invalid();
        var added = next.Pages[^1];
        var prior = previous.Pages.Where(page => SamePage(page, added)).MaxBy(page => page.Attempt);
        if (prior is null || !CanRegenerate(previous, prior) || added.Attempt != checked(prior.Attempt + 1)
            || added.Status != RenderBatchPageStatus.Planned || added.ExecutionId is not null || added.Commit is not null
            || added.PlanJson is null || added.PreparationReasonCode != prior.PreparationReasonCode
            || added.OutputVariantName != prior.OutputVariantName || added.ReasonCode != prior.PreparationReasonCode) throw Invalid();
        if (added.SizeTableRepair is { } repair)
        {
            if (next.Version != 3 || repair.ExpectedJournalRevision != previous.Revision || repair.BaseAttempt != prior.Attempt
                || previous.Pages.Any(page => page.SizeTableRepair?.RequestId == repair.RequestId)) throw Invalid();
            ValidateSizeRepair(previous.BatchId, prior, added);
        }
        else if (next.Version != Math.Max(2, previous.Version)
            || !RenderPlanRegeneration.PreservesFacts(prior.PlanJson!, added.PlanJson)) throw Invalid();
    }

    private static void ValidateSizeRepair(string batchId, RenderBatchPage prior, RenderBatchPage added)
    {
        var repair = added.SizeTableRepair!;
        if (prior.PlanJson is null || added.PlanJson is null || !Id(repair.RequestId)
            || repair.ExpectedJournalRevision < 1 || repair.BaseAttempt != prior.Attempt) throw Invalid();
        RenderPlanSizeTableRepair.Validate(prior.PlanJson, added.PlanJson, repair);
        var before = SizeTableRepairData.Deserialize(repair.BaseTableJson);
        var after = SizeTableRepairData.ToEditorDocument(SizeTableRepairData.Deserialize(repair.TableJson));
        var command = new SizeTablePageRepairCommand(repair.RequestId,
            new(batchId, prior.StyleId, prior.PageId, prior.OutputVariantId, repair.ExpectedJournalRevision,
                prior.Attempt, repair.BasePlanDigest, before.Revision, before.DataDigest), new(after.Columns, after.Rows));
        if (SizeTableRepairData.RequestDigest(command) != repair.RequestDigest) throw Invalid();
    }

    public static bool SamePage(RenderBatchPage left, RenderBatchPage right) => left.StyleId == right.StyleId
        && left.PageId == right.PageId && left.OutputVariantId == right.OutputVariantId;

    private static bool Id(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':');
    private static InvalidDataException Invalid() => new("RENDER_BATCH_JOURNAL_INVALID");
}
