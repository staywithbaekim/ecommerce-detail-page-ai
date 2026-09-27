using Desktop.Application.Inputs;
using Desktop.Application.Tasks;
using UiBatchSelectionMode = Desktop.App.BatchSelectionMode;

namespace Desktop.App;

public sealed class BatchInputWorkflowAdapter(BatchInputWorkflowFacade workflow, TaskCoordinator tasks) : IBatchInputWorkflow
{
    public async Task<IReadOnlyList<BatchRecoverySummary>> ListRecoverableAsync(
        CancellationToken cancellationToken)
    {
        var drafts = await workflow.ListRecoverableAsync(cancellationToken).ConfigureAwait(false);
        var summaries = new List<BatchRecoverySummary>(drafts.Count);
        foreach (var draft in drafts.OrderByDescending(draft => draft.UpdatedAt)
                     .ThenBy(draft => draft.BatchId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var summary = new BatchRecoverySummary(draft.BatchId, draft.TemplateGroupId,
                draft.InputRoot, Project(draft).Status, draft.UpdatedAt);
            try
            {
                summary = summary with
                {
                    Task = await tasks.GetProjectionAsync(draft.BatchId, cancellationToken).ConfigureAwait(false)
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // A damaged event stream must not turn into a harmless "scanned"
                // status or hide every other batch. Never enter AI/render recovery here.
                summary = summary with { TaskStateUnavailable = true };
            }
            summaries.Add(summary);
        }
        return summaries.OrderByDescending(summary => summary.LatestUpdatedAt)
            .ThenBy(summary => summary.BatchId, StringComparer.Ordinal).ToArray();
    }

    public async Task<BatchInputProjection?> RestoreBatchAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        var drafts = await workflow.ListRecoverableAsync(cancellationToken).ConfigureAwait(false);
        var draft = drafts.FirstOrDefault(candidate =>
            string.Equals(candidate.BatchId, batchId, StringComparison.Ordinal));
        return draft is null ? null : ProjectRestored(draft);
    }

    public async Task<BatchInputProjection?> RestoreLatestDraftAsync(
        CancellationToken cancellationToken)
    {
        var drafts = await workflow.ListRecoverableAsync(cancellationToken).ConfigureAwait(false);
        var draft = drafts
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .ThenBy(candidate => candidate.BatchId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (draft is null)
        {
            return null;
        }

        return ProjectRestored(draft);
    }

    public async Task<BatchInputProjection> ScanAsync(
        BatchInputSubmission submission,
        CancellationToken cancellationToken)
    {
        var scanned = await workflow.ScanAsync(ToCommand(submission), cancellationToken)
            .ConfigureAwait(false);
        return Project(scanned);
    }

    public async Task<BatchInputProjection> SaveDraftAsync(
        BatchInputSubmission submission,
        CancellationToken cancellationToken)
    {
        var draft = await workflow.SaveDraftAsync(ToCommand(submission), cancellationToken)
            .ConfigureAwait(false);
        return Project(draft);
    }

    private static BatchInputProjection ProjectRestored(BatchInputWorkflowState draft)
    {
        var projection = Project(draft);
        return draft.TemplateGroupId is null
            ? projection with
            {
                Status = BatchInputUiStatus.ScanBlocked,
                StatusMessage = "原批次使用的模板已不可用。请选择一个已校准模板后重新扫描。"
            }
            : projection;
    }

    private static BatchInputWorkflowCommand ToCommand(BatchInputSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return new BatchInputWorkflowCommand(
            submission.BatchId,
            submission.TemplateGroupId,
            submission.InputDirectory,
            submission.OutputMode is null ? submission.OutputDirectory : "",
            submission.SelectedPageIds,
            submission.SelectionMode == UiBatchSelectionMode.Automatic
                ? BatchInputWorkflowSelectionMode.Automatic
                : BatchInputWorkflowSelectionMode.Manual,
            submission.IncludeEditablePsd,
            submission.OutputMode switch
            {
                null => null,
                BatchOutputMode.UnifiedDirectory => new(BatchInputWorkflowOutputMode.UnifiedDirectory, submission.OutputDirectory),
                BatchOutputMode.StyleSourceDirectories => new(BatchInputWorkflowOutputMode.StyleSourceDirectories, null),
                _ => throw new InvalidOperationException("INPUT_PUBLIC_OUTPUT_CONFIGURATION_INVALID")
            });
    }

    private static BatchInputProjection Project(BatchInputWorkflowState draft)
    {
        var recognitionText = draft.InputType switch
        {
            BatchInputWorkflowType.SingleStyle => "已识别为单款批次 · 1 款",
            BatchInputWorkflowType.StyleDirectoryBatch => $"已识别为正常批次 · {draft.StyleCount} 款",
            _ => "目录已选择；等待扫描识别。"
        };
        var status = draft.Status switch
        {
            BatchInputWorkflowStatus.Draft => BatchInputUiStatus.Draft,
            BatchInputWorkflowStatus.Scanning => BatchInputUiStatus.Scanning,
            BatchInputWorkflowStatus.ScanReady => BatchInputUiStatus.ScanReady,
            BatchInputWorkflowStatus.ScanBlocked => BatchInputUiStatus.ScanBlocked,
            _ => throw new InvalidOperationException("INPUT_SCAN_STATUS_UNSUPPORTED")
        };
        var statusMessage = draft.Status switch
        {
            BatchInputWorkflowStatus.Draft => "草稿已自动保存。",
            BatchInputWorkflowStatus.Scanning => "正在只读扫描商品目录；此阶段不会修改源图片，也不占用 Photoshop。",
            BatchInputWorkflowStatus.ScanReady =>
                $"扫描完成，共识别 {draft.StyleCount} 款、{draft.AssetCount} 张商品图片，可进入后续分析。",
            BatchInputWorkflowStatus.ScanBlocked => draft.BlockUserAction
                ?? "扫描未能安全完成，请检查目录和本地存储后重试。",
            _ => throw new InvalidOperationException("INPUT_SCAN_STATUS_UNSUPPORTED")
        };

        return new BatchInputProjection(
            draft.BatchId,
            draft.TemplateGroupId,
            status,
            draft.InputRoot,
            draft.PublicOutput is null ? draft.OutputRoot : draft.PublicOutput.UnifiedRoot ?? "",
            draft.SelectedPageIds,
            draft.SelectionMode == BatchInputWorkflowSelectionMode.Automatic
                ? UiBatchSelectionMode.Automatic
                : UiBatchSelectionMode.Manual,
            draft.IncludeEditablePsd,
            recognitionText,
            statusMessage,
            draft.PublicOutput?.Mode switch
            {
                null => null,
                BatchInputWorkflowOutputMode.UnifiedDirectory => BatchOutputMode.UnifiedDirectory,
                BatchInputWorkflowOutputMode.StyleSourceDirectories => BatchOutputMode.StyleSourceDirectories,
                _ => throw new InvalidOperationException("INPUT_PUBLIC_OUTPUT_CONFIGURATION_INVALID")
            });
    }
}
