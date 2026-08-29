using Desktop.Application.Inputs;
using UiBatchSelectionMode = Desktop.App.BatchSelectionMode;

namespace Desktop.App;

public sealed class BatchInputWorkflowAdapter(BatchInputWorkflowFacade workflow) : IBatchInputWorkflow
{
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

        var projection = Project(draft);
        return draft.TemplateGroupId is null
            ? projection with
            {
                Status = BatchInputUiStatus.ScanBlocked,
                StatusMessage = "原批次使用的模板已不可用。请选择一个已校准模板后重新扫描。"
            }
            : projection;
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

    private static BatchInputWorkflowCommand ToCommand(BatchInputSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return new BatchInputWorkflowCommand(
            submission.BatchId,
            submission.TemplateGroupId,
            submission.InputDirectory,
            submission.OutputDirectory,
            submission.SelectedPageIds,
            submission.SelectionMode == UiBatchSelectionMode.Automatic
                ? BatchInputWorkflowSelectionMode.Automatic
                : BatchInputWorkflowSelectionMode.Manual,
            submission.IncludeEditablePsd);
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
            draft.OutputRoot,
            draft.SelectedPageIds,
            draft.SelectionMode == BatchInputWorkflowSelectionMode.Automatic
                ? UiBatchSelectionMode.Automatic
                : UiBatchSelectionMode.Manual,
            draft.IncludeEditablePsd,
            recognitionText,
            statusMessage);
    }
}
