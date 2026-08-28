namespace Desktop.Application.Inputs;

public enum BatchInputWorkflowStatus
{
    Draft = 0,
    Scanning,
    ScanReady,
    ScanBlocked
}

public enum BatchInputWorkflowType
{
    SingleStyle = 0,
    StyleDirectoryBatch
}

public enum BatchInputWorkflowSelectionMode
{
    Automatic = 0,
    Manual
}

public sealed record BatchInputWorkflowCommand(
    string? BatchId,
    string TemplateGroupId,
    string InputRoot,
    string OutputRoot,
    IReadOnlyList<string> SelectedPageIds,
    BatchInputWorkflowSelectionMode SelectionMode,
    bool IncludeEditablePsd);

public sealed record BatchInputWorkflowState(
    string BatchId,
    string? TemplateGroupId,
    BatchInputWorkflowStatus Status,
    BatchInputWorkflowType? InputType,
    string InputRoot,
    string OutputRoot,
    IReadOnlyList<string> SelectedPageIds,
    BatchInputWorkflowSelectionMode SelectionMode,
    bool IncludeEditablePsd,
    int StyleCount,
    int AssetCount,
    string? BlockUserAction,
    DateTimeOffset UpdatedAt);

public sealed class BatchInputWorkflowFacade(BatchInputService service)
{
    public async Task<BatchInputWorkflowState> SaveDraftAsync(
        BatchInputWorkflowCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var batchId = command.BatchId ?? $"batch-{Guid.NewGuid():N}";
        var draft = await service.CreateDraftFromTemplateAsync(
            new CreateBatchFromTemplateCommand(
                batchId,
                command.TemplateGroupId,
                command.InputRoot,
                command.OutputRoot,
                command.SelectedPageIds,
                command.SelectionMode == BatchInputWorkflowSelectionMode.Automatic
                    ? Domain.Inputs.BatchSelectionMode.Automatic
                    : Domain.Inputs.BatchSelectionMode.Manual,
                command.IncludeEditablePsd),
            cancellationToken).ConfigureAwait(false);
        return await ProjectAsync(draft, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BatchInputWorkflowState> ScanAsync(
        BatchInputWorkflowCommand command,
        CancellationToken cancellationToken)
    {
        var saved = await SaveDraftAsync(command, cancellationToken).ConfigureAwait(false);
        var scanned = await service.ScanAsync(saved.BatchId, cancellationToken).ConfigureAwait(false);
        return await ProjectAsync(scanned, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BatchInputWorkflowState>> ListRecoverableAsync(
        CancellationToken cancellationToken)
    {
        var drafts = await service.ListRecoverableAsync(cancellationToken).ConfigureAwait(false);
        var states = new List<BatchInputWorkflowState>(drafts.Count);
        foreach (var draft in drafts)
        {
            states.Add(await ProjectAsync(draft, cancellationToken).ConfigureAwait(false));
        }

        return states;
    }

    private async Task<BatchInputWorkflowState> ProjectAsync(
        Domain.Inputs.BatchDraft draft,
        CancellationToken cancellationToken)
    {
        var templateGroupId = await service.ResolveTemplateGroupIdAsync(draft, cancellationToken)
            .ConfigureAwait(false);
        return new BatchInputWorkflowState(
            draft.BatchId,
            templateGroupId,
            draft.ScanStatus switch
            {
                Domain.Inputs.BatchScanStatus.Draft => BatchInputWorkflowStatus.Draft,
                Domain.Inputs.BatchScanStatus.Scanning => BatchInputWorkflowStatus.Scanning,
                Domain.Inputs.BatchScanStatus.ScanReady => BatchInputWorkflowStatus.ScanReady,
                Domain.Inputs.BatchScanStatus.ScanBlocked => BatchInputWorkflowStatus.ScanBlocked,
                _ => throw new InvalidOperationException("INPUT_SCAN_STATUS_UNSUPPORTED")
            },
            draft.InputType switch
            {
                Domain.Inputs.BatchInputType.SingleStyle => BatchInputWorkflowType.SingleStyle,
                Domain.Inputs.BatchInputType.StyleDirectoryBatch => BatchInputWorkflowType.StyleDirectoryBatch,
                null => null,
                _ => throw new InvalidOperationException("INPUT_TYPE_UNSUPPORTED")
            },
            draft.InputRoot,
            draft.OutputRoot,
            draft.TemplateSnapshot.Pages
                .OrderBy(page => page.Order)
                .Select(page => page.PageId)
                .ToArray(),
            draft.SelectionMode == Domain.Inputs.BatchSelectionMode.Automatic
                ? BatchInputWorkflowSelectionMode.Automatic
                : BatchInputWorkflowSelectionMode.Manual,
            draft.IncludeEditablePsd,
            draft.Styles.Length,
            draft.Assets.Length,
            draft.BlockReason?.UserAction,
            draft.UpdatedAt);
    }
}
