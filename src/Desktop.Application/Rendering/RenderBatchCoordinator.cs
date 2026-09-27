using Desktop.Application.Ai;
using Desktop.Application.Tasks;
using Desktop.Domain.Tasks;
using TaskStatus = Desktop.Domain.Tasks.TaskStatus;

namespace Desktop.Application.Rendering;

/// <summary>Serial page execution with durable intent before delivery, style-boundary pause and evidence-first recovery.</summary>
public sealed partial class RenderBatchCoordinator(IRenderBatchStore store, IRenderBatchSessionFactory sessions,
    IRenderPlanValidator plans, TaskCoordinator tasks, TimeProvider time,
    ISizeTablePageRepairPreparer? sizeRepairs = null, ISizeTableRepairAdmission? repairAdmission = null,
    IRenderStyleOutputPublisher? publicOutputs = null)
    : IRenderBatchWorkflow, ISizeTablePageRepairWorkflow, IAsyncDisposable
{
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposeStarted;

    public Task<RenderBatchJournal?> LoadAsync(string batchId, CancellationToken cancellationToken) =>
        store.LoadRenderBatchAsync(batchId, cancellationToken);

    public async Task<RenderBatchJournal> PrepareAsync(AiRenderPlanPreparation preparation, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await store.LoadRenderBatchAsync(preparation.BatchId, cancellationToken).ConfigureAwait(false);
            // A new projection must never replace an existing execution or recovery intent.
            if (previous is not null) return previous;
            var journal = new RenderBatchJournal(1, preparation.BatchId, 1, RenderBatchStatus.Prepared, false, false,
                preparation.Pages.Select(page => new RenderBatchPage(page.StyleId, page.PageId, page.OutputVariantId, 1,
                    page.Status == AiRenderPagePreparationStatus.ReadyToRender ? page.RenderPlanJson : null,
                    page.Status == AiRenderPagePreparationStatus.ReadyToRender && page.HasPlan
                        ? RenderBatchPageStatus.Planned : RenderBatchPageStatus.Blocked, page.ReasonCode,
                    PreparationReasonCode: page.ReasonCode, OutputVariantName: page.OutputVariantName)).ToArray());
            RenderBatchJournalValidator.Validate(journal, plans);
            await store.SaveRenderBatchAsync(journal, 0, cancellationToken).ConfigureAwait(false);
            return journal;
        }
        finally { _writes.Release(); }
    }

    public async Task RequestPauseAsync(string batchId, CancellationToken cancellationToken) =>
        _ = await Change(batchId, journal => journal with { PauseRequested = true }, cancellationToken).ConfigureAwait(false);

    public async Task RequestCancelAsync(string batchId, CancellationToken cancellationToken) =>
        _ = await Change(batchId, journal => journal with { CancelRequested = true }, cancellationToken).ConfigureAwait(false);

    public async Task<RenderBatchJournal> PrepareRegenerationAsync(string batchId, string styleId, string pageId,
        string? outputVariantId, int expectedAttempt, CancellationToken cancellationToken)
    {
        if (!await _run.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("RENDER_BATCH_BUSY");
        try
        {
            await RequireRegenerationAdmissionAsync(batchId, styleId, pageId, outputVariantId, expectedAttempt,
                cancellationToken).ConfigureAwait(false);
            return await Change(batchId, journal =>
            {
                var page = journal.Pages.SingleOrDefault(item => item.StyleId == styleId && item.PageId == pageId
                    && item.OutputVariantId == outputVariantId && item.Attempt == expectedAttempt);
                if (page is null || !RenderBatchJournalValidator.CanRegenerate(journal, page))
                    throw new InvalidOperationException("RENDER_REGENERATION_NOT_ALLOWED");
                var added = page with { Attempt = checked(page.Attempt + 1),
                    PlanJson = RenderPlanRegeneration.Create(page.PlanJson!, time.GetUtcNow()),
                    Status = RenderBatchPageStatus.Planned, ReasonCode = page.PreparationReasonCode!, ExecutionId = null, Commit = null,
                    SizeTableRepair = null };
                return journal with { Version = Math.Max(2, journal.Version), Status = RenderBatchStatus.Prepared, PauseRequested = false, CancelRequested = false,
                    RegenerationPageIndex = journal.Pages.Count, Pages = [.. journal.Pages, added] };
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _run.Release(); }
    }

    public async Task<RenderBatchJournal> RunAsync(string batchId, CancellationToken cancellationToken)
    {
        using var performance = RenderPerformance.StartBatch("batch.run", batchId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        if (!await _run.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("RENDER_BATCH_BUSY");
        var active = -1;
        var sessionOpened = false;
        try
        {
            var journal = await Require(batchId, cancellationToken).ConfigureAwait(false);
            if (journal.Status == RenderBatchStatus.Cancelled) return journal;
            var publicOutput = publicOutputs is not null && await publicOutputs.UsesPublicOutputAsync(batchId, cancellationToken).ConfigureAwait(false);
            await EnsureTask(batchId, cancellationToken).ConfigureAwait(false);
            // Public-copy/rename recovery uses already verified internal receipts, not a Photoshop
            // connection or another execution. Uncertain/submitting/planned pages never take this path.
            if (publicOutput && journal.Pages.All(page => page.Status is RenderBatchPageStatus.Committed
                    or RenderBatchPageStatus.Blocked or RenderBatchPageStatus.Failed or RenderBatchPageStatus.Cancelled))
            {
                await publicOutputs!.PublishAsync(batchId, journal, cancellationToken).ConfigureAwait(false);
                return await Finish(batchId, cancellationToken).ConfigureAwait(false);
            }
            await using var session = await sessions.OpenAsync(journal, cancellationToken).ConfigureAwait(false);
            sessionOpened = true;
            // All earlier effects are reconciled before a new page can be delivered.
            for (var i = 0; i < journal.Pages.Count; i++)
            {
                var page = journal.Pages[i];
                if (page.Status is RenderBatchPageStatus.Planned or RenderBatchPageStatus.Blocked) continue;
                active = i;
                var observed = await session.ObserveAsync(page, recovering: true, cancellationToken).ConfigureAwait(false);
                if (observed.Status is not (RenderBatchPageStatus.Committed or RenderBatchPageStatus.Failed or RenderBatchPageStatus.Cancelled))
                    return await StopUnknown(batchId, i, observed, cancellationToken).ConfigureAwait(false);
                journal = await Apply(batchId, i, observed, cancellationToken).ConfigureAwait(false);
            }
            active = -1;
            if (publicOutput) await publicOutputs!.PublishAsync(batchId, journal, cancellationToken).ConfigureAwait(false);
            if (!journal.Pages.Where((_, index) => InScope(journal, index)).Any(page => page.Status == RenderBatchPageStatus.Planned))
                return await Finish(batchId, cancellationToken).ConfigureAwait(false);
            journal = await Change(batchId, value => value with { Status = RenderBatchStatus.Running, PauseRequested = false }, cancellationToken).ConfigureAwait(false);
            await SetTask(batchId, TaskEventType.RenderingStarted, cancellationToken).ConfigureAwait(false);
            string? currentStyle = null;
            for (var i = 0; i < journal.Pages.Count; i++)
            {
                journal = await Require(batchId, cancellationToken).ConfigureAwait(false);
                var page = journal.Pages[i];
                if (page.Status != RenderBatchPageStatus.Planned || !InScope(journal, i)) continue;
                if (journal.CancelRequested) return await Finish(batchId, cancellationToken).ConfigureAwait(false);
                if (journal.PauseRequested && currentStyle != page.StyleId)
                    return await Pause(batchId, cancellationToken).ConfigureAwait(false);
                using var pagePerformance = RenderPerformance.Start("page.run", page.PageId);
                using (RenderPerformance.Start("page.wait_ready"))
                {
                while (!await session.IsReadyAsync(cancellationToken).ConfigureAwait(false))
                {
                    var control = await Require(batchId, cancellationToken).ConfigureAwait(false);
                    if (control.CancelRequested) return await Finish(batchId, cancellationToken).ConfigureAwait(false);
                    if (control.PauseRequested && currentStyle != page.StyleId)
                        return await Pause(batchId, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMilliseconds(250), time, cancellationToken).ConfigureAwait(false);
                }
                }
                // Readiness may await the host while a control request is durably recorded.
                // A style begins only when its first page is about to be delivered.
                journal = await Require(batchId, cancellationToken).ConfigureAwait(false);
                if (journal.CancelRequested) return await Finish(batchId, cancellationToken).ConfigureAwait(false);
                if (journal.PauseRequested && currentStyle != page.StyleId)
                    return await Pause(batchId, cancellationToken).ConfigureAwait(false);
                currentStyle = page.StyleId;
                active = i;
                journal = await Apply(batchId, i, new(RenderBatchPageStatus.Submitting, "RENDER_SUBMITTING"), cancellationToken).ConfigureAwait(false);
                var observed = await session.SubmitAsync(journal.Pages[i], cancellationToken).ConfigureAwait(false);
                journal = await Apply(batchId, i, observed, cancellationToken).ConfigureAwait(false);
                while (observed.Status == RenderBatchPageStatus.Running)
                {
                    var control = await Require(batchId, cancellationToken).ConfigureAwait(false);
                    if (control.CancelRequested) await session.CancelAsync(control.Pages[i], cancellationToken).ConfigureAwait(false);
                    observed = await session.ObserveAsync(control.Pages[i], recovering: false, cancellationToken).ConfigureAwait(false);
                    if (observed.Status == RenderBatchPageStatus.Running)
                        await Task.Delay(TimeSpan.FromMilliseconds(250), time, cancellationToken).ConfigureAwait(false);
                }
                if (observed.Status is not (RenderBatchPageStatus.Committed or RenderBatchPageStatus.Failed or RenderBatchPageStatus.Cancelled))
                    return await StopUnknown(batchId, i, observed, cancellationToken).ConfigureAwait(false);
                journal = await Apply(batchId, i, observed, cancellationToken).ConfigureAwait(false);
                active = -1;
                if (publicOutput) await publicOutputs!.PublishAsync(batchId, journal, cancellationToken).ConfigureAwait(false);
            }
            return await Finish(batchId, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException error) when (!sessionOpened
            && error.Message is "PHOTOSHOP_HOST_SELECTION_REQUIRED" or "PHOTOSHOP_HOST_SELECTION_INVALID" or "RENDER_OUTPUT_PREFLIGHT_REQUIRED")
        {
            // An explicit local precondition failed before a session existed. No execution was sent;
            // preserve Prepared and every pre-existing Unknown instead of fabricating a recovery event.
            throw;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or OperationCanceledException or System.Data.Common.DbException)
        {
            // Lost save/response is never an authorization to resend, including cancellation of the caller.
            try
            {
                return await StopUnknown(batchId, active, new(RenderBatchPageStatus.Unknown, "RENDER_RECOVERY_REQUIRED"), CancellationToken.None).ConfigureAwait(false);
            }
            catch { throw new InvalidOperationException("RENDER_RECOVERY_REQUIRED"); }
        }
        finally { _run.Release(); }
    }

    private async Task RequireRegenerationAdmissionAsync(string batchId, string styleId, string pageId,
        string? outputVariantId, int expectedAttempt, CancellationToken token)
    {
        if (publicOutputs is null || !await publicOutputs.UsesPublicOutputAsync(batchId, token).ConfigureAwait(false)) return;

        // Public output remains immutable after publication. The only exception is a narrowly
        // admitted recovery of a terminal Failed page before its style has been published.
        if (!await publicOutputs.CanRecoverFailedPageAsync(batchId, styleId, pageId, outputVariantId, expectedAttempt, token)
            .ConfigureAwait(false))
            throw new InvalidOperationException("RENDER_PUBLIC_OUTPUT_EDITING_NOT_AVAILABLE");
    }

    private async Task RequireLegacyEditing(string batchId, CancellationToken token)
    {
        if (publicOutputs is not null && await publicOutputs.UsesPublicOutputAsync(batchId, token).ConfigureAwait(false))
            throw new InvalidOperationException("RENDER_PUBLIC_OUTPUT_EDITING_NOT_AVAILABLE");
    }

    private async Task<RenderBatchJournal> StopUnknown(string batchId, int index, RenderPageObservation observed, CancellationToken token)
    {
        if (index >= 0)
        {
            var current = await Require(batchId, token).ConfigureAwait(false);
            if (current.Pages[index].Status is not (RenderBatchPageStatus.Committed or RenderBatchPageStatus.Failed or RenderBatchPageStatus.Cancelled))
                await Apply(batchId, index, observed with { Status = RenderBatchPageStatus.Unknown, Commit = null }, token).ConfigureAwait(false);
        }
        var journal = await Change(batchId, value => value with { Status = RenderBatchStatus.RecoveryRequired }, token).ConfigureAwait(false);
        await SetTask(batchId, TaskEventType.RecoveryRequired, token).ConfigureAwait(false);
        return journal;
    }

    private async Task<RenderBatchJournal> Pause(string batchId, CancellationToken token)
    {
        var journal = await Change(batchId, value => value with { Status = RenderBatchStatus.Paused }, token).ConfigureAwait(false);
        await SetTask(batchId, TaskEventType.PauseRequested, token).ConfigureAwait(false);
        return journal;
    }

    private async Task<RenderBatchJournal> Finish(string batchId, CancellationToken token)
    {
        var journal = await Require(batchId, token).ConfigureAwait(false);
        var latestPages = journal.Pages.GroupBy(page => (page.StyleId, page.PageId, page.OutputVariantId))
            .Select(group => group.MaxBy(page => page.Attempt)!);
        var issues = latestPages.Count(page => page.Status is RenderBatchPageStatus.Blocked or RenderBatchPageStatus.Failed or RenderBatchPageStatus.Cancelled
            || journal.Version >= 2 && page.Status == RenderBatchPageStatus.Planned);
        var task = await tasks.LoadAsync(batchId, token).ConfigureAwait(false);
        // Earlier issue events remain audit history even after a successful new attempt.
        issues = Math.Max(issues, task.IssueCount);
        if (!task.IsTerminal)
        {
            while (task.IssueCount < issues)
                task = (await tasks.RecordAsync(TaskEvent.Create(batchId, task.LastSequence + 1, TaskEventType.IssueRecorded), token).ConfigureAwait(false)).State;
            var kind = journal.CancelRequested ? TaskEventType.Cancelled
                : journal.Pages.All(page => page.Status != RenderBatchPageStatus.Committed) ? TaskEventType.UserActionRequired
                : issues > 0 ? TaskEventType.CompletedWithIssues : TaskEventType.Completed;
            await SetTask(batchId, kind, token).ConfigureAwait(false);
        }
        return await Change(batchId, value => value with
        {
            Status = value.CancelRequested ? RenderBatchStatus.Cancelled
                : value.Pages.All(page => page.Status != RenderBatchPageStatus.Committed) ? RenderBatchStatus.Paused
                : issues > 0 ? RenderBatchStatus.CompletedWithIssues : RenderBatchStatus.Completed
        }, token).ConfigureAwait(false);
    }

    private static bool InScope(RenderBatchJournal journal, int index) => journal.RegenerationPageIndex is null
        || journal.RegenerationPageIndex == index;

    private Task<RenderBatchJournal> Apply(string batchId, int index, RenderPageObservation observed, CancellationToken token) =>
        Change(batchId, journal => journal with { Pages = journal.Pages.Select((page, i) => i != index ? page : page with
        {
            Status = observed.Status, ReasonCode = observed.ReasonCode, ExecutionId = observed.ExecutionId ?? page.ExecutionId,
            Commit = observed.Commit ?? page.Commit
        }).ToArray() }, token);

    private async Task<RenderBatchJournal> Change(string batchId, Func<RenderBatchJournal, RenderBatchJournal> change, CancellationToken token)
    {
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current = await Require(batchId, token).ConfigureAwait(false);
            var next = change(current) with { Revision = current.Revision + 1 };
            RenderBatchJournalValidator.Validate(next, plans);
            RenderBatchJournalValidator.ValidateUpdate(current, next);
            await store.SaveRenderBatchAsync(next, current.Revision, token).ConfigureAwait(false);
            return next;
        }
        finally { _writes.Release(); }
    }

    private async Task<RenderBatchJournal> Require(string batchId, CancellationToken token) =>
        await store.LoadRenderBatchAsync(batchId, token).ConfigureAwait(false) ?? throw new InvalidOperationException("RENDER_BATCH_NOT_FOUND");

    private async Task EnsureTask(string batchId, CancellationToken token)
    {
        var task = await tasks.LoadAsync(batchId, token).ConfigureAwait(false);
        if (task.Status == TaskStatus.None) await SetTask(batchId, TaskEventType.TaskCreated, token).ConfigureAwait(false);
        task = await tasks.LoadAsync(batchId, token).ConfigureAwait(false);
        if (task.Status is TaskStatus.Draft or TaskStatus.UserActionRequired or TaskStatus.SystemBlocked)
            await SetTask(batchId, TaskEventType.ReadyToRender, token).ConfigureAwait(false);
    }

    private async Task SetTask(string batchId, TaskEventType kind, CancellationToken token)
    {
        var task = await tasks.LoadAsync(batchId, token).ConfigureAwait(false);
        if (task.IsTerminal || task.Status.ToString() == kind.ToString()
            || kind == TaskEventType.RenderingStarted && task.Status == TaskStatus.Rendering
            || kind == TaskEventType.PauseRequested && task.Status == TaskStatus.Paused) return;
        await tasks.RecordAsync(TaskEvent.Create(batchId, task.LastSequence + 1, kind), token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _run.WaitAsync().ConfigureAwait(false);
        await _writes.WaitAsync().ConfigureAwait(false);
        _writes.Dispose(); _run.Dispose(); _lifetime.Dispose();
    }
}
